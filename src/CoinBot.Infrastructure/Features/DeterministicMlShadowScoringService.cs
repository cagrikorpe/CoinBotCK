using System.Globalization;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;

namespace CoinBot.Infrastructure.Features;

public sealed class DeterministicMlShadowScoringService : IMlShadowScoringService
{
    internal const string ModelVersionValue = "ML-Shadow-Deterministic.v1";

    public MlShadowScoreSnapshot Evaluate(MlShadowScoreInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var featureSchemaVersion = NormalizeExplicitState(input.FeatureSchemaVersion, "Unavailable");
        if (input.FeatureSnapshot is null)
        {
            return CreateNoDecision(
                featureSchemaVersion,
                "FeatureSnapshotUnavailable",
                score: null,
                confidence: 0m);
        }

        var completenessScore = ClampScore(input.FeatureCompletenessScore);
        var baselineScore = ClampScore(input.CombinedBaselineScore);
        var riskPenalty = ClampScore(input.RiskPenalty);
        var sampleQualityScore = ResolveSampleQualityScore(input.FeatureSnapshot);
        var confidence = ClampScore((completenessScore * 0.60m) + (sampleQualityScore * 0.25m) + ((100m - riskPenalty) * 0.15m));

        if (input.FeatureSnapshot.SnapshotState != FeatureSnapshotState.Ready ||
            input.FeatureSnapshot.QualityReasonCode != FeatureSnapshotQualityReason.None ||
            completenessScore < 60m)
        {
            return CreateNoDecision(
                featureSchemaVersion,
                BuildReasonSummary(
                    "FeatureDataIncomplete",
                    baselineScore,
                    confidence,
                    completenessScore,
                    riskPenalty),
                baselineScore,
                confidence);
        }

        var rawDecision = ResolveRawDecision(baselineScore, completenessScore, riskPenalty);
        var normalizedDecision = NormalizeDecisionAgainstRuntime(rawDecision, input.GuardDecision, input.ExecutionDecision, out var runtimeReason);

        return new MlShadowScoreSnapshot(
            MlShadowScore: baselineScore,
            MlConfidence: confidence,
            MlShadowDecision: normalizedDecision,
            ModelVersion: ModelVersionValue,
            FeatureSchemaVersion: featureSchemaVersion,
            ReasonSummary: BuildReasonSummary(
                runtimeReason,
                baselineScore,
                confidence,
                completenessScore,
                riskPenalty),
            IsDecisionInfluential: false);
    }

    private static string ResolveRawDecision(decimal baselineScore, decimal completenessScore, decimal riskPenalty)
    {
        if (baselineScore >= 70m && completenessScore >= 85m && riskPenalty <= 25m)
        {
            return "WouldAllow";
        }

        if (baselineScore <= 35m || completenessScore <= 40m || riskPenalty >= 60m)
        {
            return "WouldSuppress";
        }

        return "NoDecision";
    }

    private static string NormalizeDecisionAgainstRuntime(string rawDecision, string? guardDecision, string? executionDecision, out string runtimeReason)
    {
        if (IsBlockedDecision(guardDecision) || IsBlockedDecision(executionDecision))
        {
            if (string.Equals(rawDecision, "WouldAllow", StringComparison.Ordinal))
            {
                runtimeReason = "BlockedTradeShadowAllowSuppressed";
                return "NoDecision";
            }

            runtimeReason = "BlockedTradeObserved";
            return rawDecision;
        }

        if (IsAllowedDecision(guardDecision, executionDecision))
        {
            if (string.Equals(rawDecision, "WouldSuppress", StringComparison.Ordinal))
            {
                runtimeReason = "AllowedTradeShadowSuppressSuppressed";
                return "NoDecision";
            }

            runtimeReason = "AllowedTradeObserved";
            return rawDecision;
        }

        runtimeReason = "RuntimeDecisionUnavailable";
        return rawDecision;
    }

    private static bool IsBlockedDecision(string? value)
    {
        return string.Equals(NormalizeOptional(value), "Blocked", StringComparison.Ordinal);
    }

    private static bool IsAllowedDecision(string? guardDecision, string? executionDecision)
    {
        var normalizedGuardDecision = NormalizeOptional(guardDecision);
        var normalizedExecutionDecision = NormalizeOptional(executionDecision);
        if (!string.Equals(normalizedGuardDecision, "Allowed", StringComparison.Ordinal))
        {
            return false;
        }

        return normalizedExecutionDecision is not null &&
               !string.Equals(normalizedExecutionDecision, "Blocked", StringComparison.Ordinal) &&
               !string.Equals(normalizedExecutionDecision, "NotRequested", StringComparison.Ordinal) &&
               !string.Equals(normalizedExecutionDecision, "NotEvaluated", StringComparison.Ordinal);
    }

    private static decimal ResolveSampleQualityScore(TradingFeatureSnapshot featureSnapshot)
    {
        if (featureSnapshot.RequiredSampleCount <= 0)
        {
            return 0m;
        }

        var ratio = featureSnapshot.SampleCount / (decimal)featureSnapshot.RequiredSampleCount;
        return ClampScore(decimal.Round(ratio * 100m, 4, MidpointRounding.AwayFromZero));
    }

    private static MlShadowScoreSnapshot CreateNoDecision(
        string featureSchemaVersion,
        string reasonSummary,
        decimal? score,
        decimal confidence)
    {
        return new MlShadowScoreSnapshot(
            MlShadowScore: score,
            MlConfidence: ClampScore(confidence),
            MlShadowDecision: "NoDecision",
            ModelVersion: ModelVersionValue,
            FeatureSchemaVersion: featureSchemaVersion,
            ReasonSummary: reasonSummary,
            IsDecisionInfluential: false);
    }

    private static string BuildReasonSummary(
        string reasonCode,
        decimal baselineScore,
        decimal confidence,
        decimal completenessScore,
        decimal riskPenalty)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"ReasonCode={reasonCode}; BaselineScore={baselineScore:0.####}; Confidence={confidence:0.####}; Completeness={completenessScore:0.####}; RiskPenalty={riskPenalty:0.####}");
    }

    private static decimal ClampScore(decimal? value)
    {
        if (!value.HasValue)
        {
            return 0m;
        }

        return value.Value switch
        {
            < 0m => 0m,
            > 100m => 100m,
            _ => decimal.Round(value.Value, 4, MidpointRounding.AwayFromZero)
        };
    }

    private static string NormalizeExplicitState(string? value, string fallbackValue)
    {
        var normalizedValue = NormalizeOptional(value);
        return normalizedValue ?? fallbackValue;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalizedValue = value?.Trim();
        return string.IsNullOrWhiteSpace(normalizedValue)
            ? null
            : normalizedValue;
    }
}
