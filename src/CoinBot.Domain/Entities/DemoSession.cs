using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class DemoSession : UserOwnedEntity
{
    public int SequenceNumber { get; set; }

    public string SeedAsset { get; set; } = string.Empty;

    public decimal SeedAmount { get; set; }

    public DemoSessionState State { get; set; } = DemoSessionState.Active;

    public DemoConsistencyStatus ConsistencyStatus { get; set; } = DemoConsistencyStatus.Unknown;

    public DateTime StartedAtUtc { get; set; }

    public DateTime? ClosedAtUtc { get; set; }

    public DateTime? LastConsistencyCheckedAtUtc { get; set; }

    public DateTime? LastDriftDetectedAtUtc { get; set; }

    public string? LastDriftSummary { get; set; }
}
