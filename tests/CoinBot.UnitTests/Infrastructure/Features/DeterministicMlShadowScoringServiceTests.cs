using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Features;

namespace CoinBot.UnitTests.Infrastructure.Features;

public sealed class DeterministicMlShadowScoringServiceTests
{
    private readonly DeterministicMlShadowScoringService service = new();

    [Fact]
    public void Evaluate_ML_Shadow_ProducesWouldAllow_WhenFeatureDataValid()
    {
        var result = service.Evaluate(
            new MlShadowScoreInput(
                CreateFeatureSnapshot(FeatureSnapshotState.Ready, FeatureSnapshotQualityReason.None, 240, 200),
                CombinedBaselineScore: 76.344m,
                FeatureCompletenessScore: 100m,
                RiskPenalty: 3.5m,
                GuardDecision: "Allowed",
                ExecutionDecision: "Prepared",
                FeatureSchemaVersion: "AI-1.v1"));

        Assert.Equal(76.344m, result.MlShadowScore);
        Assert.Equal(99.475m, result.MlConfidence);
        Assert.Equal("WouldAllow", result.MlShadowDecision);
        Assert.Equal(DeterministicMlShadowScoringService.ModelVersionValue, result.ModelVersion);
        Assert.Equal("AI-1.v1", result.FeatureSchemaVersion);
        Assert.Contains("ReasonCode=AllowedTradeObserved", result.ReasonSummary, StringComparison.Ordinal);
        Assert.False(result.IsDecisionInfluential);
    }

    [Fact]
    public void Evaluate_ML_Shadow_NoDecision_WhenFeatureSnapshotMissing()
    {
        var result = service.Evaluate(
            new MlShadowScoreInput(
                FeatureSnapshot: null,
                CombinedBaselineScore: 50m,
                FeatureCompletenessScore: 0m,
                RiskPenalty: 0m,
                GuardDecision: "Allowed",
                ExecutionDecision: "Prepared",
                FeatureSchemaVersion: "Unavailable"));

        Assert.Null(result.MlShadowScore);
        Assert.Equal(0m, result.MlConfidence);
        Assert.Equal("NoDecision", result.MlShadowDecision);
        Assert.Equal(DeterministicMlShadowScoringService.ModelVersionValue, result.ModelVersion);
        Assert.Equal("Unavailable", result.FeatureSchemaVersion);
        Assert.Equal("FeatureSnapshotUnavailable", result.ReasonSummary);
        Assert.False(result.IsDecisionInfluential);
    }

    [Fact]
    public void Evaluate_ML_Shadow_NoDecision_WhenFeatureDataIncomplete()
    {
        var result = service.Evaluate(
            new MlShadowScoreInput(
                CreateFeatureSnapshot(FeatureSnapshotState.MissingData, FeatureSnapshotQualityReason.IncompleteSnapshot, 60, 200),
                CombinedBaselineScore: 30.7m,
                FeatureCompletenessScore: 5m,
                RiskPenalty: 10m,
                GuardDecision: "Allowed",
                ExecutionDecision: "Prepared",
                FeatureSchemaVersion: "AI-1.v1"));

        Assert.Equal(30.7m, result.MlShadowScore);
        Assert.Equal(24m, result.MlConfidence);
        Assert.Equal("NoDecision", result.MlShadowDecision);
        Assert.Contains("ReasonCode=FeatureDataIncomplete", result.ReasonSummary, StringComparison.Ordinal);
        Assert.False(result.IsDecisionInfluential);
    }

    [Fact]
    public void Evaluate_ML_Shadow_NoInfluence_DoesNotAllowBlockedTrade()
    {
        var result = service.Evaluate(
            new MlShadowScoreInput(
                CreateFeatureSnapshot(FeatureSnapshotState.Ready, FeatureSnapshotQualityReason.None, 240, 200),
                CombinedBaselineScore: 76.344m,
                FeatureCompletenessScore: 100m,
                RiskPenalty: 3.5m,
                GuardDecision: "Blocked",
                ExecutionDecision: "Blocked",
                FeatureSchemaVersion: "AI-1.v1"));

        Assert.Equal("NoDecision", result.MlShadowDecision);
        Assert.Contains("ReasonCode=BlockedTradeShadowAllowSuppressed", result.ReasonSummary, StringComparison.Ordinal);
        Assert.False(result.IsDecisionInfluential);
    }

    [Fact]
    public void Evaluate_ML_Shadow_NoInfluence_DoesNotSuppressAllowedTrade()
    {
        var result = service.Evaluate(
            new MlShadowScoreInput(
                CreateFeatureSnapshot(FeatureSnapshotState.Ready, FeatureSnapshotQualityReason.None, 240, 200),
                CombinedBaselineScore: 10.8m,
                FeatureCompletenessScore: 100m,
                RiskPenalty: 80m,
                GuardDecision: "Allowed",
                ExecutionDecision: "Prepared",
                FeatureSchemaVersion: "AI-1.v1"));

        Assert.Equal(10.8m, result.MlShadowScore);
        Assert.Equal("NoDecision", result.MlShadowDecision);
        Assert.Contains("ReasonCode=AllowedTradeShadowSuppressSuppressed", result.ReasonSummary, StringComparison.Ordinal);
        Assert.False(result.IsDecisionInfluential);
    }

    [Fact]
    public void Evaluate_ML_Shadow_NoInfluence_KeepsScoresWithinBounds()
    {
        var result = service.Evaluate(
            new MlShadowScoreInput(
                CreateFeatureSnapshot(FeatureSnapshotState.Ready, FeatureSnapshotQualityReason.None, 400, 200),
                CombinedBaselineScore: 150m,
                FeatureCompletenessScore: 120m,
                RiskPenalty: -10m,
                GuardDecision: "Allowed",
                ExecutionDecision: "Prepared",
                FeatureSchemaVersion: "AI-1.v1"));

        Assert.Equal(100m, result.MlShadowScore);
        Assert.Equal(100m, result.MlConfidence);
        Assert.False(result.IsDecisionInfluential);
    }

    private static TradingFeatureSnapshot CreateFeatureSnapshot(
        FeatureSnapshotState snapshotState,
        FeatureSnapshotQualityReason qualityReasonCode,
        int sampleCount,
        int requiredSampleCount)
    {
        return new TradingFeatureSnapshot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "shadow-user",
            BotId = Guid.NewGuid(),
            ExchangeAccountId = Guid.NewGuid(),
            StrategyKey = "shadow-strategy",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            EvaluatedAtUtc = new DateTime(2026, 5, 11, 8, 0, 0, DateTimeKind.Utc),
            FeatureVersion = "AI-1.v1",
            SnapshotState = snapshotState,
            QualityReasonCode = qualityReasonCode,
            SampleCount = sampleCount,
            RequiredSampleCount = requiredSampleCount
        };
    }
}
