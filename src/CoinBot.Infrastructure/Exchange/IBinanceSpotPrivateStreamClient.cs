namespace CoinBot.Infrastructure.Exchange;

public interface IBinanceSpotPrivateStreamClient
{
    IAsyncEnumerable<BinancePrivateStreamEvent> StreamAsync(
        string listenKey,
        CancellationToken cancellationToken = default);
}
