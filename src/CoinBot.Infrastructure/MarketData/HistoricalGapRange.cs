namespace CoinBot.Infrastructure.MarketData;

public sealed record HistoricalGapRange(
    string Symbol,
    string Interval,
    DateTime StartOpenTimeUtc,
    DateTime EndOpenTimeUtc,
    int MissingCandleCount);
