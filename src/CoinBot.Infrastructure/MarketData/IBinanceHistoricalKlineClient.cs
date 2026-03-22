using CoinBot.Application.Abstractions.MarketData;

namespace CoinBot.Infrastructure.MarketData;

public interface IBinanceHistoricalKlineClient
{
    Task<IReadOnlyCollection<MarketCandleSnapshot>> GetClosedCandlesAsync(
        string symbol,
        string interval,
        DateTime startOpenTimeUtc,
        DateTime endOpenTimeUtc,
        int limit,
        CancellationToken cancellationToken = default);
}
