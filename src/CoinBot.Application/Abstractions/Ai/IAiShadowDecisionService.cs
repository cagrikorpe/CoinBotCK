using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Ai;

public interface IAiShadowDecisionService
{
    Task<AiShadowDecisionSnapshot> CaptureAsync(
        AiShadowDecisionWriteRequest request,
        CancellationToken cancellationToken = default);

    Task<AiShadowDecisionSnapshot?> GetLatestAsync(
        string userId,
        Guid botId,
        string symbol,
        string timeframe,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<AiShadowDecisionSnapshot>> ListRecentAsync(
        string userId,
        Guid botId,
        string symbol,
        string timeframe,
        int take = 20,
        CancellationToken cancellationToken = default);

    Task<AiShadowDecisionSummarySnapshot> GetSummaryAsync(
        string userId,
        Guid botId,
        string symbol,
        string timeframe,
        int take = 200,
        CancellationToken cancellationToken = default);

    Task<AiShadowDecisionOutcomeSnapshot> ScoreOutcomeAsync(
        string userId,
        Guid decisionId,
        AiShadowOutcomeHorizonKind horizonKind = AiShadowOutcomeDefaults.OfficialHorizonKind,
        int horizonValue = AiShadowOutcomeDefaults.OfficialHorizonValue,
        CancellationToken cancellationToken = default);

    Task<int> EnsureOutcomeCoverageAsync(
        string userId,
        AiShadowOutcomeHorizonKind horizonKind = AiShadowOutcomeDefaults.OfficialHorizonKind,
        int horizonValue = AiShadowOutcomeDefaults.OfficialHorizonValue,
        int take = 200,
        CancellationToken cancellationToken = default);

    Task<AiShadowDecisionOutcomeSummarySnapshot> GetOutcomeSummaryAsync(
        string userId,
        AiShadowOutcomeHorizonKind horizonKind = AiShadowOutcomeDefaults.OfficialHorizonKind,
        int horizonValue = AiShadowOutcomeDefaults.OfficialHorizonValue,
        int take = 200,
        CancellationToken cancellationToken = default);
}

public static class AiShadowOutcomeDefaults
{
    public const AiShadowOutcomeHorizonKind OfficialHorizonKind = AiShadowOutcomeHorizonKind.BarsForward;
    public const int OfficialHorizonValue = 1;
}

public sealed record AiShadowDecisionWriteRequest(
    Guid Id,
    string UserId,
    Guid BotId,
    Guid? ExchangeAccountId,
    Guid? TradingStrategyId,
    Guid? TradingStrategyVersionId,
    Guid? StrategySignalId,
    Guid? StrategySignalVetoId,
    Guid? FeatureSnapshotId,
    Guid? StrategyDecisionTraceId,
    Guid? HypotheticalDecisionTraceId,
    string CorrelationId,
    string StrategyKey,
    string Symbol,
    string Timeframe,
    DateTime EvaluatedAtUtc,
    DateTime? MarketDataTimestampUtc,
    string? FeatureVersion,
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
    int AiLatencyMs,
    bool AiIsFallback,
    string? AiFallbackReason,
    bool RiskVetoPresent,
    string? RiskVetoReason,
    string? RiskVetoSummary,
    bool PilotSafetyBlocked,
    string? PilotSafetyReason,
    string? PilotSafetySummary,
    ExecutionEnvironment TradingMode,
    ExchangeDataPlane Plane,
    string FinalAction,
    bool HypotheticalSubmitAllowed,
    string? HypotheticalBlockReason,
    string? HypotheticalBlockSummary,
    string NoSubmitReason,
    string? FeatureSummary,
    string AgreementState,
    decimal AiAdvisoryScore = 0m,
    string? AiContributionSummary = null);

public sealed record AiShadowDecisionSnapshot(
    Guid Id,
    string UserId,
    Guid BotId,
    Guid? ExchangeAccountId,
    Guid? TradingStrategyId,
    Guid? TradingStrategyVersionId,
    Guid? StrategySignalId,
    Guid? StrategySignalVetoId,
    Guid? FeatureSnapshotId,
    Guid? StrategyDecisionTraceId,
    Guid? HypotheticalDecisionTraceId,
    string CorrelationId,
    string StrategyKey,
    string Symbol,
    string Timeframe,
    DateTime EvaluatedAtUtc,
    DateTime? MarketDataTimestampUtc,
    string? FeatureVersion,
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
    int AiLatencyMs,
    bool AiIsFallback,
    string? AiFallbackReason,
    bool RiskVetoPresent,
    string? RiskVetoReason,
    string? RiskVetoSummary,
    bool PilotSafetyBlocked,
    string? PilotSafetyReason,
    string? PilotSafetySummary,
    ExecutionEnvironment TradingMode,
    ExchangeDataPlane Plane,
    string FinalAction,
    bool HypotheticalSubmitAllowed,
    string? HypotheticalBlockReason,
    string? HypotheticalBlockSummary,
    string NoSubmitReason,
    string? FeatureSummary,
    string AgreementState,
    DateTime CreatedAtUtc,
    decimal AiAdvisoryScore = 0m,
    string? AiContributionSummary = null);

public sealed record AiShadowDecisionSummarySnapshot(
    string UserId,
    Guid BotId,
    string Symbol,
    string Timeframe,
    int TotalCount,
    int ShadowOnlyCount,
    int NoSubmitCount,
    int LongCount,
    int ShortCount,
    int NeutralCount,
    int FallbackCount,
    int RiskVetoCount,
    int PilotSafetyBlockCount,
    int AgreementCount,
    int DisagreementCount,
    int NotApplicableCount,
    decimal AverageAiConfidence,
    int HighConfidenceCount,
    int MediumConfidenceCount,
    int LowConfidenceCount,
    IReadOnlyCollection<AiShadowMetricBucketSnapshot> NoSubmitReasons,
    IReadOnlyCollection<AiShadowMetricBucketSnapshot> HypotheticalBlockReasons);

public sealed record AiShadowDecisionOutcomeSnapshot(
    Guid Id,
    Guid AiShadowDecisionId,
    string UserId,
    Guid BotId,
    string Symbol,
    string Timeframe,
    DateTime DecisionEvaluatedAtUtc,
    AiShadowOutcomeHorizonKind HorizonKind,
    int HorizonValue,
    AiShadowOutcomeState OutcomeState,
    decimal? OutcomeScore,
    string RealizedDirectionality,
    string ConfidenceBucket,
    AiShadowFutureDataAvailability FutureDataAvailability,
    DateTime? ReferenceCandleCloseTimeUtc,
    DateTime? FutureCandleCloseTimeUtc,
    decimal? ReferenceClosePrice,
    decimal? FutureClosePrice,
    decimal? RealizedReturn,
    bool FalsePositive,
    bool FalseNeutral,
    bool Overtrading,
    bool SuppressionCandidate,
    bool SuppressionAligned,
    DateTime ScoredAtUtc,
    DateTime CreatedAtUtc);

public sealed record AiShadowDecisionOutcomeSummarySnapshot(
    string UserId,
    AiShadowOutcomeHorizonKind HorizonKind,
    int HorizonValue,
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
    int SuppressionMissedCount,
    IReadOnlyCollection<AiShadowOutcomeConfidenceBucketSnapshot> ConfidenceBuckets,
    IReadOnlyCollection<AiShadowMetricBucketSnapshot> OutcomeStates,
    IReadOnlyCollection<AiShadowMetricBucketSnapshot> FutureDataAvailabilityBuckets);

public sealed record AiShadowMetricBucketSnapshot(string Key, int Count);

public sealed record AiShadowOutcomeConfidenceBucketSnapshot(
    string Bucket,
    int TotalCount,
    int ScoredCount,
    int SuccessCount,
    int FalsePositiveCount,
    int FalseNeutralCount,
    int OvertradingCount,
    decimal AverageOutcomeScore);
