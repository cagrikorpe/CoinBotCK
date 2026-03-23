namespace CoinBot.Application.Abstractions.ExchangeCredentials;

public sealed record StoreExchangeCredentialsRequest(
    Guid ExchangeAccountId,
    string ApiKey,
    string ApiSecret,
    string Actor,
    string? CorrelationId = null)
{
    public override string ToString()
    {
        return $"{nameof(StoreExchangeCredentialsRequest)} {{ ExchangeAccountId = {ExchangeAccountId}, ApiKey = ***REDACTED***, ApiSecret = ***REDACTED***, Actor = {Actor}, CorrelationId = {CorrelationId ?? "missing"} }}";
    }
}
