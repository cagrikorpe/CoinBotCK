using System.Collections.Concurrent;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Monitoring;

namespace CoinBot.Infrastructure.MarketData;

public sealed class SharedMarketDataCacheObservabilityCollector(TimeProvider timeProvider) : ISharedMarketDataCacheObservabilityCollector
{
    public static ISharedMarketDataCacheObservabilityCollector NoOp { get; } = new NoOpCollector();

    private readonly ConcurrentDictionary<string, MutableScopeSnapshot> scopes = new(StringComparer.Ordinal);

    public void RecordRead<TPayload>(
        SharedMarketDataCacheDataType dataType,
        string symbol,
        string? timeframe,
        SharedMarketDataCacheReadResult<TPayload> result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var scope = scopes.AddOrUpdate(
            BuildScopeKey(dataType, symbol, timeframe),
            _ => CreateScopeSnapshot(
                dataType,
                symbol,
                timeframe,
                result.Status,
                lastProjectionStatus: null,
                MapReadReason(result.Status),
                result.Entry?.UpdatedAtUtc,
                result.Entry?.FreshUntilUtc,
                utcNow,
                result.Entry?.Source,
                result.ReasonSummary),
            (_, current) => UpdateScopeSnapshot(
                current,
                result.Status,
                lastProjectionStatus: current.LastProjectionStatus,
                MapReadReason(result.Status),
                result.Entry?.UpdatedAtUtc ?? current.UpdatedAtUtc,
                result.Entry?.FreshUntilUtc ?? current.FreshUntilUtc,
                utcNow,
                result.Entry?.Source ?? current.SourceLayer,
                result.ReasonSummary ?? current.ReasonSummary));

        IncrementReadCounter(scope, result.Status);
    }

    public void RecordProjection(
        SharedMarketDataCacheDataType dataType,
        string symbol,
        string? timeframe,
        SharedMarketDataProjectionResult result,
        DateTime? updatedAtUtc,
        DateTime? freshUntilUtc,
        string? sourceLayer)
    {
        ArgumentNullException.ThrowIfNull(result);

        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        scopes.AddOrUpdate(
            BuildScopeKey(dataType, symbol, timeframe),
            _ => CreateScopeSnapshot(
                dataType,
                symbol,
                timeframe,
                SharedMarketDataCacheReadStatus.Miss,
                result.Status,
                MapProjectionReason(result.Status),
                updatedAtUtc,
                freshUntilUtc,
                utcNow,
                sourceLayer,
                result.ReasonSummary),
            (_, current) => UpdateScopeSnapshot(
                current,
                current.LastReadStatus,
                result.Status,
                MapProjectionReason(result.Status),
                result.Status == SharedMarketDataProjectionStatus.Accepted ? updatedAtUtc : current.UpdatedAtUtc,
                result.Status == SharedMarketDataProjectionStatus.Accepted ? freshUntilUtc : current.FreshUntilUtc,
                utcNow,
                result.Status == SharedMarketDataProjectionStatus.Accepted
                    ? sourceLayer ?? current.SourceLayer
                    : current.SourceLayer,
                result.ReasonSummary ?? current.ReasonSummary));
    }

