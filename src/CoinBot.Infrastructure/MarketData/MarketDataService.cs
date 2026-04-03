using System.Runtime.CompilerServices;
using CoinBot.Application.Abstractions.MarketData;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.MarketData;

public sealed class MarketDataService(
    SharedSymbolRegistry symbolRegistry,
    IMemoryCache memoryCache,
    MarketDataCachePolicyProvider cachePolicyProvider,
    MarketPriceStreamHub streamHub,
    ISharedMarketDataCache sharedMarketDataCache,
    TimeProvider timeProvider,
    ILogger<MarketDataService> logger) : IMarketDataService
{
    public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default)
    {
        return symbolRegistry.TrackSymbolAsync(symbol, cancellationToken);
    }

    public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
    {
        return symbolRegistry.TrackSymbolsAsync(symbols, cancellationToken);
    }

    public async ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSymbol = MarketDataSymbolNormalizer.Normalize(symbol);
        var cacheResult = await sharedMarketDataCache.ReadAsync<MarketPriceSnapshot>(
            SharedMarketDataCacheDataType.Ticker,
            normalizedSymbol,
            timeframe: null,
            cancellationToken);

        if (cacheResult.Status == SharedMarketDataCacheReadStatus.HitFresh)
        {
            return cacheResult.Entry?.Payload;
        }

        logger.LogDebug(
            "Shared market-data ticker cache read for {Symbol} returned {Status} ({ReasonCode}).",
            normalizedSymbol,
            cacheResult.Status,
            cacheResult.ReasonCode);

        return null;
    }

    public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
    {
        return symbolRegistry.GetSymbolAsync(symbol, cancellationToken);
    }

    public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
        IEnumerable<string> symbols,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var normalizedSymbols = MarketDataSymbolNormalizer.NormalizeMany(symbols);

        await TrackSymbolsAsync(normalizedSymbols, cancellationToken);

        await foreach (var snapshot in streamHub.SubscribeAsync(normalizedSymbols, cancellationToken))
        {
            yield return snapshot;
        }
    }

    internal async ValueTask RecordPriceAsync(MarketPriceSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSnapshot = snapshot with
        {
            Symbol = MarketDataSymbolNormalizer.Normalize(snapshot.Symbol),
            ObservedAtUtc = NormalizeTimestamp(snapshot.ObservedAtUtc),
            ReceivedAtUtc = NormalizeTimestamp(snapshot.ReceivedAtUtc),
            Source = snapshot.Source.Trim()
        };

        var cachedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var freshness = cachePolicyProvider.GetLatestPriceFreshness();
        var retention = cachePolicyProvider.GetLatestPriceRetention();
        var cacheResult = await sharedMarketDataCache.WriteAsync(
            new SharedMarketDataCacheEntry<MarketPriceSnapshot>(
                SharedMarketDataCacheDataType.Ticker,
                normalizedSnapshot.Symbol,
                Timeframe: null,
                UpdatedAtUtc: normalizedSnapshot.ReceivedAtUtc,
                CachedAtUtc: cachedAtUtc,
                FreshUntilUtc: normalizedSnapshot.ReceivedAtUtc.Add(freshness),
                ExpiresAtUtc: cachedAtUtc.Add(retention),
                Source: normalizedSnapshot.Source,
                Payload: normalizedSnapshot),
            cancellationToken);

        if (cacheResult.Status != SharedMarketDataCacheWriteStatus.Written)
        {
            logger.LogWarning(
                "Shared market-data ticker cache write for {Symbol} failed closed with {ReasonCode}.",
                normalizedSnapshot.Symbol,
                cacheResult.ReasonCode);

            return;
        }

        streamHub.Publish(normalizedSnapshot);

        logger.LogDebug(
            "Central market data price cache updated for {Symbol} via {Source}.",
            normalizedSnapshot.Symbol,
            normalizedSnapshot.Source);

        return;
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
