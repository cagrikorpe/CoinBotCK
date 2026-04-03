using CoinBot.Application.Abstractions.MarketData;

namespace CoinBot.Infrastructure.MarketData;

public interface IBinanceDepthStreamClient
{
    IAsyncEnumerable<MarketDepthSnapshot> StreamAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken cancellationToken = default);
}
