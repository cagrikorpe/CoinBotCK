using CoinBot.Domain.Enums;

namespace CoinBot.Infrastructure.Exchange;

public sealed record BinanceSpotTradeFillSnapshot(
    string Symbol,
    string ExchangeOrderId,
    string ClientOrderId,
    long TradeId,
    decimal Quantity,
    decimal QuoteQuantity,
    decimal Price,
    string? FeeAsset,
    decimal? FeeAmount,
    DateTime EventTimeUtc,
    string Source,
    ExchangeDataPlane Plane = ExchangeDataPlane.Spot);
