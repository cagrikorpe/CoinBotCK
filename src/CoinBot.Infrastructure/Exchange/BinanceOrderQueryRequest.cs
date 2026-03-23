namespace CoinBot.Infrastructure.Exchange;

public sealed record BinanceOrderQueryRequest(
    Guid ExchangeAccountId,
    string Symbol,
    string? ExchangeOrderId,
    string? ClientOrderId,
    string ApiKey,
    string ApiSecret)
{
    public override string ToString()
    {
        return $"{nameof(BinanceOrderQueryRequest)} {{ ExchangeAccountId = {ExchangeAccountId}, Symbol = {Symbol}, ExchangeOrderId = {ExchangeOrderId ?? "missing"}, ClientOrderId = {ClientOrderId ?? "missing"}, ApiKey = ***REDACTED***, ApiSecret = ***REDACTED*** }}";
    }
}