    public SharedMarketDataCacheHealthSnapshot GetSnapshot(DateTime utcNow)
    {
        var normalizedNow = NormalizeUtc(utcNow);
        var symbolSnapshots = scopes.Values
            .Select(scope => ToSymbolSnapshot(scope, normalizedNow))
            .OrderBy(snapshot => snapshot.DataType)
            .ThenBy(snapshot => snapshot.Symbol, StringComparer.Ordinal)
            .ThenBy(snapshot => snapshot.Timeframe, StringComparer.Ordinal)
            .ToArray();
        var streamSnapshots = symbolSnapshots
            .GroupBy(snapshot => snapshot.DataType)
            .Select(group => group
                .OrderByDescending(snapshot => snapshot.LastObservedAtUtc)
                .ThenBy(snapshot => snapshot.Symbol, StringComparer.Ordinal)
                .ThenBy(snapshot => snapshot.Timeframe, StringComparer.Ordinal)
                .First())
            .OrderBy(snapshot => snapshot.DataType)
            .Select(snapshot => new SharedMarketDataCacheStreamSnapshot(
                snapshot.DataType,
                snapshot.Symbol,
                snapshot.Timeframe,
                snapshot.UpdatedAtUtc,
                snapshot.FreshUntilUtc,
                snapshot.StaleReasonCode,
                snapshot.SourceLayer,
                snapshot.ReasonSummary))
            .ToArray();

        return new SharedMarketDataCacheHealthSnapshot(
            symbolSnapshots.Sum(snapshot => GetScopeCounter(snapshot.DataType, snapshot.Symbol, snapshot.Timeframe, counter => counter.HitCount)),
            symbolSnapshots.Sum(snapshot => GetScopeCounter(snapshot.DataType, snapshot.Symbol, snapshot.Timeframe, counter => counter.MissCount)),
            symbolSnapshots.Sum(snapshot => GetScopeCounter(snapshot.DataType, snapshot.Symbol, snapshot.Timeframe, counter => counter.StaleHitCount)),
            symbolSnapshots.Sum(snapshot => GetScopeCounter(snapshot.DataType, snapshot.Symbol, snapshot.Timeframe, counter => counter.ProviderUnavailableCount)),
            symbolSnapshots.Sum(snapshot => GetScopeCounter(snapshot.DataType, snapshot.Symbol, snapshot.Timeframe, counter => counter.InvalidPayloadCount)),
            symbolSnapshots.Sum(snapshot => GetScopeCounter(snapshot.DataType, snapshot.Symbol, snapshot.Timeframe, counter => counter.DeserializeFailedCount)),
            normalizedNow,
            symbolSnapshots,
            streamSnapshots);
    }

    private static MutableScopeSnapshot CreateScopeSnapshot(
        SharedMarketDataCacheDataType dataType,
        string symbol,
        string? timeframe,
        SharedMarketDataCacheReadStatus lastReadStatus,
        SharedMarketDataProjectionStatus? lastProjectionStatus,
        SharedMarketDataCacheStaleReasonCode reasonCode,
        DateTime? updatedAtUtc,
        DateTime? freshUntilUtc,
        DateTime lastObservedAtUtc,
        string? sourceLayer,
        string? reasonSummary)
    {
        return new MutableScopeSnapshot(
            dataType,
            NormalizeSymbolForTelemetry(symbol),
            NormalizeTimeframeForTelemetry(dataType, timeframe),
            NormalizeNullableUtc(updatedAtUtc),
            NormalizeNullableUtc(freshUntilUtc),
            NormalizeUtc(lastObservedAtUtc),
            lastReadStatus,
            lastProjectionStatus,
            reasonCode,
            NormalizeSource(sourceLayer),
            SanitizeSummary(reasonSummary));
    }

    private static MutableScopeSnapshot UpdateScopeSnapshot(
        MutableScopeSnapshot current,
        SharedMarketDataCacheReadStatus lastReadStatus,
        SharedMarketDataProjectionStatus? lastProjectionStatus,
        SharedMarketDataCacheStaleReasonCode reasonCode,
        DateTime? updatedAtUtc,
        DateTime? freshUntilUtc,
        DateTime lastObservedAtUtc,
        string? sourceLayer,
        string? reasonSummary)
    {
        return current with
        {
            UpdatedAtUtc = NormalizeNullableUtc(updatedAtUtc),
            FreshUntilUtc = NormalizeNullableUtc(freshUntilUtc),
            LastObservedAtUtc = NormalizeUtc(lastObservedAtUtc),
            LastReadStatus = lastReadStatus,
            LastProjectionStatus = lastProjectionStatus,
            StaleReasonCode = reasonCode,
            SourceLayer = NormalizeSource(sourceLayer),
            ReasonSummary = SanitizeSummary(reasonSummary),
            HitCount = current.HitCount,
            MissCount = current.MissCount,
            StaleHitCount = current.StaleHitCount,
            ProviderUnavailableCount = current.ProviderUnavailableCount,
            InvalidPayloadCount = current.InvalidPayloadCount,
            DeserializeFailedCount = current.DeserializeFailedCount
        };
    }

