namespace CoinBot.Application.Abstractions.MarketData;

public interface ISharedMarketDataCache
{
    ValueTask<SharedMarketDataCacheWriteResult> WriteAsync<TPayload>(
        SharedMarketDataCacheEntry<TPayload> entry,
        CancellationToken cancellationToken = default);

    ValueTask<SharedMarketDataCacheReadResult<TPayload>> ReadAsync<TPayload>(
        SharedMarketDataCacheDataType dataType,
        string symbol,
        string? timeframe,
        CancellationToken cancellationToken = default);
}
