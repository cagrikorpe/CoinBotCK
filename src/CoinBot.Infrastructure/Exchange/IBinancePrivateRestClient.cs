using CoinBot.Application.Abstractions.Exchange;

namespace CoinBot.Infrastructure.Exchange;

public interface IBinancePrivateRestClient
{
    Task<string> StartListenKeyAsync(string apiKey, CancellationToken cancellationToken = default);

    Task KeepAliveListenKeyAsync(string apiKey, CancellationToken cancellationToken = default);

    Task CloseListenKeyAsync(string apiKey, CancellationToken cancellationToken = default);

    Task<ExchangeAccountSnapshot> GetAccountSnapshotAsync(
        Guid exchangeAccountId,
        string ownerUserId,
        string exchangeName,
        string apiKey,
        string apiSecret,
        CancellationToken cancellationToken = default);
}
