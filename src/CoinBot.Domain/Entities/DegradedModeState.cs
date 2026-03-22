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
}