namespace CoinBot.Application.Abstractions.MarketData;

public interface ISharedSymbolRegistry
{
    ValueTask<SymbolMetadataSnapshot?> GetSymbolAsync(string symbol, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<SymbolMetadataSnapshot>> ListSymbolsAsync(CancellationToken cancellationToken = default);
}
