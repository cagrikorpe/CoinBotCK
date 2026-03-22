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

    public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSymbol = MarketDataSymbolNormalizer.Normalize(symbol);
        memoryCache.TryGetValue(GetPriceCacheKey(normalizedSymbol), out MarketPriceSnapshot? snapshot);

        return ValueTask.FromResult(snapshot);
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

    internal ValueTask RecordPriceAsync(MarketPriceSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSnapshot = snapshot with
        {
            Symbol = MarketDataSymbolNormalizer.Normalize(snapshot.Symbol),
            ObservedAtUtc = NormalizeTimestamp(snapshot.ObservedAtUtc),
            ReceivedAtUtc = NormalizeTimestamp(snapshot.ReceivedAtUtc),
            Source = snapshot.Source.Trim()
        };

        memoryCache.Set(
            GetPriceCacheKey(normalizedSnapshot.Symbol),
            normalizedSnapshot,
            cachePolicyProvider.CreateLatestPriceOptions());

        streamHub.Publish(normalizedSnapshot);

        logger.LogDebug(
            "Central market data price cache updated for {Symbol} via {Source}.",
            normalizedSnapshot.Symbol,
            normalizedSnapshot.Source);

        return ValueTask.CompletedTask;
    }

    private static string GetPriceCacheKey(string symbol)
    {
        return $"marketdata:price:{symbol}";
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
