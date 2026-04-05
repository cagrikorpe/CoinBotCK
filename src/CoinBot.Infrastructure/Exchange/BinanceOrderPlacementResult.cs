namespace CoinBot.Infrastructure.Exchange;

public sealed record BinanceOrderPlacementResult(
    string OrderId,
    string ClientOrderId,
    DateTime SubmittedAtUtc,
    BinanceOrderStatusSnapshot? Snapshot = null);
