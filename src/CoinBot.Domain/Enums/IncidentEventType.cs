namespace CoinBot.Domain.Enums;

public enum IncidentEventType
{
    IncidentCreated = 0,
    ApprovalQueued = 1,
    ApprovalRecorded = 2,
    ApprovalRejected = 3,
    ApprovalExpired = 4,
    ApprovalExecuted = 5,
    IncidentResolved = 6,
    IncidentEscalated = 7,
    TraceLinked = 8,
    StateLinked = 9,
    BreakerLinked = 10,
    RecoveryRecorded = 11,
    ExecutionFailed = 12
}
