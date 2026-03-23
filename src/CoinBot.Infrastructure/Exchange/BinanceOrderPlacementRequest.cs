using CoinBot.Domain.Enums;

namespace CoinBot.Infrastructure.Exchange;

public sealed record BinanceOrderPlacementRequest(
    Guid ExchangeAccountId,
    string Symbol,
    ExecutionOrderSide Side,
    ExecutionOrderType OrderType,
    decimal Quantity,
    decimal Price,
    string ClientOrderId,
    string ApiKey,
    string ApiSecret);
