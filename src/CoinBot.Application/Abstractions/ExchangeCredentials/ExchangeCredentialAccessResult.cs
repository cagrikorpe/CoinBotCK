namespace CoinBot.Application.Abstractions.ExchangeCredentials;

public sealed record ExchangeCredentialAccessResult(
    string ApiKey,
    string ApiSecret,
    ExchangeCredentialStateSnapshot State);
