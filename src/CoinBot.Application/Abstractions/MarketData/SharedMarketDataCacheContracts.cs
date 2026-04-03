namespace CoinBot.Application.Abstractions.MarketData;

public enum SharedMarketDataCacheDataType
{
    Kline = 0,
    Ticker = 1,
    Depth = 2
}

public enum SharedMarketDataCacheReadStatus
{
    HitFresh = 0,
    HitStale = 1,
    Miss = 2,
    ProviderUnavailable = 3,
    DeserializeFailed = 4,
    InvalidPayload = 5
}

public enum SharedMarketDataCacheWriteStatus
{
    Written = 0,
    ProviderUnavailable = 1,
    SerializeFailed = 2,
    InvalidPayload = 3
}

public sealed record SharedMarketDataCacheEntry<TPayload>(
    SharedMarketDataCacheDataType DataType,
    string Symbol,
    string? Timeframe,
    DateTime UpdatedAtUtc,
    DateTime CachedAtUtc,
    DateTime FreshUntilUtc,
    DateTime ExpiresAtUtc,
    string Source,
    TPayload Payload)
{
    public int SchemaVersion { get; init; } = 1;
}

public sealed record SharedMarketDataCacheReadResult<TPayload>(
    SharedMarketDataCacheReadStatus Status,
    SharedMarketDataCacheEntry<TPayload>? Entry,
    string ReasonCode,
    string? ReasonSummary = null)
{
    public static SharedMarketDataCacheReadResult<TPayload> HitFresh(SharedMarketDataCacheEntry<TPayload> entry)
    {
        return new SharedMarketDataCacheReadResult<TPayload>(
            SharedMarketDataCacheReadStatus.HitFresh,
            entry,
            nameof(SharedMarketDataCacheReadStatus.HitFresh));
    }

    public static SharedMarketDataCacheReadResult<TPayload> HitStale(SharedMarketDataCacheEntry<TPayload> entry)
    {
        return new SharedMarketDataCacheReadResult<TPayload>(
            SharedMarketDataCacheReadStatus.HitStale,
            entry,
            nameof(SharedMarketDataCacheReadStatus.HitStale));
    }

    public static SharedMarketDataCacheReadResult<TPayload> Miss(string? reasonSummary = null)
    {
        return new SharedMarketDataCacheReadResult<TPayload>(
            SharedMarketDataCacheReadStatus.Miss,
            null,
            nameof(SharedMarketDataCacheReadStatus.Miss),
            reasonSummary);
    }

    public static SharedMarketDataCacheReadResult<TPayload> ProviderUnavailable(string? reasonSummary = null)
    {
        return new SharedMarketDataCacheReadResult<TPayload>(
            SharedMarketDataCacheReadStatus.ProviderUnavailable,
            null,
            nameof(SharedMarketDataCacheReadStatus.ProviderUnavailable),
            reasonSummary);
    }

    public static SharedMarketDataCacheReadResult<TPayload> DeserializeFailed(string? reasonSummary = null)
    {
        return new SharedMarketDataCacheReadResult<TPayload>(
            SharedMarketDataCacheReadStatus.DeserializeFailed,
            null,
            nameof(SharedMarketDataCacheReadStatus.DeserializeFailed),
            reasonSummary);
    }

    public static SharedMarketDataCacheReadResult<TPayload> InvalidPayload(string? reasonSummary = null)
    {
        return new SharedMarketDataCacheReadResult<TPayload>(
            SharedMarketDataCacheReadStatus.InvalidPayload,
            null,
            nameof(SharedMarketDataCacheReadStatus.InvalidPayload),
            reasonSummary);
    }
}

public sealed record SharedMarketDataCacheWriteResult(
    SharedMarketDataCacheWriteStatus Status,
    string ReasonCode,
    string? ReasonSummary = null)
{
    public static SharedMarketDataCacheWriteResult Written()
    {
        return new SharedMarketDataCacheWriteResult(
            SharedMarketDataCacheWriteStatus.Written,
            nameof(SharedMarketDataCacheWriteStatus.Written));
    }

    public static SharedMarketDataCacheWriteResult ProviderUnavailable(string? reasonSummary = null)
    {
        return new SharedMarketDataCacheWriteResult(
            SharedMarketDataCacheWriteStatus.ProviderUnavailable,
            nameof(SharedMarketDataCacheWriteStatus.ProviderUnavailable),
            reasonSummary);
    }

    public static SharedMarketDataCacheWriteResult SerializeFailed(string? reasonSummary = null)
    {
        return new SharedMarketDataCacheWriteResult(
            SharedMarketDataCacheWriteStatus.SerializeFailed,
            nameof(SharedMarketDataCacheWriteStatus.SerializeFailed),
            reasonSummary);
    }

    public static SharedMarketDataCacheWriteResult InvalidPayload(string? reasonSummary = null)
    {
        return new SharedMarketDataCacheWriteResult(
            SharedMarketDataCacheWriteStatus.InvalidPayload,
            nameof(SharedMarketDataCacheWriteStatus.InvalidPayload),
            reasonSummary);
    }
}
