using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Policy;

public sealed record AutonomyPolicy(
    AutonomyPolicyMode Mode,
    bool RequireManualApprovalForLive)
{
    public static AutonomyPolicy CreateDefault()
    {
        return new AutonomyPolicy(
            AutonomyPolicyMode.LowRiskAutoAct,
            RequireManualApprovalForLive: false);
    }
}
