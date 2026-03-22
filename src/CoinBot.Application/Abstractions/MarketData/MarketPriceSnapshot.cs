namespace CoinBot.Application.Abstractions.MarketData;

public sealed record MarketPriceSnapshot(
    string Symbol,
    decimal Price,
    DateTime ObservedAtUtc,
    DateTime ReceivedAtUtc,
    string Source);
