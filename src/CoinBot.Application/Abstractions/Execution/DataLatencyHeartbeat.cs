using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Execution;

public sealed record DataLatencyHeartbeat(
    string Source,
    DateTime DataTimestampUtc,
    DegradedModeStateCode GuardStateCode = DegradedModeStateCode.Normal,
    DegradedModeReasonCode GuardReasonCode = DegradedModeReasonCode.None,
    string? Symbol = null,
    string? Timeframe = null,
    DateTime? ExpectedOpenTimeUtc = null,
    int? ContinuityGapCount = null);
