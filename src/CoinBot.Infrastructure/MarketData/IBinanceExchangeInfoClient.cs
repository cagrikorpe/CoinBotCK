using CoinBot.Application.Abstractions.MarketData;

namespace CoinBot.Infrastructure.MarketData;

public interface IBinanceExchangeInfoClient
{
    Task<IReadOnlyCollection<SymbolMetadataSnapshot>> GetSymbolMetadataAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken cancellationToken = default);

    Task<DateTime?> GetServerTimeUtcAsync(CancellationToken cancellationToken = default);
}
