using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Features;

public interface IMlFeatureSnapshotService
{
    Task<MlFeatureSnapshotModel> CaptureAsync(
        MlFeatureSnapshotCaptureRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record MlFeatureSnapshotCaptureRequest(
    string OwnerUserId,
    Guid BotId,
    Guid? ExchangeAccountId,
    Guid? TradingStrategyVersionId,
    string StrategyKey,
    string Symbol,
    string Timeframe,
    DateTime CapturedAtUtc,
    decimal? ScannerScore,
    decimal? MarketScore,
    int? StrategyScore,
    decimal? RiskPenalty,
    string? CandidateScoringSummary,
    string? StrategyDirection,
    string GuardDecision,
    string? GuardReasonCode,
    string ExecutionDecision,
    StrategySignalType? OrderSignalType = null,
    ExecutionOrderState? OrderState = null,
    bool? SubmittedToBroker = null,
    bool? ReduceOnly = null,
    ExecutionEnvironment? ExecutionEnvironment = null);

public sealed record MlFeatureSnapshotModel(
    Guid Id,
    string Symbol,
    string Timeframe,
    string StrategyKey,
    string? StrategyTemplateKey,
    int? StrategySchemaVersion,
    string SignalDirection,
    decimal? ScannerScore,
    decimal? MarketScore,
    int? StrategyScore,
    decimal? RiskPenalty,
    string TrendState,
    string VolatilityState,
    string LiquidityState,
    string MarketFreshnessState,
    string? MarketFreshnessReason,
    string? MarketFreshnessSource,
    string HistoricalFallbackState,
    string PrivatePlaneFreshnessState,
    string? PrivatePlaneFreshnessReason,
    string GuardDecision,
    string? GuardReasonCode,
    string ExecutionDecision,
    StrategySignalType? OrderSignalType,
    ExecutionOrderState? OrderState,
    bool? SubmittedToBroker,
    bool? ReduceOnly,
    decimal? PositionUnrealizedPnl,
    decimal? PositionRealizedPnl,
    string FeatureCompletenessState,
    string FeatureCompletenessSummary,
    string SchemaVersion,
    DateTime CapturedAtUtc,
    DateTime? FeatureAnchorTimeUtc,
    DateTime? MarketDataTimestampUtc,
    ExecutionEnvironment? ExecutionEnvironment);
