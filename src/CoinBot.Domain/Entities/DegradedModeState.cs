using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class DegradedModeState : BaseEntity
{
    public DegradedModeStateCode StateCode { get; set; } = DegradedModeStateCode.Stopped;

    public DegradedModeReasonCode ReasonCode { get; set; } = DegradedModeReasonCode.MarketDataUnavailable;

    public bool SignalFlowBlocked { get; set; } = true;

    public bool ExecutionFlowBlocked { get; set; } = true;

    public DateTime? LatestDataTimestampAtUtc { get; set; }

    public DateTime? LatestHeartbeatReceivedAtUtc { get; set; }

    public int? LatestClockDriftMilliseconds { get; set; }

    public DateTime? LastStateChangedAtUtc { get; set; }

    public string? LatestHeartbeatSource { get; set; }

    public string? LatestSymbol { get; set; }

    public string? LatestTimeframe { get; set; }

    public DateTime? LatestExpectedOpenTimeUtc { get; set; }

    public int? LatestContinuityGapCount { get; set; }

    public DateTime? LatestContinuityGapStartedAtUtc { get; set; }

    public DateTime? LatestContinuityGapLastSeenAtUtc { get; set; }

    public DateTime? LatestContinuityRecoveredAtUtc { get; set; }
}
