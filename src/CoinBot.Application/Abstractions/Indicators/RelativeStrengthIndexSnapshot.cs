namespace CoinBot.Application.Abstractions.Indicators;

public sealed record RelativeStrengthIndexSnapshot(
    int Period,
    bool IsReady,
    decimal? Value);
