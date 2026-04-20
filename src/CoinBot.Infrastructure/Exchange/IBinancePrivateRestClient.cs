using CoinBot.Application.Abstractions.Exchange;

namespace CoinBot.Infrastructure.Exchange;

public interface IBinancePrivateRestClient
{
    Task EnsureMarginTypeAsync(
        Guid exchangeAccountId,
        string symbol,
        string marginType,
        string apiKey,
        string apiSecret,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    Task EnsureLeverageAsync(
        Guid exchangeAccountId,
        string symbol,
        decimal leverage,
        string apiKey,
        string apiSecret,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    Task<BinanceOrderPlacementResult> PlaceOrderAsync(
        BinanceOrderPlacementRequest request,
        CancellationToken cancellationToken = default);

    Task<BinanceOrderStatusSnapshot> CancelOrderAsync(
        BinanceOrderCancelRequest request,
        CancellationToken cancellationToken = default);

    Task<BinanceOrderStatusSnapshot> GetOrderAsync(
        BinanceOrderQueryRequest request,
        CancellationToken cancellationToken = default);

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

    Task<IReadOnlyCollection<ExchangePositionSnapshot>> GetPositionRiskSnapshotsAsync(
        Guid exchangeAccountId,
        string ownerUserId,
        string apiKey,
        string apiSecret,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyCollection<ExchangePositionSnapshot>>(Array.Empty<ExchangePositionSnapshot>());
    }
}
