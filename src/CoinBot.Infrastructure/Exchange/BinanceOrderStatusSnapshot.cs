namespace CoinBot.Infrastructure.Exchange;

public sealed record BinanceOrderStatusSnapshot(
    string Symbol,
    string ExchangeOrderId,
    string ClientOrderId,
    string Status,
    decimal OriginalQuantity,
    decimal ExecutedQuantity,
    decimal CumulativeQuoteQuantity,
    decimal AveragePrice,
    decimal LastExecutedQuantity,
    decimal LastExecutedPrice,
    DateTime EventTimeUtc,
    string Source);
