namespace CoinBot.Domain.Enums;

public enum ExecutionOrderState
{
    Received = 0,
    GatePassed = 1,
    Dispatching = 2,
    Submitted = 3,
    PartiallyFilled = 4,
    Filled = 5,
    Cancelled = 6,
    Rejected = 7,
    Failed = 8
}
