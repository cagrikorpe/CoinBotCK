namespace CoinBot.Domain.Enums;

public enum IncidentStatus
{
    Open = 0,
    PendingApproval = 1,
    Monitoring = 2,
    Resolved = 3,
    Rejected = 4,
    Expired = 5,
    Cancelled = 6,
    Failed = 7
}
