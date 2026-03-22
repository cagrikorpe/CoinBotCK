namespace CoinBot.Application.Abstractions.Risk;

public interface IRiskPolicyEvaluator
{
    Task<RiskVetoResult> EvaluateAsync(
        RiskPolicyEvaluationRequest request,
        CancellationToken cancellationToken = default);
}
