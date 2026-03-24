namespace CoinBot.Application.Abstractions.Policy;

public sealed record RiskPolicySnapshot(
    string PolicyKey,
    ExecutionGuardPolicy ExecutionGuardPolicy,
    AutonomyPolicy AutonomyPolicy,
    IReadOnlyCollection<SymbolRestriction> SymbolRestrictions)
{
    public static RiskPolicySnapshot CreateDefault()
    {
        return new RiskPolicySnapshot(
            PolicyKey: "GlobalRiskPolicy",
            ExecutionGuardPolicy: ExecutionGuardPolicy.CreateDefault(),
            AutonomyPolicy: AutonomyPolicy.CreateDefault(),
            SymbolRestrictions: Array.Empty<SymbolRestriction>());
    }
}
