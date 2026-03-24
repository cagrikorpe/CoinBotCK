namespace CoinBot.Application.Abstractions.Policy;

public sealed record GlobalPolicySnapshot(
    RiskPolicySnapshot Policy,
    int CurrentVersion,
    DateTime LastUpdatedAtUtc,
    string? LastUpdatedByUserId,
    string? LastChangeSummary,
    bool IsPersisted,
    IReadOnlyCollection<GlobalPolicyVersionSnapshot> Versions)
{
    public static GlobalPolicySnapshot CreateDefault(DateTime utcNow)
    {
        return new GlobalPolicySnapshot(
            RiskPolicySnapshot.CreateDefault(),
            CurrentVersion: 1,
            LastUpdatedAtUtc: utcNow,
            LastUpdatedByUserId: null,
            LastChangeSummary: "Default policy",
            IsPersisted: false,
            Versions: Array.Empty<GlobalPolicyVersionSnapshot>());
    }
}
