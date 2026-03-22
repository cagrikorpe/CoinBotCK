namespace CoinBot.Application.Abstractions.MarketData;

public interface IMarketDataService
{
    ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default);

    ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default);

    ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default);

    ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default);

    IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default);
}
