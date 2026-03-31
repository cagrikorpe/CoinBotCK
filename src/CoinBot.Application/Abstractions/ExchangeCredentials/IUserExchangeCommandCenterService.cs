using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.ExchangeCredentials;

public interface IUserExchangeCommandCenterService
{
    Task<UserExchangeCommandCenterSnapshot> GetSnapshotAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<ConnectUserBinanceCredentialResult> ConnectBinanceAsync(
        ConnectUserBinanceCredentialRequest request,
        CancellationToken cancellationToken = default);
}
