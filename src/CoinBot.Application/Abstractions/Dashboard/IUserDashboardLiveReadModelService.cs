using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Dashboard;

public interface IUserDashboardLiveReadModelService
{
    Task<UserDashboardLiveSnapshot> GetSnapshotAsync(
        string userId,
        CancellationToken cancellationToken = default);
}

public sealed record UserDashboardLiveSnapshot(
    UserDashboardLiveControlSnapshot Control,
    UserDashboardLatestNoTradeSnapshot LatestNoTrade,
    UserDashboardLatestRejectSnapshot LatestReject,
    UserDashboardAiSummarySnapshot AiSummary,
    IReadOnlyCollection<UserDashboardAiHistoryRowSnapshot> AiHistory,
    IReadOnlyCollection<UserDashboardReasonBucketSnapshot> NoSubmitReasons,
    IReadOnlyCollection<UserDashboardReasonBucketSnapshot> HypotheticalBlockReasons,
    UserDashboardAiOutcomeSummarySnapshot? AiOutcomeSummary = null,
    IReadOnlyCollection<UserDashboardReasonBucketSnapshot>? OutcomeStates = null,
    IReadOnlyCollection<UserDashboardReasonBucketSnapshot>? FutureDataAvailabilityBuckets = null,
    IReadOnlyCollection<UserDashboardAiConfidenceBucketSnapshot>? OutcomeConfidenceBuckets = null);

public sealed record UserDashboardLiveControlSnapshot(
    string TradeMasterLabel,
    string TradeMasterTone,
    string TradingModeLabel,
    string TradingModeTone,
    string PilotActivationLabel,
    string PilotActivationTone,
    string MarketDataLabel,
    string MarketDataTone,
    string MarketDataSummary,
    string PrivatePlaneLabel,
    string PrivatePlaneTone,
    string PrivatePlaneSummary);

public sealed record UserDashboardLatestNoTradeSnapshot(
    string Label,
    string Tone,
    string? Code,
    string Summary,
    DateTime? OccurredAtUtc);

public sealed record UserDashboardLatestRejectSnapshot(
    string Label,
    string Tone,
    string? Code,
    string Summary,
    string? ReconciliationLabel,
    DateTime? OccurredAtUtc);

public sealed record UserDashboardAiSummarySnapshot(
    int TotalCount,
    int LongCount,
    int ShortCount,
    int NeutralCount,
    int FallbackCount,
    int AgreementCount,
    int DisagreementCount,
    decimal AverageConfidence,
    int HighConfidenceCount,
    int MediumConfidenceCount,
    int LowConfidenceCount);

public sealed record UserDashboardAiOutcomeSummarySnapshot(
    string HorizonLabel,
    int TotalDecisionCount,
    int ScoredCount,
    int FutureDataUnavailableCount,
    int ReferenceDataUnavailableCount,
    decimal AverageOutcomeScore,
    int PositiveOutcomeCount,
    int NegativeOutcomeCount,
    int NeutralOutcomeCount,
    int FalsePositiveCount,
    int FalseNeutralCount,
    int OvertradingCount,
    int SuppressionAlignedCount,
    int SuppressionMissedCount);

public sealed record UserDashboardAiConfidenceBucketSnapshot(
    string Label,
    int TotalCount,
    int ScoredCount,
    int SuccessCount,
    int FalsePositiveCount,
    int FalseNeutralCount,
    int OvertradingCount,
    decimal AverageOutcomeScore);

public sealed record UserDashboardAiHistoryRowSnapshot(
    Guid DecisionId,
    Guid BotId,
    string Symbol,
    string Timeframe,
    DateTime EvaluatedAtUtc,
    string StrategyDirection,
    int? StrategyConfidenceScore,
    string? StrategyDecisionOutcome,
    string? StrategyDecisionCode,
    string? StrategySummary,
    string AiDirection,
    decimal AiConfidence,
    string AiReasonSummary,
    string AiProviderName,
    string? AiProviderModel,
    bool AiIsFallback,
    string? AiFallbackReason,
    bool RiskVetoPresent,
    string? RiskVetoReason,
    string? RiskVetoSummary,
    bool PilotSafetyBlocked,
    string? PilotSafetyReason,
    string? PilotSafetySummary,
    string FinalAction,
    bool HypotheticalSubmitAllowed,
    string? HypotheticalBlockReason,
    string? HypotheticalBlockSummary,
    string NoSubmitReason,
    string AgreementState,
    Guid? FeatureSnapshotId,
    string? FeatureVersion,
    string? FeatureSummary,
    string? TopSignalHints,
    string? PrimaryRegime,
    string? MomentumBias,
    string? VolatilityState,
    ExecutionEnvironment TradingMode,
    ExchangeDataPlane Plane,
    AiShadowOutcomeState? OutcomeState = null,
    decimal? OutcomeScore = null,
    string? RealizedDirectionality = null,
    string OutcomeConfidenceBucket = "Low",
    AiShadowFutureDataAvailability? FutureDataAvailability = null,
    AiShadowOutcomeHorizonKind? OutcomeHorizonKind = null,
    int? OutcomeHorizonValue = null,
    bool FalsePositive = false,
    bool FalseNeutral = false,
    bool Overtrading = false,
    bool SuppressionCandidate = false,
    bool SuppressionAligned = false);

public sealed record UserDashboardReasonBucketSnapshot(string Label, int Count);
