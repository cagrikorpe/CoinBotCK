using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Execution;

public sealed record DegradedModeSnapshot(
    DegradedModeStateCode StateCode,
    DegradedModeReasonCode ReasonCode,
    bool SignalFlowBlocked,
    bool ExecutionFlowBlocked,
    DateTime? LatestDataTimestampAtUtc,
    DateTime? LatestHeartbeatReceivedAtUtc,
    int? LatestDataAgeMilliseconds,
    int? LatestClockDriftMilliseconds,
    DateTime? LastStateChangedAtUtc,
    bool IsPersisted)
{
    public bool IsNormal => StateCode == DegradedModeStateCode.Normal &&
                            !SignalFlowBlocked &&
                            !ExecutionFlowBlocked;
}