namespace CoinBot.Infrastructure.Exchange;

public sealed record BinanceOrderQueryRequest(
    Guid ExchangeAccountId,
    string Symbol,
    string? ExchangeOrderId,
    string? ClientOrderId,
    string ApiKey,
    string ApiSecret);
