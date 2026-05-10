using System.Globalization;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;

namespace CoinBot.Infrastructure.Features;

public static class MlBaselineScoringEngine
{
    public static MlBaselineScoreSnapshot Calculate(MlBaselineScoreInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var signalConfidenceScore = ClampScore(input.StrategyScore);
        var trendAlignmentScore = ResolveTrendAlignmentScore(
            input.SignalDirection,
            input.AdvisoryDirection,
            input.ScannerTrendAlignment,
            input.TrendState);
        var volatilityScore = ResolveStateScore(input.VolatilityScore, input.VolatilityState, unavailableScore: 25m);
        var liquidityScore = ResolveStateScore(input.LiquidityScore, input.LiquidityState, unavailableScore: 20m);
        var recentPerformanceScore = ResolveRecentPerformanceScore(input.PositionRealizedPnl, input.PositionUnrealizedPnl);
        var drawdownPenalty = ResolveDrawdownPenalty(input.PositionUnrealizedPnl);
        var sampleQualityScore = ResolveSampleQualityScore(input.FeatureSnapshot);
        var featureCompletenessScore = ResolveFeatureCompletenessScore(input.FeatureSnapshot);
        var riskPenalty = ClampScore(input.RiskPenalty);

        var weightedScore =
            (signalConfidenceScore * 0.18m) +
            (trendAlignmentScore * 0.16m) +
            (volatilityScore * 0.10m) +
            (liquidityScore * 0.10m) +
            (recentPerformanceScore * 0.10m) +
            (sampleQualityScore * 0.16m) +
            (featureCompletenessScore * 0.20m);

        var combinedBaselineScore = ClampScore(weightedScore - (riskPenalty * 0.50m) - (drawdownPenalty * 0.35m));

        return new MlBaselineScoreSnapshot(
            signalConfidenceScore,
            trendAlignmentScore,
            volatilityScore,
            liquidityScore,
            recentPerformanceScore,
            drawdownPenalty,
            sampleQualityScore,
            featureCompletenessScore,
            combinedBaselineScore,
            BuildSummary(
                signalConfidenceScore,
                trendAlignmentScore,
                volatilityScore,
                liquidityScore,
                riskPenalty,
                recentPerformanceScore,
                drawdownPenalty,
                sampleQualityScore,
                featureCompletenessScore,
                combinedBaselineScore));
    }

    private static decimal ResolveTrendAlignmentScore(
        string? signalDirection,
        string? advisoryDirection,
        string? scannerTrendAlignment,
        string? trendState)
    {
        var normalizedAlignment = NormalizeOptional(scannerTrendAlignment);
        if (normalizedAlignment is not null)
        {
            if (normalizedAlignment.StartsWith("Conflict", StringComparison.Ordinal))
            {
                return 0m;
            }

            if (normalizedAlignment.StartsWith("Aligned", StringComparison.Ordinal))
            {
                return 100m;
            }

            if (string.Equals(normalizedAlignment, "NotEvaluated", StringComparison.Ordinal))
            {
                return 50m;
            }
        }

        var normalizedSignal = NormalizeDirection(signalDirection);
        var normalizedAdvisory = NormalizeAdvisory(advisoryDirection);
        if (normalizedSignal is not null && normalizedAdvisory is not null)
        {
            return string.Equals(normalizedSignal, normalizedAdvisory, StringComparison.Ordinal)
                ? 100m
                : 0m;
        }

        return NormalizeOptional(trendState) switch
        {
            null or "Unavailable" => 25m,
            _ => 60m
        };
    }

    private static decimal ResolveStateScore(decimal? explicitScore, string? state, decimal unavailableScore)
    {
        if (explicitScore.HasValue)
        {
            return ClampScore(explicitScore.Value);
        }

        return NormalizeOptional(state) switch
        {
            null or "Unavailable" => unavailableScore,
            "High" => 90m,
            "Moderate" => 60m,
            "Low" => 25m,
            "Compressed" => 65m,
            "Normal" => 70m,
            "Expanded" => 45m,
            "Extreme" => 20m,
            _ => 50m
        };
    }

    private static decimal ResolveRecentPerformanceScore(decimal? realizedPnl, decimal? unrealizedPnl)
    {
        var referencePnl = realizedPnl ?? unrealizedPnl;
        if (!referencePnl.HasValue)
        {
            return 50m;
        }

        var magnitude = Math.Min(Math.Abs(referencePnl.Value), 50m);
        return ClampScore(referencePnl.Value >= 0m ? 50m + magnitude : 50m - magnitude);
    }

