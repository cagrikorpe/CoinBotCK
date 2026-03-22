namespace CoinBot.Application.Abstractions.Indicators;

public interface IIndicatorDataService
{
    ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default);

    ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default);

    ValueTask<StrategyIndicatorSnapshot?> GetLatestAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<StrategyIndicatorSnapshot> WatchAsync(
        IEnumerable<IndicatorSubscription> subscriptions,
        CancellationToken cancellationToken = default);
}
