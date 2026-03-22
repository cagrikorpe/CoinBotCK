using System.Collections.Concurrent;
using CoinBot.Application.Abstractions.MarketData;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.MarketData;

public sealed class SharedSymbolRegistry(
    IMemoryCache memoryCache,
    MarketDataCachePolicyProvider cachePolicyProvider,
    ILogger<SharedSymbolRegistry> logger) : ISharedSymbolRegistry
{
    private readonly ConcurrentDictionary<string, byte> trackedSymbols = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> cachedSymbols = new(StringComparer.Ordinal);
    private long trackedSymbolVersion;

    public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSymbol = MarketDataSymbolNormalizer.Normalize(symbol);

        if (trackedSymbols.TryAdd(normalizedSymbol, 0))
        {
            Interlocked.Increment(ref trackedSymbolVersion);
            logger.LogInformation("Shared symbol registry started tracking {Symbol}.", normalizedSymbol);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
    {
        foreach (var symbol in MarketDataSymbolNormalizer.NormalizeMany(symbols))
        {
            await TrackSymbolAsync(symbol, cancellationToken);
        }
    }

    public ValueTask<SymbolMetadataSnapshot?> GetSymbolAsync(string symbol, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSymbol = MarketDataSymbolNormalizer.Normalize(symbol);
        memoryCache.TryGetValue(GetSymbolCacheKey(normalizedSymbol), out SymbolMetadataSnapshot? snapshot);

        return ValueTask.FromResult(snapshot);
    }

    public ValueTask<IReadOnlyCollection<SymbolMetadataSnapshot>> ListSymbolsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var snapshots = cachedSymbols.Keys
            .Select(symbol =>
            {
                memoryCache.TryGetValue(GetSymbolCacheKey(symbol), out SymbolMetadataSnapshot? snapshot);
                return snapshot;
            })
            .OfType<SymbolMetadataSnapshot>()
            .OrderBy(snapshot => snapshot.Symbol, StringComparer.Ordinal)
            .ToArray();

        return ValueTask.FromResult<IReadOnlyCollection<SymbolMetadataSnapshot>>(snapshots);
    }

    internal TrackedSymbolSnapshot GetTrackedSymbolsSnapshot()
    {
        var symbols = trackedSymbols.Keys
            .OrderBy(symbol => symbol, StringComparer.Ordinal)
            .ToArray();

        return new TrackedSymbolSnapshot(symbols, Interlocked.Read(ref trackedSymbolVersion));
    }

    internal void Upsert(IReadOnlyCollection<SymbolMetadataSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        foreach (var snapshot in snapshots)
        {
            var normalizedSnapshot = snapshot with
            {
                Symbol = MarketDataSymbolNormalizer.Normalize(snapshot.Symbol),
                Exchange = snapshot.Exchange.Trim(),
                BaseAsset = snapshot.BaseAsset.Trim(),
                QuoteAsset = snapshot.QuoteAsset.Trim(),
                TradingStatus = snapshot.TradingStatus.Trim(),
                RefreshedAtUtc = NormalizeTimestamp(snapshot.RefreshedAtUtc)
            };

            memoryCache.Set(
                GetSymbolCacheKey(normalizedSnapshot.Symbol),
                normalizedSnapshot,
                cachePolicyProvider.CreateSymbolMetadataOptions());

            cachedSymbols.TryAdd(normalizedSnapshot.Symbol, 0);
        }
    }

    private static string GetSymbolCacheKey(string symbol)
    {
        return $"marketdata:symbol:{symbol}";
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
