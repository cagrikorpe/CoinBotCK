namespace CoinBot.Infrastructure.MarketData;

public sealed record HistoricalGapFillRunSummary(
    int ScannedSymbolCount,
    int DetectedGapCount,
    int InsertedCandleCount,
    int SkippedDuplicateCount,
    int ContinuityVerifiedSymbolCount);
