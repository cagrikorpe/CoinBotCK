namespace CoinBot.Application.Abstractions.MarketData;

public interface IMarketDataService
{
    ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default);

    ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default);

    ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default);

    async ValueTask<SharedMarketDataCacheReadResult<MarketPriceSnapshot>> ReadLatestPriceAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetLatestPriceAsync(symbol, cancellationToken);
        if (snapshot is null)
        {
            return SharedMarketDataCacheReadResult<MarketPriceSnapshot>.Miss(
                "Legacy market-data implementation did not return a latest ticker snapshot.");
        }

        return SharedMarketDataCacheReadResult<MarketPriceSnapshot>.HitFresh(
            new SharedMarketDataCacheEntry<MarketPriceSnapshot>(
                SharedMarketDataCacheDataType.Ticker,
                snapshot.Symbol,
                Timeframe: null,
                UpdatedAtUtc: snapshot.ObservedAtUtc,
                CachedAtUtc: snapshot.ReceivedAtUtc,
                FreshUntilUtc: snapshot.ReceivedAtUtc,
                ExpiresAtUtc: snapshot.ReceivedAtUtc,
                Source: snapshot.Source,
                Payload: snapshot));
    }

    ValueTask<SharedMarketDataCacheReadResult<MarketCandleSnapshot>> ReadLatestKlineAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            SharedMarketDataCacheReadResult<MarketCandleSnapshot>.Miss(
                "Legacy market-data implementation does not expose shared kline snapshots."));
    }

    ValueTask<SharedMarketDataCacheReadResult<MarketDepthSnapshot>> ReadLatestDepthAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            SharedMarketDataCacheReadResult<MarketDepthSnapshot>.Miss(
                "Legacy market-data implementation does not expose shared depth snapshots."));
    }

    ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default);

    IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default);
}
