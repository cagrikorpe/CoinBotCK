namespace CoinBot.Domain.Entities;

public sealed class RiskPolicy
{
    public Guid Id { get; set; }

    public string PolicyKey { get; set; } = string.Empty;

    public int CurrentVersion { get; set; }

    public string PolicyJson { get; set; } = string.Empty;

    public string PolicyHash { get; set; } = string.Empty;

    public DateTime LastUpdatedAtUtc { get; set; }

    public string? LastUpdatedByUserId { get; set; }

    public string? LastChangeSummary { get; set; }
}
