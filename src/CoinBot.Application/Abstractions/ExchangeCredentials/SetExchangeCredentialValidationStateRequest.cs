namespace CoinBot.Application.Abstractions.ExchangeCredentials;

public sealed record SetExchangeCredentialValidationStateRequest(
    Guid ExchangeAccountId,
    bool IsValid,
    string Actor,
    string? CorrelationId = null);
