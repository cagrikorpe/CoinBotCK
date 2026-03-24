namespace CoinBot.Application.Abstractions.Policy;

public interface IGlobalPolicyEngine
{
    Task<GlobalPolicySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<GlobalPolicyEvaluationResult> EvaluateAsync(
        GlobalPolicyEvaluationRequest request,
        CancellationToken cancellationToken = default);

    Task<GlobalPolicySnapshot> UpdateAsync(
        GlobalPolicyUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<GlobalPolicySnapshot> RollbackAsync(
        GlobalPolicyRollbackRequest request,
        CancellationToken cancellationToken = default);
}