    private static void IncrementReadCounter(MutableScopeSnapshot scope, SharedMarketDataCacheReadStatus status)
    {
        switch (status)
        {
            case SharedMarketDataCacheReadStatus.HitFresh:
                scope.HitCount++;
                break;
            case SharedMarketDataCacheReadStatus.HitStale:
                scope.StaleHitCount++;
                break;
            case SharedMarketDataCacheReadStatus.Miss:
                scope.MissCount++;
                break;
            case SharedMarketDataCacheReadStatus.ProviderUnavailable:
                scope.ProviderUnavailableCount++;
                break;
            case SharedMarketDataCacheReadStatus.DeserializeFailed:
                scope.DeserializeFailedCount++;
                break;
            case SharedMarketDataCacheReadStatus.InvalidPayload:
                scope.InvalidPayloadCount++;
                break;
        }
    }

    private long GetScopeCounter(
        SharedMarketDataCacheDataType dataType,
        string symbol,
        string? timeframe,
        Func<MutableScopeSnapshot, long> selector)
    {
        return scopes.TryGetValue(BuildScopeKey(dataType, symbol, timeframe), out var scope)
            ? selector(scope)
            : 0L;
    }

    private static SharedMarketDataCacheSymbolSnapshot ToSymbolSnapshot(MutableScopeSnapshot scope, DateTime utcNow)
    {
        var staleReasonCode = scope.StaleReasonCode;
        var readStatus = scope.LastReadStatus;

        if (scope.FreshUntilUtc.HasValue &&
            scope.FreshUntilUtc.Value < utcNow &&
            staleReasonCode is SharedMarketDataCacheStaleReasonCode.Fresh or SharedMarketDataCacheStaleReasonCode.FreshnessTimeout)
        {
            staleReasonCode = SharedMarketDataCacheStaleReasonCode.FreshnessTimeout;
            readStatus = SharedMarketDataCacheReadStatus.HitStale;
        }

        return new SharedMarketDataCacheSymbolSnapshot(
            scope.DataType,
            scope.Symbol,
            scope.Timeframe,
            scope.UpdatedAtUtc,
            scope.FreshUntilUtc,
            scope.LastObservedAtUtc,
            readStatus,
            scope.LastProjectionStatus,
            staleReasonCode,
            scope.SourceLayer,
            scope.ReasonSummary);
    }

    private static string BuildScopeKey(SharedMarketDataCacheDataType dataType, string symbol, string? timeframe)
    {
        var normalizedSymbol = NormalizeSymbolForTelemetry(symbol);
        var normalizedTimeframe = NormalizeTimeframeForTelemetry(dataType, timeframe) ?? "spot";

        return $"{dataType}:{normalizedSymbol}:{normalizedTimeframe}";
    }

    private static SharedMarketDataCacheStaleReasonCode MapReadReason(SharedMarketDataCacheReadStatus status)
    {
        return status switch
        {
            SharedMarketDataCacheReadStatus.HitFresh => SharedMarketDataCacheStaleReasonCode.Fresh,
            SharedMarketDataCacheReadStatus.HitStale => SharedMarketDataCacheStaleReasonCode.FreshnessTimeout,
            SharedMarketDataCacheReadStatus.Miss => SharedMarketDataCacheStaleReasonCode.Miss,
            SharedMarketDataCacheReadStatus.ProviderUnavailable => SharedMarketDataCacheStaleReasonCode.ProviderUnavailable,
            SharedMarketDataCacheReadStatus.DeserializeFailed => SharedMarketDataCacheStaleReasonCode.DeserializeFailed,
            _ => SharedMarketDataCacheStaleReasonCode.InvalidPayload
        };
    }

