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
        var cacheResult = await ReadLatestPriceAsync(symbol, cancellationToken);
        return cacheResult.Status == SharedMarketDataCacheReadStatus.HitFresh
            ? cacheResult.Entry?.Payload
            : null;
    }

    public async ValueTask<SharedMarketDataCacheReadResult<MarketPriceSnapshot>> ReadLatestPriceAsync(
        string symbol,
        CancellationToken cancellationToken = default)
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
            return cacheResult;
        }

        logger.LogDebug(
            "Shared market-data ticker cache read for {Symbol} returned {Status} ({ReasonCode}).",
            normalizedSymbol,
            cacheResult.Status,
            cacheResult.ReasonCode);

        return cacheResult;
    }

    internal async ValueTask<MarketCandleSnapshot?> GetLatestKlineAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken = default)
    {
        var cacheResult = await ReadLatestKlineAsync(symbol, timeframe, cancellationToken);
        return cacheResult.Status == SharedMarketDataCacheReadStatus.HitFresh
            ? cacheResult.Entry?.Payload
            : null;
    }

    public async ValueTask<SharedMarketDataCacheReadResult<MarketCandleSnapshot>> ReadLatestKlineAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSymbol = MarketDataSymbolNormalizer.Normalize(symbol);
        var cacheResult = await sharedMarketDataCache.ReadAsync<MarketCandleSnapshot>(
            SharedMarketDataCacheDataType.Kline,
            normalizedSymbol,
            timeframe,
            cancellationToken);

        if (cacheResult.Status == SharedMarketDataCacheReadStatus.HitFresh)
        {
            return cacheResult;
        }

        logger.LogDebug(
            "Shared market-data kline cache read for {Symbol}/{Timeframe} returned {Status} ({ReasonCode}).",
            normalizedSymbol,
            timeframe,
            cacheResult.Status,
            cacheResult.ReasonCode);

        return cacheResult;
    }

    internal async ValueTask<MarketDepthSnapshot?> GetLatestDepthAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var cacheResult = await ReadLatestDepthAsync(symbol, cancellationToken);
        return cacheResult.Status == SharedMarketDataCacheReadStatus.HitFresh
            ? cacheResult.Entry?.Payload
            : null;
    }

    public async ValueTask<SharedMarketDataCacheReadResult<MarketDepthSnapshot>> ReadLatestDepthAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSymbol = MarketDataSymbolNormalizer.Normalize(symbol);
        var cacheResult = await sharedMarketDataCache.ReadAsync<MarketDepthSnapshot>(
            SharedMarketDataCacheDataType.Depth,
            normalizedSymbol,
            timeframe: null,
            cancellationToken);

        if (cacheResult.Status == SharedMarketDataCacheReadStatus.HitFresh)
        {
            return cacheResult;
        }

        logger.LogDebug(
            "Shared market-data depth cache read for {Symbol} returned {Status} ({ReasonCode}).",
            normalizedSymbol,
            cacheResult.Status,
            cacheResult.ReasonCode);

        return cacheResult;
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

    internal async ValueTask<SharedMarketDataProjectionResult> RecordPriceAsync(
        MarketPriceSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        var projectionResult = SharedMarketDataProjectionPolicy.NormalizeTicker(snapshot, out var normalizedSnapshot);
        if (projectionResult.Status != SharedMarketDataProjectionStatus.Accepted)
        {
            return projectionResult;
        }

        var currentReadResult = await sharedMarketDataCache.ReadAsync<MarketPriceSnapshot>(
            SharedMarketDataCacheDataType.Ticker,
            normalizedSnapshot.Symbol,
            timeframe: null,
            cancellationToken);

        if (!TryResolveCurrentProjection(
            currentReadResult,
            SharedMarketDataProjectionReasonCode.InvalidTickerPayload,
            out var currentTicker,
            out var readFailureResult))
        {
            return readFailureResult;
        }

        if (currentTicker is not null)
        {
            var orderingResult = SharedMarketDataProjectionPolicy.EvaluateTickerOrdering(
                normalizedSnapshot,
                currentTicker);
            if (orderingResult.Status != SharedMarketDataProjectionStatus.Accepted)
            {
                return orderingResult;
            }
        }

        var cachedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var freshness = cachePolicyProvider.GetFreshness(SharedMarketDataCacheDataType.Ticker);
        var retention = cachePolicyProvider.GetRetention(SharedMarketDataCacheDataType.Ticker);
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

            return MapWriteFailure(
                cacheResult,
                SharedMarketDataProjectionReasonCode.InvalidTickerPayload);
        }

        streamHub.Publish(normalizedSnapshot);

        logger.LogDebug(
            "Central market data price cache updated for {Symbol} via {Source}.",
            normalizedSnapshot.Symbol,
            normalizedSnapshot.Source);

        return SharedMarketDataProjectionResult.Accepted();
    }

    internal async ValueTask<SharedMarketDataProjectionResult> RecordKlineAsync(
        MarketCandleSnapshot snapshot,
        CandleDataQualityGuardResult? guardResult = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        var projectionResult = SharedMarketDataProjectionPolicy.NormalizeKline(
            snapshot,
            guardResult,
            out var normalizedSnapshot);
        if (projectionResult.Status != SharedMarketDataProjectionStatus.Accepted)
        {
            return projectionResult;
        }

        var currentReadResult = await sharedMarketDataCache.ReadAsync<MarketCandleSnapshot>(
            SharedMarketDataCacheDataType.Kline,
            normalizedSnapshot.Symbol,
            normalizedSnapshot.Interval,
            cancellationToken);

        if (!TryResolveCurrentProjection(
            currentReadResult,
            SharedMarketDataProjectionReasonCode.InvalidKlinePayload,
            out var currentKline,
            out var readFailureResult))
        {
            return readFailureResult;
        }

        if (currentKline is not null)
        {
            var orderingResult = SharedMarketDataProjectionPolicy.EvaluateKlineOrdering(
                normalizedSnapshot,
                currentKline);
            if (orderingResult.Status != SharedMarketDataProjectionStatus.Accepted)
            {
                return orderingResult;
            }
        }

        var cachedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var cacheResult = await sharedMarketDataCache.WriteAsync(
            new SharedMarketDataCacheEntry<MarketCandleSnapshot>(
                SharedMarketDataCacheDataType.Kline,
                normalizedSnapshot.Symbol,
                normalizedSnapshot.Interval,
                UpdatedAtUtc: normalizedSnapshot.CloseTimeUtc,
                CachedAtUtc: cachedAtUtc,
                FreshUntilUtc: normalizedSnapshot.CloseTimeUtc.Add(cachePolicyProvider.GetFreshness(SharedMarketDataCacheDataType.Kline)),
                ExpiresAtUtc: cachedAtUtc.Add(cachePolicyProvider.GetRetention(SharedMarketDataCacheDataType.Kline)),
                Source: normalizedSnapshot.Source,
                Payload: normalizedSnapshot),
            cancellationToken);

        if (cacheResult.Status != SharedMarketDataCacheWriteStatus.Written)
        {
            logger.LogWarning(
                "Shared market-data kline cache write for {Symbol}/{Timeframe} failed closed with {ReasonCode}.",
                normalizedSnapshot.Symbol,
                normalizedSnapshot.Interval,
                cacheResult.ReasonCode);

            return MapWriteFailure(
                cacheResult,
                SharedMarketDataProjectionReasonCode.InvalidKlinePayload);
        }

        return SharedMarketDataProjectionResult.Accepted();
    }

    internal async ValueTask<SharedMarketDataProjectionResult> RecordDepthAsync(
        MarketDepthSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        var projectionResult = SharedMarketDataProjectionPolicy.NormalizeDepth(snapshot, out var normalizedSnapshot);
        if (projectionResult.Status != SharedMarketDataProjectionStatus.Accepted)
        {
            return projectionResult;
        }

        var currentReadResult = await sharedMarketDataCache.ReadAsync<MarketDepthSnapshot>(
            SharedMarketDataCacheDataType.Depth,
            normalizedSnapshot.Symbol,
            timeframe: null,
            cancellationToken);

        if (!TryResolveCurrentProjection(
            currentReadResult,
            SharedMarketDataProjectionReasonCode.InvalidDepthPayload,
            out var currentDepth,
            out var readFailureResult))
        {
            return readFailureResult;
        }

        if (currentDepth is not null)
        {
            var orderingResult = SharedMarketDataProjectionPolicy.EvaluateDepthOrdering(
                normalizedSnapshot,
                currentDepth);
            if (orderingResult.Status != SharedMarketDataProjectionStatus.Accepted)
            {
                return orderingResult;
            }
        }

        var cachedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var cacheResult = await sharedMarketDataCache.WriteAsync(
            new SharedMarketDataCacheEntry<MarketDepthSnapshot>(
                SharedMarketDataCacheDataType.Depth,
                normalizedSnapshot.Symbol,
                Timeframe: null,
                UpdatedAtUtc: normalizedSnapshot.EventTimeUtc,
                CachedAtUtc: cachedAtUtc,
                FreshUntilUtc: normalizedSnapshot.EventTimeUtc.Add(cachePolicyProvider.GetFreshness(SharedMarketDataCacheDataType.Depth)),
                ExpiresAtUtc: cachedAtUtc.Add(cachePolicyProvider.GetRetention(SharedMarketDataCacheDataType.Depth)),
                Source: normalizedSnapshot.Source,
                Payload: normalizedSnapshot),
            cancellationToken);

        if (cacheResult.Status != SharedMarketDataCacheWriteStatus.Written)
        {
            logger.LogWarning(
                "Shared market-data depth cache write for {Symbol} failed closed with {ReasonCode}.",
                normalizedSnapshot.Symbol,
                cacheResult.ReasonCode);

            return MapWriteFailure(
                cacheResult,
                SharedMarketDataProjectionReasonCode.InvalidDepthPayload);
        }

        return SharedMarketDataProjectionResult.Accepted();
    }

    private static bool TryResolveCurrentProjection<TPayload>(
        SharedMarketDataCacheReadResult<TPayload> cacheResult,
        SharedMarketDataProjectionReasonCode invalidPayloadReasonCode,
        out TPayload? payload,
        out SharedMarketDataProjectionResult projectionResult)
    {
        payload = default;
        projectionResult = SharedMarketDataProjectionResult.Accepted();

        if (cacheResult.Status is SharedMarketDataCacheReadStatus.HitFresh or SharedMarketDataCacheReadStatus.HitStale)
        {
            payload = cacheResult.Entry!.Payload;
            return true;
        }

        if (cacheResult.Status == SharedMarketDataCacheReadStatus.Miss)
        {
            return true;
        }

        projectionResult = cacheResult.Status == SharedMarketDataCacheReadStatus.ProviderUnavailable
            ? SharedMarketDataProjectionResult.ProviderUnavailable(cacheResult.ReasonSummary)
            : SharedMarketDataProjectionResult.InvalidPayload(invalidPayloadReasonCode, cacheResult.ReasonSummary);
        return false;
    }

    private static SharedMarketDataProjectionResult MapWriteFailure(
        SharedMarketDataCacheWriteResult cacheResult,
        SharedMarketDataProjectionReasonCode invalidPayloadReasonCode)
    {
        return cacheResult.Status switch
        {
            SharedMarketDataCacheWriteStatus.ProviderUnavailable =>
                SharedMarketDataProjectionResult.ProviderUnavailable(cacheResult.ReasonSummary),
            SharedMarketDataCacheWriteStatus.InvalidPayload =>
                SharedMarketDataProjectionResult.InvalidPayload(
                    invalidPayloadReasonCode,
                    cacheResult.ReasonSummary),
            _ => SharedMarketDataProjectionResult.CacheWriteFailed(cacheResult.ReasonSummary)
        };
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
