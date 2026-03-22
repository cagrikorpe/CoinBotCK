using CoinBot.Application.Abstractions.MarketData;
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
    ILogger<BinanceWebSocketManager> logger) : BackgroundService
{
    private static readonly TimeSpan InterruptionPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);
    private readonly BinanceMarketDataOptions optionsValue = options.Value;
    private readonly TimeSpan exchangeInfoRefreshInterval = TimeSpan.FromMinutes(options.Value.ExchangeInfoRefreshIntervalMinutes);
    private readonly TimeSpan reconnectDelay = TimeSpan.FromSeconds(options.Value.ReconnectDelaySeconds);
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
                logger.LogWarning(
                    exception,
                    "Binance market data plane cycle failed. Reconnecting after a backoff.");

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
        await using var enumerator = candleStreamClient
            .StreamAsync(trackedSymbols.Symbols, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            var moveNextTask = enumerator.MoveNextAsync().AsTask();
            var completedTask = await Task.WhenAny(
                moveNextTask,
                Task.Delay(InterruptionPollInterval, cancellationToken));

            if (completedTask != moveNextTask)
            {
                if (ShouldRestartStream(trackedSymbols.Version))
                {
                    return;
                }

                continue;
            }

            if (!await moveNextTask)
            {
                return;
            }

            var snapshot = enumerator.Current;

            if (!snapshot.IsClosed)
            {
                continue;
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

                logger.LogWarning(
                    "Candle data quality guard blocked {Symbol} {Interval} with reason {ReasonCode}.",
                    snapshot.Symbol,
                    snapshot.Interval,
                    guardResult.GuardReasonCode);
            }

            await heartbeatRecorder.RecordAsync(guardResult, cancellationToken);

            if (ShouldRestartStream(trackedSymbols.Version))
            {
                return;
            }
        }
    }

    private bool ShouldRestartStream(long trackedSymbolVersion)
    {
        return symbolRegistry.GetTrackedSymbolsSnapshot().Version != trackedSymbolVersion ||
               timeProvider.GetUtcNow().UtcDateTime >= nextMetadataRefreshAtUtc;
    }
}