    private static SharedMarketDataCacheStaleReasonCode MapProjectionReason(SharedMarketDataProjectionStatus status)
    {
        return status switch
        {
            SharedMarketDataProjectionStatus.Accepted => SharedMarketDataCacheStaleReasonCode.Fresh,
            SharedMarketDataProjectionStatus.IgnoredOutOfOrder => SharedMarketDataCacheStaleReasonCode.IgnoredOutOfOrder,
            SharedMarketDataProjectionStatus.IgnoredDegraded => SharedMarketDataCacheStaleReasonCode.IgnoredDegraded,
            SharedMarketDataProjectionStatus.ProviderUnavailable => SharedMarketDataCacheStaleReasonCode.ProviderUnavailable,
            SharedMarketDataProjectionStatus.CacheWriteFailed => SharedMarketDataCacheStaleReasonCode.CacheWriteFailed,
            _ => SharedMarketDataCacheStaleReasonCode.InvalidPayload
        };
    }

    private static string NormalizeSymbolForTelemetry(string symbol)
    {
        try
        {
            return MarketDataSymbolNormalizer.Normalize(symbol);
        }
        catch (ArgumentException)
        {
            return string.IsNullOrWhiteSpace(symbol)
                ? "INVALID"
                : symbol.Trim().ToUpperInvariant();
        }
    }

    private static string? NormalizeTimeframeForTelemetry(SharedMarketDataCacheDataType dataType, string? timeframe)
    {
        try
        {
            return SharedMarketDataCacheKeyBuilder.NormalizeTimeframe(dataType, timeframe);
        }
        catch (ArgumentException)
        {
            return dataType == SharedMarketDataCacheDataType.Kline
                ? string.IsNullOrWhiteSpace(timeframe)
                    ? "invalid"
                    : timeframe.Trim().ToLowerInvariant()
                : "spot";
        }
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static DateTime? NormalizeNullableUtc(DateTime? value)
    {
        return value.HasValue
            ? NormalizeUtc(value.Value)
            : null;
    }

    private static string NormalizeSource(string? sourceLayer)
    {
        return string.IsNullOrWhiteSpace(sourceLayer)
            ? "n/a"
            : sourceLayer.Trim();
    }

    private static string? SanitizeSummary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sanitized = value.Trim();
        return sanitized.Length <= 256
            ? sanitized
            : sanitized[..256];
    }

    private sealed record MutableScopeSnapshot(
        SharedMarketDataCacheDataType DataType,
        string Symbol,
        string? Timeframe,
        DateTime? UpdatedAtUtc,
        DateTime? FreshUntilUtc,
        DateTime LastObservedAtUtc,
        SharedMarketDataCacheReadStatus LastReadStatus,
        SharedMarketDataProjectionStatus? LastProjectionStatus,
        SharedMarketDataCacheStaleReasonCode StaleReasonCode,
        string SourceLayer,
        string? ReasonSummary)
    {
        public long HitCount { get; set; }

        public long MissCount { get; set; }

        public long StaleHitCount { get; set; }

        public long ProviderUnavailableCount { get; set; }

        public long InvalidPayloadCount { get; set; }

        public long DeserializeFailedCount { get; set; }
    }

    private sealed class NoOpCollector : ISharedMarketDataCacheObservabilityCollector
    {
        public void RecordRead<TPayload>(
            SharedMarketDataCacheDataType dataType,
            string symbol,
            string? timeframe,
            SharedMarketDataCacheReadResult<TPayload> result)
        {
        }

        public void RecordProjection(
            SharedMarketDataCacheDataType dataType,
            string symbol,
            string? timeframe,
            SharedMarketDataProjectionResult result,
            DateTime? updatedAtUtc,
            DateTime? freshUntilUtc,
            string? sourceLayer)
        {
        }

        public SharedMarketDataCacheHealthSnapshot GetSnapshot(DateTime utcNow)
        {
            return SharedMarketDataCacheHealthSnapshot.Empty(NormalizeUtc(utcNow));
        }
    }
}
