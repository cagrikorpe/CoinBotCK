using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Features;

namespace CoinBot.UnitTests.Infrastructure.Features;

public sealed class MlBaselineScoringEngineTests
{
    [Fact]
    public void Calculate_ReturnsDeterministicExplainableScores_ForCompleteInput()
    {
        var featureSnapshot = CreateFeatureSnapshot(
            FeatureSnapshotState.Ready,
            FeatureSnapshotQualityReason.None,
            sampleCount: 240,
            requiredSampleCount: 200,
            missingFeatureSummary: null);

        var score = MlBaselineScoringEngine.Calculate(
            new MlBaselineScoreInput(
                StrategyScore: 72,
                VolatilityScore: 45m,
                LiquidityScore: 88m,
                RiskPenalty: 3.5m,
                SignalDirection: "Long",
                AdvisoryDirection: null,
                ScannerTrendAlignment: null,
                TrendState: "BullTrend",
                VolatilityState: "Compressed",
                LiquidityState: "High",
                FeatureSnapshot: featureSnapshot,
                PositionRealizedPnl: null,
                PositionUnrealizedPnl: 12.34m));

        Assert.Equal(72m, score.SignalConfidenceScore);
        Assert.Equal(60m, score.TrendAlignmentScore);
        Assert.Equal(45m, score.VolatilityScore);
        Assert.Equal(88m, score.LiquidityScore);
        Assert.Equal(62.34m, score.RecentPerformanceScore);
        Assert.Equal(0m, score.DrawdownPenalty);
        Assert.Equal(100m, score.SampleQualityScore);
        Assert.Equal(100m, score.FeatureCompletenessScore);
        Assert.Equal(76.344m, score.CombinedBaselineScore);
        Assert.Contains("SignalConfidence=72", score.Summary, StringComparison.Ordinal);
        Assert.Contains("CombinedBaselineScore=76.344", score.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Calculate_LowersCompletenessScore_WhenFeatureDataIsMissingOrIncomplete()
    {
        var incompleteSnapshot = CreateFeatureSnapshot(
            FeatureSnapshotState.MissingData,
            FeatureSnapshotQualityReason.IncompleteSnapshot,
            sampleCount: 40,
            requiredSampleCount: 200,
            missingFeatureSummary: "ATR,RSI");

        var incomplete = MlBaselineScoringEngine.Calculate(
            new MlBaselineScoreInput(
                StrategyScore: null,
                VolatilityScore: null,
                LiquidityScore: null,
                RiskPenalty: null,
                SignalDirection: null,
                AdvisoryDirection: null,
                ScannerTrendAlignment: null,
                TrendState: "Unavailable",
                VolatilityState: "Unavailable",
                LiquidityState: "Unavailable",
                FeatureSnapshot: incompleteSnapshot,
                PositionRealizedPnl: null,
                PositionUnrealizedPnl: null));

        Assert.Equal(20m, incomplete.SampleQualityScore);
        Assert.Equal(0m, incomplete.FeatureCompletenessScore);
        Assert.Equal(16.7m, incomplete.CombinedBaselineScore);
    }

    [Fact]
    public void Calculate_AppliesRiskPenalty_AndBoundsScores()
    {
        var featureSnapshot = CreateFeatureSnapshot(
            FeatureSnapshotState.Ready,
            FeatureSnapshotQualityReason.None,
            sampleCount: 500,
            requiredSampleCount: 100,
            missingFeatureSummary: null);

        var withoutRisk = MlBaselineScoringEngine.Calculate(
            new MlBaselineScoreInput(
                StrategyScore: 140,
                VolatilityScore: 130m,
                LiquidityScore: 120m,
                RiskPenalty: 0m,
                SignalDirection: "Short",
                AdvisoryDirection: "Bearish",
                ScannerTrendAlignment: "AlignedShort",
                TrendState: "BearTrend",
                VolatilityState: "High",
                LiquidityState: "High",
                FeatureSnapshot: featureSnapshot,
                PositionRealizedPnl: 200m,
                PositionUnrealizedPnl: -500m));

        var withRisk = MlBaselineScoringEngine.Calculate(
            new MlBaselineScoreInput(
                StrategyScore: 140,
                VolatilityScore: 130m,
                LiquidityScore: 120m,
                RiskPenalty: 80m,
                SignalDirection: "Short",
                AdvisoryDirection: "Bearish",
                ScannerTrendAlignment: "AlignedShort",
                TrendState: "BearTrend",
                VolatilityState: "High",
                LiquidityState: "High",
                FeatureSnapshot: featureSnapshot,
                PositionRealizedPnl: 200m,
                PositionUnrealizedPnl: -500m));

        Assert.Equal(100m, withoutRisk.SignalConfidenceScore);
        Assert.Equal(100m, withoutRisk.VolatilityScore);
        Assert.Equal(100m, withoutRisk.LiquidityScore);
        Assert.Equal(100m, withoutRisk.SampleQualityScore);
        Assert.Equal(100m, withoutRisk.FeatureCompletenessScore);
        Assert.Equal(100m, withoutRisk.RecentPerformanceScore);
        Assert.Equal(100m, withoutRisk.DrawdownPenalty);
        Assert.Equal(65m, withoutRisk.CombinedBaselineScore);
        Assert.Equal(25m, withRisk.CombinedBaselineScore);
        Assert.True(withRisk.CombinedBaselineScore < withoutRisk.CombinedBaselineScore);
        Assert.InRange(withRisk.CombinedBaselineScore, 0m, 100m);
        Assert.InRange(withoutRisk.CombinedBaselineScore, 0m, 100m);
    }

    private static TradingFeatureSnapshot CreateFeatureSnapshot(
        FeatureSnapshotState snapshotState,
        FeatureSnapshotQualityReason qualityReason,
        int sampleCount,
        int requiredSampleCount,
        string? missingFeatureSummary)
    {
        return new TradingFeatureSnapshot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "ml-score-user",
            BotId = Guid.NewGuid(),
            StrategyKey = "ml-score-strategy",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            EvaluatedAtUtc = new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc),
            SnapshotState = snapshotState,
            QualityReasonCode = qualityReason,
            SampleCount = sampleCount,
            RequiredSampleCount = requiredSampleCount,
            MissingFeatureSummary = missingFeatureSummary
        };
    }
}
