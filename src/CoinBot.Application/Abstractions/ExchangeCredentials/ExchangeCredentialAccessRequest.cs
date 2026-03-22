namespace CoinBot.Application.Abstractions.ExchangeCredentials;

public sealed record ExchangeCredentialAccessRequest(
    Guid ExchangeAccountId,
    string Actor,
    ExchangeCredentialAccessPurpose Purpose,
    string? CorrelationId = null);
