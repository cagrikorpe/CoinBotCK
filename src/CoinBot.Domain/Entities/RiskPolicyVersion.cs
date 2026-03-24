namespace CoinBot.Domain.Entities;

public sealed class RiskPolicyVersion
{
    public Guid Id { get; set; }

    public Guid RiskPolicyId { get; set; }

    public int Version { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public string CreatedByUserId { get; set; } = string.Empty;

    public string? Source { get; set; }

    public string? CorrelationId { get; set; }

    public string ChangeSummary { get; set; } = string.Empty;

    public string PolicyJson { get; set; } = string.Empty;

    public string? DiffJson { get; set; }

    public int? RolledBackFromVersion { get; set; }
}
