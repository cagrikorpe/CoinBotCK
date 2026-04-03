namespace CoinBot.Application.Abstractions.MarketData;

public enum SharedMarketDataProjectionStatus
{
    Accepted = 0,
    IgnoredOutOfOrder = 1,
    IgnoredDegraded = 2,
    InvalidPayload = 3,
    ProviderUnavailable = 4,
    CacheWriteFailed = 5
}

public enum SharedMarketDataProjectionReasonCode
{
    Accepted = 0,
    TickerOutOfOrder = 1,
    TickerDegraded = 2,
    InvalidTickerPayload = 3,
    KlineOutOfOrder = 4,
    KlineDegraded = 5,
    InvalidKlinePayload = 6,
    DepthOutOfOrder = 7,
    DepthDegraded = 8,
    InvalidDepthPayload = 9,
    ProviderUnavailable = 10,
    CacheWriteFailed = 11
}

public sealed record SharedMarketDataProjectionResult(
    SharedMarketDataProjectionStatus Status,
    SharedMarketDataProjectionReasonCode ReasonCode,
    string? ReasonSummary = null)
{
    public static SharedMarketDataProjectionResult Accepted()
    {
        return new SharedMarketDataProjectionResult(
            SharedMarketDataProjectionStatus.Accepted,
            SharedMarketDataProjectionReasonCode.Accepted);
    }

    public static SharedMarketDataProjectionResult IgnoredOutOfOrder(
        SharedMarketDataProjectionReasonCode reasonCode,
        string? reasonSummary = null)
    {
        return new SharedMarketDataProjectionResult(
            SharedMarketDataProjectionStatus.IgnoredOutOfOrder,
            reasonCode,
            reasonSummary);
    }

    public static SharedMarketDataProjectionResult IgnoredDegraded(
        SharedMarketDataProjectionReasonCode reasonCode,
        string? reasonSummary = null)
    {
        return new SharedMarketDataProjectionResult(
            SharedMarketDataProjectionStatus.IgnoredDegraded,
            reasonCode,
            reasonSummary);
    }

    public static SharedMarketDataProjectionResult InvalidPayload(
        SharedMarketDataProjectionReasonCode reasonCode,
        string? reasonSummary = null)
    {
        return new SharedMarketDataProjectionResult(
            SharedMarketDataProjectionStatus.InvalidPayload,
            reasonCode,
            reasonSummary);
    }

    public static SharedMarketDataProjectionResult ProviderUnavailable(string? reasonSummary = null)
    {
        return new SharedMarketDataProjectionResult(
            SharedMarketDataProjectionStatus.ProviderUnavailable,
            SharedMarketDataProjectionReasonCode.ProviderUnavailable,
            reasonSummary);
    }

    public static SharedMarketDataProjectionResult CacheWriteFailed(string? reasonSummary = null)
    {
        return new SharedMarketDataProjectionResult(
            SharedMarketDataProjectionStatus.CacheWriteFailed,
            SharedMarketDataProjectionReasonCode.CacheWriteFailed,
            reasonSummary);
    }
}
