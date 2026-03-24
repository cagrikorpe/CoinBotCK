namespace CoinBot.Application.Abstractions.Policy;

public sealed record GlobalPolicyVersionSnapshot(
    int Version,
    DateTime CreatedAtUtc,
    string CreatedByUserId,
    string ChangeSummary,
    IReadOnlyCollection<GlobalPolicyDiffEntry> DiffEntries,
    RiskPolicySnapshot Policy,
    string? Source = null,
    string? CorrelationId = null,
    int? RolledBackFromVersion = null);
