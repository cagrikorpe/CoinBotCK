using CoinBot.Domain.Enums;

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
    string Source,
    long? TradeId = null,
    string? FeeAsset = null,
    decimal? FeeAmount = null,
    ExchangeDataPlane Plane = ExchangeDataPlane.Futures);
