using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Indicators;

public sealed record StrategyIndicatorSnapshot(
    string Symbol,
    string Timeframe,
    DateTime OpenTimeUtc,
    DateTime CloseTimeUtc,
    DateTime ReceivedAtUtc,
    int SampleCount,
    int RequiredSampleCount,
    IndicatorDataState State,
    DegradedModeReasonCode DataQualityReasonCode,
    RelativeStrengthIndexSnapshot Rsi,
    MovingAverageConvergenceDivergenceSnapshot Macd,
    BollingerBandsSnapshot Bollinger,
    string Source);
