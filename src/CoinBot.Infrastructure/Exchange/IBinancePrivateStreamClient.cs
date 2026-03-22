namespace CoinBot.Infrastructure.Exchange;

public interface IBinancePrivateStreamClient
{
    IAsyncEnumerable<BinancePrivateStreamEvent> StreamAsync(
        string listenKey,
        CancellationToken cancellationToken = default);
}
