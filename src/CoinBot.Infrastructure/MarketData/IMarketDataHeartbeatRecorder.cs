namespace CoinBot.Infrastructure.MarketData;

public interface IMarketDataHeartbeatRecorder
{
    Task RecordAsync(
        CandleDataQualityGuardResult guardResult,
        CancellationToken cancellationToken = default);
}
