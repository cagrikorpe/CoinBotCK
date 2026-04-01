using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Application.Abstractions.Autonomy;
using CoinBot.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.MarketData;

public sealed class BinanceWebSocketManager(
    SharedSymbolRegistry symbolRegistry,
    MarketDataService marketDataService,
    IndicatorDataService indicatorDataService,
    IBinanceExchangeInfoClient exchangeInfoClient,
    IBinanceCandleStreamClient candleStreamClient,
    CandleDataQualityGuard dataQualityGuard,
    IMarketDataHeartbeatRecorder heartbeatRecorder,
    IOptions<BinanceMarketDataOptions> options,
    TimeProvider timeProvider,
    ILogger<BinanceWebSocketManager> logger,
    IMonitoringTelemetryCollector? monitoringTelemetryCollector = null,
    IWebSocketReconnectCoordinator? webSocketReconnectCoordinator = null,
    IServiceScopeFactory? serviceScopeFactory = null) : BackgroundService
{
    private const string BreakerActor = "system:market-data-websocket";
    private static readonly TimeSpan InterruptionPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);
    private readonly BinanceMarketDataOptions optionsValue = options.Value;
    private readonly TimeSpan exchangeInfoRefreshInterval = TimeSpan.FromMinutes(options.Value.ExchangeInfoRefreshIntervalMinutes);
    private readonly TimeSpan reconnectDelay = TimeSpan.FromSeconds(options.Value.ReconnectDelaySeconds);
    private int reconnectCount;
    private int streamGapCount;
    private DateTime? lastMessageReceivedAtUtc;
    private bool seedSymbolsRegistered;
    private DateTime nextMetadataRefreshAtUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!optionsValue.Enabled)
        {
            logger.LogInformation("Binance market data plane is disabled by configuration.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                await RecordBreakerFailureAsync(exception, stoppingToken);

                logger.LogWarning(
                    exception,
                    "Binance market data plane cycle failed. Reconnecting after a backoff.");

                reconnectCount++;
                RecordTelemetry();
                await Task.Delay(reconnectDelay, stoppingToken);
            }
        }
    }

    internal async Task RunCycleAsync(CancellationToken cancellationToken = default)
    {
        if (!seedSymbolsRegistered)
        {
            await marketDataService.TrackSymbolsAsync(optionsValue.SeedSymbols, cancellationToken);
            seedSymbolsRegistered = true;
        }

        var trackedSymbols = symbolRegistry.GetTrackedSymbolsSnapshot();

        if (trackedSymbols.Symbols.Count == 0)
        {
            await Task.Delay(IdleDelay, cancellationToken);
            return;
        }

        if (timeProvider.GetUtcNow().UtcDateTime >= nextMetadataRefreshAtUtc)
        {
            var metadataSnapshots = await exchangeInfoClient.GetSymbolMetadataAsync(
                trackedSymbols.Symbols,
                cancellationToken);

            symbolRegistry.Upsert(metadataSnapshots);
            nextMetadataRefreshAtUtc = timeProvider.GetUtcNow().UtcDateTime.Add(exchangeInfoRefreshInterval);

            logger.LogInformation(
                "Shared symbol registry refreshed for {SymbolCount} symbols.",
                metadataSnapshots.Count);
        }

        await StreamTrackedSymbolsAsync(trackedSymbols, cancellationToken);
    }

    private async Task StreamTrackedSymbolsAsync(
        TrackedSymbolSnapshot trackedSymbols,
        CancellationToken cancellationToken)
    {
        var reconnectGeneration = webSocketReconnectCoordinator?.GetGeneration() ?? 0L;
        var hasRecordedHealthyState = false;
        Task<bool>? pendingMoveNextTask = null;
        await using var enumerator = candleStreamClient
            .StreamAsync(trackedSymbols.Symbols, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            pendingMoveNextTask ??= enumerator.MoveNextAsync().AsTask();
            var completedTask = await Task.WhenAny(
                pendingMoveNextTask,
                Task.Delay(InterruptionPollInterval, cancellationToken));

            if (completedTask != pendingMoveNextTask)
            {
                if (ShouldRestartStream(trackedSymbols.Version, reconnectGeneration))
                {
                    return;
                }

                continue;
            }

            if (!await pendingMoveNextTask)
            {
                return;
            }

            pendingMoveNextTask = null;
            var snapshot = enumerator.Current;
            lastMessageReceivedAtUtc = snapshot.ReceivedAtUtc;

            if (!snapshot.IsClosed)
            {
                RecordTelemetry();
                continue;
            }

            if (!hasRecordedHealthyState &&
                serviceScopeFactory is not null)
            {
                await RecordBreakerSuccessAsync(cancellationToken);
                hasRecordedHealthyState = true;
            }

            var guardResult = dataQualityGuard.Evaluate(snapshot);

            if (guardResult.IsAccepted)
            {
                await marketDataService.RecordPriceAsync(
                    new MarketPriceSnapshot(
                        snapshot.Symbol,
                        snapshot.ClosePrice,
                        snapshot.CloseTimeUtc,
                        snapshot.ReceivedAtUtc,
                        snapshot.Source),
                    cancellationToken);

                await indicatorDataService.RecordAcceptedCandleAsync(snapshot, cancellationToken);
            }
            else
            {
                await indicatorDataService.RecordRejectedCandleAsync(snapshot, guardResult, cancellationToken);

                if (guardResult.GuardReasonCode is DegradedModeReasonCode.CandleDataGapDetected or DegradedModeReasonCode.CandleDataDuplicateDetected or DegradedModeReasonCode.CandleDataOutOfOrderDetected)
                {
                    streamGapCount++;
                }

                logger.LogWarning(
                    "Candle data quality guard blocked {Symbol} {Interval} with reason {ReasonCode}.",
                    snapshot.Symbol,
                    snapshot.Interval,
                    guardResult.GuardReasonCode);
            }

            await heartbeatRecorder.RecordAsync(guardResult, cancellationToken);
            RecordTelemetry();

            if (ShouldRestartStream(trackedSymbols.Version, reconnectGeneration))
            {
                return;
            }
        }
    }

    private bool ShouldRestartStream(long trackedSymbolVersion, long reconnectGeneration)
    {
        return symbolRegistry.GetTrackedSymbolsSnapshot().Version != trackedSymbolVersion ||
               timeProvider.GetUtcNow().UtcDateTime >= nextMetadataRefreshAtUtc ||
               (webSocketReconnectCoordinator?.GetGeneration() ?? reconnectGeneration) != reconnectGeneration;
    }

    private void RecordTelemetry()
    {
        if (monitoringTelemetryCollector is null)
        {
            return;
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var lastMessageAtUtc = lastMessageReceivedAtUtc ?? nowUtc;
        var ageSeconds = lastMessageReceivedAtUtc is null
            ? (int?)null
            : Math.Max(0, (int)Math.Round((nowUtc - lastMessageAtUtc).TotalSeconds, MidpointRounding.AwayFromZero));

        monitoringTelemetryCollector.RecordWebSocketActivity(
            lastMessageAtUtc,
            reconnectCount,
            streamGapCount,
            lastMessageAgeSeconds: ageSeconds,
            staleDurationSeconds: ageSeconds);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }

    private async Task RecordBreakerFailureAsync(Exception exception, CancellationToken cancellationToken)
    {
        if (serviceScopeFactory is null)
        {
            return;
        }

        using var scope = serviceScopeFactory.CreateScope();
        var dependencyCircuitBreakerStateManager = scope.ServiceProvider.GetService<IDependencyCircuitBreakerStateManager>();

        if (dependencyCircuitBreakerStateManager is null)
        {
            return;
        }

        await dependencyCircuitBreakerStateManager.RecordFailureAsync(
            new DependencyCircuitBreakerFailureRequest(
                DependencyCircuitBreakerKind.WebSocket,
                BreakerActor,
                exception.GetType().Name,
                Truncate(exception.Message, 512) ?? "WebSocket cycle failed."),
            cancellationToken);
    }

    private async Task RecordBreakerSuccessAsync(CancellationToken cancellationToken)
    {
        if (serviceScopeFactory is null)
        {
            return;
        }

        using var scope = serviceScopeFactory.CreateScope();
        var dependencyCircuitBreakerStateManager = scope.ServiceProvider.GetService<IDependencyCircuitBreakerStateManager>();

        if (dependencyCircuitBreakerStateManager is null)
        {
            return;
        }

        await dependencyCircuitBreakerStateManager.RecordSuccessAsync(
            new DependencyCircuitBreakerSuccessRequest(
                DependencyCircuitBreakerKind.WebSocket,
                BreakerActor),
            cancellationToken);
    }
}
