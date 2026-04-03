using CoinBot.Application.Abstractions.MarketData;

namespace CoinBot.Application.Abstractions.Monitoring;

public enum SharedMarketDataCacheStaleReasonCode
{
    Fresh = 0,
    FreshnessTimeout = 1,
    Miss = 2,
    ProviderUnavailable = 3,
    DeserializeFailed = 4,
    InvalidPayload = 5,
    IgnoredOutOfOrder = 6,
    IgnoredDegraded = 7,
    CacheWriteFailed = 8
}

public sealed record SharedMarketDataCacheSymbolSnapshot(
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
    string? ReasonSummary);

public sealed record SharedMarketDataCacheStreamSnapshot(
    SharedMarketDataCacheDataType DataType,
    string Symbol,
    string? Timeframe,
    DateTime? UpdatedAtUtc,
    DateTime? FreshUntilUtc,
    SharedMarketDataCacheStaleReasonCode StaleReasonCode,
    string SourceLayer,
    string? ReasonSummary);

public sealed record SharedMarketDataCacheHealthSnapshot(
    long HitCount,
    long MissCount,
    long StaleHitCount,
    long ProviderUnavailableCount,
    long InvalidPayloadCount,
    long DeserializeFailedCount,
    DateTime LastObservedAtUtc,
    IReadOnlyCollection<SharedMarketDataCacheSymbolSnapshot> SymbolFreshness,
    IReadOnlyCollection<SharedMarketDataCacheStreamSnapshot> StreamSnapshots)
{
    public static SharedMarketDataCacheHealthSnapshot Empty(DateTime? lastObservedAtUtc = null)
    {
        return new SharedMarketDataCacheHealthSnapshot(
            0,
            0,
            0,
            0,
            0,
            0,
            lastObservedAtUtc ?? DateTime.UtcNow,
            Array.Empty<SharedMarketDataCacheSymbolSnapshot>(),
            Array.Empty<SharedMarketDataCacheStreamSnapshot>());
    }
}
