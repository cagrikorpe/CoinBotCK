using CoinBot.Domain.Enums;

namespace CoinBot.Infrastructure.Autonomy;

internal sealed record MarketAnomalySweepResult(
    DateTime EvaluatedAtUtc,
    int CandidateSymbolCount,
    IReadOnlyCollection<MarketAnomalyEvaluationSnapshot> Evaluations,
    int PolicyUpdatedCount,
    int ReviewQueuedCount,
    int AlreadyProtectedCount,
    int InsufficientDataCount,
    string Summary);

internal sealed record MarketAnomalyEvaluationSnapshot(
    string Symbol,
    SymbolRestrictionState? ProposedState,
    SymbolRestrictionState? ExistingState,
    decimal ConfidenceScore,
    MarketAnomalyDecision Decision,
    bool PolicyUpdated,
    bool ReviewQueued,
    bool IncidentWritten,
    bool UsedLatestPrice,
    bool UsedHistoricalCandles,
    bool UsedDegradedMode,
    string Summary,
    IReadOnlyCollection<string> TriggerLabels);

internal enum MarketAnomalyDecision
{
    NoAction = 0,
    PolicyUpdated = 1,
    ReviewQueued = 2,
    PolicyUpdatedAndReviewQueued = 3,
    AlreadyProtected = 4,
    InsufficientData = 5
}

internal sealed class MarketAnomalyMetrics
{
    public int SeverityScore { get; set; }

    public decimal PriceShockPercent { get; set; }

    public decimal RangeRatio { get; set; }

    public decimal? VolumeRatio { get; set; }

    public int? PriceAgeSeconds { get; set; }

    public int? CandleAgeSeconds { get; set; }
}
