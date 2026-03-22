namespace CoinBot.Application.Abstractions.ExchangeCredentials;

public sealed record StoreExchangeCredentialsRequest(
    Guid ExchangeAccountId,
    string ApiKey,
    string ApiSecret,
    string Actor,
    string? CorrelationId = null);
