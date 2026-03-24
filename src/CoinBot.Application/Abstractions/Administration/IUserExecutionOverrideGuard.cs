namespace CoinBot.Application.Abstractions.Execution;

public interface IUserExecutionOverrideGuard
{
    Task<UserExecutionOverrideEvaluationResult> EvaluateAsync(
        UserExecutionOverrideEvaluationRequest request,
        CancellationToken cancellationToken = default);
}
