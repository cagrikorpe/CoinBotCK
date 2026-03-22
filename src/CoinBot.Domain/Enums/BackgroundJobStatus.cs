namespace CoinBot.Domain.Enums;

public enum BackgroundJobStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    RetryPending = 3,
    Failed = 4,
    TimedOut = 5
}
