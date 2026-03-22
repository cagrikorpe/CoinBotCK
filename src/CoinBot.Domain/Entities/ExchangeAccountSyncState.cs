using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class ExchangeAccountSyncState : UserOwnedEntity
{
    public Guid ExchangeAccountId { get; set; }

    public ExchangePrivateStreamConnectionState PrivateStreamConnectionState { get; set; } = ExchangePrivateStreamConnectionState.Disconnected;

    public DateTime? LastListenKeyStartedAtUtc { get; set; }

    public DateTime? LastListenKeyRenewedAtUtc { get; set; }

    public DateTime? LastPrivateStreamEventAtUtc { get; set; }

    public DateTime? LastBalanceSyncedAtUtc { get; set; }

    public DateTime? LastPositionSyncedAtUtc { get; set; }

    public DateTime? LastStateReconciledAtUtc { get; set; }

    public ExchangeStateDriftStatus DriftStatus { get; set; } = ExchangeStateDriftStatus.Unknown;

    public string? DriftSummary { get; set; }

    public DateTime? LastDriftDetectedAtUtc { get; set; }

    public int ConsecutiveStreamFailureCount { get; set; }

    public string? LastErrorCode { get; set; }
}
