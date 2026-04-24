namespace CoinBot.Application.Abstractions.Monitoring;

public sealed record MarketScannerCandidateSnapshot(
    string Symbol,
    string UniverseSource,
    DateTime ObservedAtUtc,
    DateTime? LastCandleAtUtc,
    decimal? LastPrice,
    decimal? QuoteVolume24h,
    decimal MarketScore,
    int? StrategyScore,
    string? ScoringSummary,
    IReadOnlyCollection<string> AdvisoryLabels,
    IReadOnlyCollection<string> AdvisoryReasonCodes,
    string? AdvisorySummary,
    int? AdvisoryShadowScore,
    IReadOnlyCollection<string> AdvisoryShadowContributions,
    bool IsEligible,
    string? RejectionReason,
    decimal Score,
    int? Rank,
    bool IsTopCandidate);
