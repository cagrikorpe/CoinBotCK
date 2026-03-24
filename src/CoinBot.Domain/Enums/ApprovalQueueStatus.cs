namespace CoinBot.Domain.Enums;

public enum ApprovalQueueStatus
{
    Pending = 0,
    Approved = 1,
    Executed = 2,
    Rejected = 3,
    Expired = 4,
    Cancelled = 5,
    Failed = 6
}
