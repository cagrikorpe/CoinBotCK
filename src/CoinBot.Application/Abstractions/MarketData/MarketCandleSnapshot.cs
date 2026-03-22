namespace CoinBot.Application.Abstractions.MarketData;

public sealed record MarketCandleSnapshot(
    string Symbol,
    string Interval,
    DateTime OpenTimeUtc,
    DateTime CloseTimeUtc,
    decimal OpenPrice,
    decimal HighPrice,
    decimal LowPrice,
    decimal ClosePrice,
    decimal Volume,
    bool IsClosed,
    DateTime ReceivedAtUtc,
    string Source);
