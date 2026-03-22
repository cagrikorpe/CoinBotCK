using CoinBot.Domain.Enums;

namespace CoinBot.Infrastructure.MarketData;

public sealed record CandleDataQualityGuardResult(
    bool IsAccepted,
    DegradedModeStateCode GuardStateCode,
    DegradedModeReasonCode GuardReasonCode,
    DateTime EffectiveDataTimestampUtc,
    DateTime? ExpectedOpenTimeUtc);
