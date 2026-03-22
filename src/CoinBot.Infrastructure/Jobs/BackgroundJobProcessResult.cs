namespace CoinBot.Infrastructure.Jobs;

public sealed record BackgroundJobProcessResult(
    bool IsSuccessful,
    bool IsRetryableFailure,
    string? ErrorCode)
{
    public static BackgroundJobProcessResult Success()
    {
        return new BackgroundJobProcessResult(
            IsSuccessful: true,
            IsRetryableFailure: false,
            ErrorCode: null);
    }

    public static BackgroundJobProcessResult RetryableFailure(string errorCode)
    {
        return new BackgroundJobProcessResult(
            IsSuccessful: false,
            IsRetryableFailure: true,
            ErrorCode: errorCode);
    }

    public static BackgroundJobProcessResult PermanentFailure(string errorCode)
    {
        return new BackgroundJobProcessResult(
            IsSuccessful: false,
            IsRetryableFailure: false,
            ErrorCode: errorCode);
    }
}
