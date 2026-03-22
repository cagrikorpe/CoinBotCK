using CoinBot.Application.Abstractions.MarketData;

namespace CoinBot.Infrastructure.MarketData;

public interface IBinanceTradeStreamClient
{
    IAsyncEnumerable<MarketPriceSnapshot> StreamAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken cancellationToken = default);
}
