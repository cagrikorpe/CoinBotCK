namespace CoinBot.Infrastructure.MarketData;

internal sealed record TrackedSymbolSnapshot(
    IReadOnlyCollection<string> Symbols,
    long Version);
