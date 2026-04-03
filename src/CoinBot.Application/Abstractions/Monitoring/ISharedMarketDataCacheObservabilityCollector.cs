using CoinBot.Application.Abstractions.MarketData;

namespace CoinBot.Application.Abstractions.Monitoring;

public interface ISharedMarketDataCacheObservabilityCollector
{
    void RecordRead<TPayload>(
        SharedMarketDataCacheDataType dataType,
        string symbol,
        string? timeframe,
        SharedMarketDataCacheReadResult<TPayload> result);

    void RecordProjection(
        SharedMarketDataCacheDataType dataType,
        string symbol,
        string? timeframe,
        SharedMarketDataProjectionResult result,
        DateTime? updatedAtUtc,
        DateTime? freshUntilUtc,
        string? sourceLayer);

    SharedMarketDataCacheHealthSnapshot GetSnapshot(DateTime utcNow);
}
