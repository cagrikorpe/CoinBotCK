namespace CoinBot.Application.Abstractions.ExchangeCredentials;

public interface IExchangeCredentialService
{
    Task<ExchangeCredentialStateSnapshot> StoreAsync(
        StoreExchangeCredentialsRequest request,
        CancellationToken cancellationToken = default);

    Task<ExchangeCredentialAccessResult> GetAsync(
        ExchangeCredentialAccessRequest request,
        CancellationToken cancellationToken = default);

    Task<ExchangeCredentialStateSnapshot> SetValidationStateAsync(
        SetExchangeCredentialValidationStateRequest request,
        CancellationToken cancellationToken = default);

    Task<ExchangeCredentialStateSnapshot> GetStateAsync(
        Guid exchangeAccountId,
        CancellationToken cancellationToken = default);
}
