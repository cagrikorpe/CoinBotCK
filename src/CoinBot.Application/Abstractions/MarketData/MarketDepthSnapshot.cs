namespace CoinBot.Application.Abstractions.MarketData;

public sealed record MarketDepthLevelSnapshot(
    decimal Price,
    decimal Quantity);

public sealed record MarketDepthSnapshot(
    string Symbol,
    IReadOnlyCollection<MarketDepthLevelSnapshot> Bids,
    IReadOnlyCollection<MarketDepthLevelSnapshot> Asks,
    long? LastUpdateId,
    DateTime EventTimeUtc,
    DateTime ReceivedAtUtc,
    string Source);