    private static decimal ResolveDrawdownPenalty(decimal? unrealizedPnl)
    {
        if (!unrealizedPnl.HasValue || unrealizedPnl.Value >= 0m)
        {
            return 0m;
        }

        return ClampScore(Math.Min(Math.Abs(unrealizedPnl.Value), 100m));
    }

    private static decimal ResolveSampleQualityScore(TradingFeatureSnapshot? featureSnapshot)
    {
        if (featureSnapshot is null || featureSnapshot.RequiredSampleCount <= 0)
        {
            return 0m;
        }

        var ratio = featureSnapshot.SampleCount / (decimal)featureSnapshot.RequiredSampleCount;
        return ClampScore(decimal.Round(ratio * 100m, 4, MidpointRounding.AwayFromZero));
    }

    private static decimal ResolveFeatureCompletenessScore(TradingFeatureSnapshot? featureSnapshot)
    {
        if (featureSnapshot is null)
        {
            return 0m;
        }

        var score = featureSnapshot.SnapshotState switch
        {
            FeatureSnapshotState.Ready => 100m,
            FeatureSnapshotState.WarmingUp => 60m,
            FeatureSnapshotState.Stale => 35m,
            FeatureSnapshotState.MissingData => 20m,
            FeatureSnapshotState.Invalid => 0m,
            _ => 0m
        };

        if (featureSnapshot.QualityReasonCode != FeatureSnapshotQualityReason.None)
        {
            score -= 15m;
        }

        var missingFeatureCount = CountMissingFeatures(featureSnapshot.MissingFeatureSummary);
        score -= Math.Min(30m, missingFeatureCount * 10m);

        return ClampScore(score);
    }

    private static int CountMissingFeatures(string? summary)
    {
        var normalizedSummary = NormalizeOptional(summary);
        if (normalizedSummary is null ||
            string.Equals(normalizedSummary, "none", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return normalizedSummary
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
    }

    private static string BuildSummary(
        decimal signalConfidenceScore,
        decimal trendAlignmentScore,
        decimal volatilityScore,
        decimal liquidityScore,
        decimal riskPenalty,
        decimal recentPerformanceScore,
        decimal drawdownPenalty,
        decimal sampleQualityScore,
        decimal featureCompletenessScore,
        decimal combinedBaselineScore)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"SignalConfidence={signalConfidenceScore:0.####}; TrendAlignmentScore={trendAlignmentScore:0.####}; VolatilityScore={volatilityScore:0.####}; LiquidityScore={liquidityScore:0.####}; RiskPenalty={riskPenalty:0.####}; RecentPerformanceScore={recentPerformanceScore:0.####}; DrawdownPenalty={drawdownPenalty:0.####}; SampleQualityScore={sampleQualityScore:0.####}; FeatureCompletenessScore={featureCompletenessScore:0.####}; CombinedBaselineScore={combinedBaselineScore:0.####}");
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

    private static string? NormalizeDirection(string? value)
    {
        return NormalizeOptional(value) switch
        {
            "Long" => "Bullish",
            "Short" => "Bearish",
            _ => null
        };
    }

    private static string? NormalizeAdvisory(string? value)
    {
        return NormalizeOptional(value) switch
        {
            "Bullish" => "Bullish",
            "Bearish" => "Bearish",
            _ => null
        };
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalizedValue = value?.Trim();
        return string.IsNullOrWhiteSpace(normalizedValue)
            ? null
            : normalizedValue;
    }
}

public sealed record MlBaselineScoreInput(
    int? StrategyScore,
    decimal? VolatilityScore,
    decimal? LiquidityScore,
    decimal? RiskPenalty,
    string? SignalDirection,
    string? AdvisoryDirection,
    string? ScannerTrendAlignment,
    string? TrendState,
    string? VolatilityState,
    string? LiquidityState,
    TradingFeatureSnapshot? FeatureSnapshot,
    decimal? PositionRealizedPnl,
    decimal? PositionUnrealizedPnl);

public sealed record MlBaselineScoreSnapshot(
    decimal SignalConfidenceScore,
    decimal TrendAlignmentScore,
    decimal VolatilityScore,
    decimal LiquidityScore,
    decimal RecentPerformanceScore,
    decimal DrawdownPenalty,
    decimal SampleQualityScore,
    decimal FeatureCompletenessScore,
    decimal CombinedBaselineScore,
    string Summary);
