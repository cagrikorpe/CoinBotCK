namespace CoinBot.Domain.Enums;

public enum ExecutionOrderState
{
    Received = 0,
    GatePassed = 1,
    Dispatching = 2,
    Submitted = 3,
    PartiallyFilled = 4,
    CancelRequested = 5,
    Filled = 6,
    Cancelled = 7,
    Rejected = 8,
    Failed = 9
}
