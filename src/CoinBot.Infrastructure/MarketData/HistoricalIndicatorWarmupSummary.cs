namespace CoinBot.Infrastructure.MarketData;

public sealed record HistoricalIndicatorWarmupSummary(
    string Interval,
    int RequestedSymbolCount,
    int PrimedSymbolCount,
    int LoadedCandleCount);
