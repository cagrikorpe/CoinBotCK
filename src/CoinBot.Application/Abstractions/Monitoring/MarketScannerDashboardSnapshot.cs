namespace CoinBot.Application.Abstractions.Monitoring;

public sealed record MarketScannerDashboardSnapshot(
    Guid? ScanCycleId,
    DateTime? LastScanCompletedAtUtc,
    int ScannedSymbolCount,
    int EligibleCandidateCount,
    string UniverseSource,
    string? BestCandidateSymbol,
    decimal? BestCandidateScore,
    IReadOnlyCollection<MarketScannerCandidateSnapshot> TopCandidates,
    IReadOnlyCollection<MarketScannerCandidateSnapshot> RejectedSamples,
    MarketScannerHandoffSnapshot LatestHandoff,
    MarketScannerHandoffSnapshot LastSuccessfulHandoff,
    MarketScannerHandoffSnapshot LastBlockedHandoff)
{
    public static MarketScannerDashboardSnapshot Empty()
    {
        return new MarketScannerDashboardSnapshot(
            ScanCycleId: null,
            LastScanCompletedAtUtc: null,
            ScannedSymbolCount: 0,
            EligibleCandidateCount: 0,
            UniverseSource: "n/a",
            BestCandidateSymbol: null,
            BestCandidateScore: null,
            TopCandidates: Array.Empty<MarketScannerCandidateSnapshot>(),
            RejectedSamples: Array.Empty<MarketScannerCandidateSnapshot>(),
            LatestHandoff: MarketScannerHandoffSnapshot.Empty(),
            LastSuccessfulHandoff: MarketScannerHandoffSnapshot.Empty(),
            LastBlockedHandoff: MarketScannerHandoffSnapshot.Empty());
    }
}
