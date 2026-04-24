using System.Collections.Generic;

namespace CoinBot.Web.ViewModels.AiRobot;

public sealed record AiRobotViewModel(
    string DisplayTimeZoneId,
    string DisplayTimeZoneJavaScriptId,
    string DisplayTimeZoneLabel,
    AiRobotSummaryViewModel Summary,
    List<AiRobotDecisionViewModel> Decisions,
    List<AiRobotBucketViewModel> NoSubmitReasons,
    List<AiRobotBucketViewModel> HypotheticalBlockReasons,
    List<AiRobotBucketViewModel> OutcomeStates,
    List<AiRobotBucketViewModel> FutureDataAvailability,
    List<AiRobotConfidenceBucketViewModel> OutcomeConfidenceBuckets);

public sealed record AiRobotSummaryViewModel(
    int TotalCount,
    int LongCount,
    int ShortCount,
    int NeutralCount,
    int FallbackCount,
    int AgreementCount,
    int DisagreementCount,
    string AverageConfidence,
    string LatestNoTradeStatus,
    string LatestNoTradeSummary,
    string EmptyStateMessage,
    string OutcomeHorizonLabel,
    string ScoringCoverage,
    string AverageOutcomeScore,
    string OutcomeMixSummary,
    string CalibrationSummary,
    string TradeMasterStatus = "Unknown",
    string TradingModeStatus = "Unknown",
    string PilotActivationStatus = "Unknown",
    string MarketReadinessStatus = "Unknown",
    string PrivatePlaneStatus = "Unknown",
    string LatestRejectStatus = "NoReject",
    string LatestRejectSummary = "Son reject kaydı yok.");

public sealed record AiRobotDecisionViewModel(
    string Time,
    string Symbol,
    string Timeframe,
    string StrategyDirection,
    string StrategyConfidence,
    string AiDirection,
    string AiConfidence,
    string Agreement,
    string FinalAction,
    string NoSubmitReason,
    string? HypotheticalBlockReason,
    string Provider,
    string ReasonSummary,
    string FeatureSnapshotReference,
    string FeatureSummary,
    string RegimeSummary,
    string RiskSummary,
    string PilotSummary,
    string OutcomeLabel,
    string OutcomeDetail,
    string Tone,
    bool IsFallback,
    string StrategySummary = "Strategy summary yok.",
    string OverlaySummary = "AI overlay uygulanmadı.",
    string FinalReasonSummary = "Final action özeti yok.",
    string TopFeatureHints = "Top feature hints yok.",
    string AdvisoryScore = "0.000",
    string ContributionSummary = "Contribution breakdown yok.");

public sealed record AiRobotBucketViewModel(string Label, int Count);

public sealed record AiRobotConfidenceBucketViewModel(
    string Label,
    int TotalCount,
    int ScoredCount,
    int SuccessCount,
    int FalsePositiveCount,
    int FalseNeutralCount,
    int OvertradingCount,
    string AverageOutcomeScore);

