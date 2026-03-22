namespace CoinBot.Application.Abstractions.Indicators;

public sealed record IndicatorSubscription(
    string Symbol,
    string Timeframe);
