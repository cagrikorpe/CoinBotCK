namespace CoinBot.Application.Abstractions.Administration;

public sealed record CrisisEscalationExecutionResult(
    CrisisEscalationPreview Preview,
    int PurgedOrderCount,
    int FlattenAttemptCount,
    int FlattenReuseCount,
    int FailedOperationCount,
    string Summary)
{
    public bool HasPartialFailures => FailedOperationCount > 0;
}
