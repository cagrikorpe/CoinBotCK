using CoinBot.Application.Abstractions.Features;
using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Ai;

public interface IAiSignalEvaluator
{
    Task<AiSignalEvaluationResult> EvaluateAsync(
        AiSignalEvaluationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record AiSignalEvaluationRequest(
    TradingFeatureSnapshotModel? FeatureSnapshot,
    string Symbol,
    string Timeframe,
    ExecutionEnvironment TradingMode,
    ExchangeDataPlane Plane,
    StrategySignalType? RequestedSignalType = null,
    string? StrategyKey = null);

public sealed record AiSignalContributionSnapshot(
    string Code,
    decimal Contribution,
    string Summary);

public sealed record AiSignalEvaluationResult(
    AiSignalDirection SignalDirection,
    decimal ConfidenceScore,
    string ReasonSummary,
    Guid? FeatureSnapshotId,
    string ProviderName,
    string? ProviderModel,
    int LatencyMs,
    bool IsFallback,
    AiSignalFallbackReason? FallbackReason,
    bool RawResponseCaptured,
    DateTime EvaluatedAtUtc,
    decimal AdvisoryScore = 0m,
    IReadOnlyCollection<AiSignalContributionSnapshot>? Contributions = null)
{
    public static AiSignalEvaluationResult NeutralFallback(
        AiSignalFallbackReason fallbackReason,
        string reasonSummary,
        Guid? featureSnapshotId,
        string providerName,
        string? providerModel,
        int latencyMs,
        DateTime evaluatedAtUtc)
    {
        return new AiSignalEvaluationResult(
            AiSignalDirection.Neutral,
            0m,
            reasonSummary,
            featureSnapshotId,
            providerName,
            providerModel,
            latencyMs,
            true,
            fallbackReason,
            RawResponseCaptured: false,
            evaluatedAtUtc);
    }
}

public enum AiSignalDirection
{
    Neutral = 0,
    Long = 1,
    Short = 2
}

public enum AiSignalFallbackReason
{
    None = 0,
    Disabled = 1,
    Timeout = 2,
    InvalidPayload = 3,
    ProviderUnavailable = 4,
    UnsupportedResponse = 5,
    ConfigurationMissing = 6,
    EvaluationException = 7,
    UnsupportedProvider = 8,
    FeatureSnapshotUnavailable = 9,
    FeatureSnapshotNotReady = 10,
    FeatureSnapshotQualityFailed = 11
}
