using CoinBot.Application.Abstractions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.MarketData;

public sealed class MarketDataHeartbeatRecorder(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<BinanceMarketDataOptions> options,
    TimeProvider timeProvider,
    ILogger<MarketDataHeartbeatRecorder> logger) : IMarketDataHeartbeatRecorder
{
    private readonly TimeSpan minimumInterval = TimeSpan.FromSeconds(options.Value.HeartbeatPersistenceIntervalSeconds);
    private readonly object syncRoot = new();
    private DateTime lastPersistedAtUtc = DateTime.MinValue;

    public async Task RecordAsync(
        CandleDataQualityGuardResult guardResult,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(guardResult);

        if (guardResult.IsAccepted && !ShouldPersistHeartbeat())
        {
            return;
        }

        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var circuitBreaker = scope.ServiceProvider.GetRequiredService<IDataLatencyCircuitBreaker>();

            await circuitBreaker.RecordHeartbeatAsync(
                new DataLatencyHeartbeat(
                    Source: "binance:kline",
                    DataTimestampUtc: NormalizeTimestamp(guardResult.EffectiveDataTimestampUtc),
                    GuardStateCode: guardResult.GuardStateCode,
                    GuardReasonCode: guardResult.GuardReasonCode,
                    Symbol: guardResult.Symbol,
                    Timeframe: guardResult.Timeframe,
                    ExpectedOpenTimeUtc: guardResult.ExpectedOpenTimeUtc,
                    ContinuityGapCount: guardResult.ContinuityGapCount),
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Market data heartbeat persistence failed for guard reason {GuardReasonCode}.",
                guardResult.GuardReasonCode);
        }
    }

    private bool ShouldPersistHeartbeat()
    {
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;

        lock (syncRoot)
        {
            if (lastPersistedAtUtc != DateTime.MinValue &&
                utcNow - lastPersistedAtUtc < minimumInterval)
            {
                return false;
            }

            lastPersistedAtUtc = utcNow;
            return true;
        }
    }

    private static DateTime NormalizeTimestamp(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
