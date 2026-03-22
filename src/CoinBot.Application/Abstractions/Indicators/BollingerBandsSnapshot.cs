namespace CoinBot.Application.Abstractions.Indicators;

public sealed record BollingerBandsSnapshot(
    int Period,
    decimal StandardDeviationMultiplier,
    bool IsReady,
    decimal? MiddleBand,
    decimal? UpperBand,
    decimal? LowerBand,
    decimal? StandardDeviation);
