using CoinBot.Domain.Enums;

namespace CoinBot.Infrastructure.MarketData;

public sealed record CandleContinuityValidationResult(
    bool IsAccepted,
    DegradedModeStateCode GuardStateCode,
    DegradedModeReasonCode GuardReasonCode,
    DateTime EffectiveDataTimestampUtc,
    DateTime? ExpectedOpenTimeUtc);
