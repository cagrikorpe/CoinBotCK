using CoinBot.Application.Abstractions.MarketData;

namespace CoinBot.Infrastructure.MarketData;

public interface IBinanceCandleStreamClient
{
    IAsyncEnumerable<MarketCandleSnapshot> StreamAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken cancellationToken = default);
}
