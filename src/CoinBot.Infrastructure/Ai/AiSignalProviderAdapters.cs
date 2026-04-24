using System.Globalization;
using System.Text.Json;
using CoinBot.Application.Abstractions.Ai;
using CoinBot.Application.Abstractions.Features;

namespace CoinBot.Infrastructure.Ai;

public interface IAiSignalProviderAdapter
{
    string Name { get; }

    Task<AiSignalProviderAdapterResponse> EvaluateAsync(
        AiSignalProviderAdapterRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record AiSignalProviderAdapterRequest(
    AiSignalEvaluationRequest EvaluationRequest,
    AiSignalOptions Options);

public sealed record AiSignalProviderAdapterResponse(
    string ProviderName,
    string? ProviderModel,
    string? Payload,
    AiSignalFallbackReason? FailureReason,
    string? FailureSummary)
{
    public static AiSignalProviderAdapterResponse Success(
        string providerName,
        string? providerModel,
        string payload)
    {
        return new AiSignalProviderAdapterResponse(providerName, providerModel, payload, null, null);
    }

    public static AiSignalProviderAdapterResponse Failure(
        string providerName,
        string? providerModel,
        AiSignalFallbackReason failureReason,
        string failureSummary)
    {
        return new AiSignalProviderAdapterResponse(providerName, providerModel, null, failureReason, failureSummary);
    }
}

public sealed class DeterministicStubAiSignalProviderAdapter : IAiSignalProviderAdapter
{
    public const string ProviderNameValue = "DeterministicStub";

    public string Name => ProviderNameValue;

    public Task<AiSignalProviderAdapterResponse> EvaluateAsync(
        AiSignalProviderAdapterRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = request.EvaluationRequest.FeatureSnapshot;

        if (snapshot is null)
        {
            return Task.FromResult(AiSignalProviderAdapterResponse.Failure(
                ProviderNameValue,
                "deterministic-v1",
                AiSignalFallbackReason.FeatureSnapshotUnavailable,
                "Feature snapshot is required for deterministic AI evaluation."));
        }

        var evaluation = Evaluate(snapshot);
        return Task.FromResult(AiSignalProviderAdapterResponse.Success(
            ProviderNameValue,
            "deterministic-v1",
            JsonSerializer.Serialize(evaluation)));
    }

    private static object Evaluate(TradingFeatureSnapshotModel snapshot)
    {
        var bullishScore = 0;
        var bearishScore = 0;
        var hints = new List<string>(capacity: 4);

        if (snapshot.Momentum.Rsi is decimal rsi)
        {
            if (rsi <= 35m)
            {
                bullishScore += 2;
                hints.Add($"RSI={FormatDecimal(rsi)} oversold");
            }
            else if (rsi >= 65m)
            {
                bearishScore += 2;
                hints.Add($"RSI={FormatDecimal(rsi)} overbought");
            }
        }

        if (snapshot.Momentum.MacdHistogram is decimal macdHistogram)
        {
            if (macdHistogram > 0m)
            {
                bullishScore++;
                hints.Add($"MACD histogram={FormatDecimal(macdHistogram)} bullish");
            }
            else if (macdHistogram < 0m)
            {
                bearishScore++;
                hints.Add($"MACD histogram={FormatDecimal(macdHistogram)} bearish");
            }
        }

        if (snapshot.Trend.Ema20 is decimal ema20 && snapshot.Trend.Ema50 is decimal ema50)
        {
            if (ema20 > ema50)
            {
                bullishScore++;
                hints.Add("EMA20 above EMA50");
            }
            else if (ema20 < ema50)
            {
                bearishScore++;
                hints.Add("EMA20 below EMA50");
            }
        }

        if (snapshot.Trend.Ema50 is decimal ema50Trend && snapshot.Trend.Ema200 is decimal ema200Trend)
        {
            if (ema50Trend > ema200Trend)
            {
                bullishScore++;
                hints.Add("EMA50 above EMA200");
            }
            else if (ema50Trend < ema200Trend)
            {
                bearishScore++;
                hints.Add("EMA50 below EMA200");
            }
        }

        if (snapshot.Volume.RelativeVolume is decimal relativeVolume)
        {
            if (relativeVolume >= 1.15m)
            {
                if (bullishScore >= bearishScore)
                {
                    bullishScore++;
                }
                else
                {
                    bearishScore++;
                }

                hints.Add($"Relative volume={FormatDecimal(relativeVolume)} elevated");
            }
        }

        if (snapshot.Volatility.BollingerPercentB is decimal percentB)
        {
            if (percentB <= 0.20m)
            {
                bullishScore++;
                hints.Add($"Bollinger %B={FormatDecimal(percentB)} near lower band");
            }
            else if (percentB >= 0.80m)
            {
                bearishScore++;
                hints.Add($"Bollinger %B={FormatDecimal(percentB)} near upper band");
            }
        }

        if (snapshot.TradingContext.HasOpenPosition)
        {
            return new
            {
                direction = AiSignalDirection.Neutral.ToString(),
                confidenceScore = 0.35m,
                reasonSummary = "Deterministic stub stayed neutral because an open position already exists."
            };
        }

        var scoreGap = bullishScore - bearishScore;
        var direction = scoreGap >= 2
            ? AiSignalDirection.Long
            : scoreGap <= -2
                ? AiSignalDirection.Short
                : AiSignalDirection.Neutral;
        var confidence = direction == AiSignalDirection.Neutral
            ? 0.45m
            : Math.Min(0.55m + (Math.Abs(scoreGap) * 0.10m), 0.95m);
        var summary = direction switch
        {
            AiSignalDirection.Long => $"Deterministic stub favored long: {string.Join("; ", hints.Take(3))}.",
            AiSignalDirection.Short => $"Deterministic stub favored short: {string.Join("; ", hints.Take(3))}.",
            _ => $"Deterministic stub stayed neutral: {string.Join("; ", hints.Take(3).DefaultIfEmpty("features were mixed"))}."
        };

        return new
        {
            direction = direction.ToString(),
            confidenceScore = confidence,
            reasonSummary = summary
        };
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}

public sealed class ShadowLinearAiSignalProviderAdapter : IAiSignalProviderAdapter
{
    public const string ProviderNameValue = "ShadowLinear";
    private const string ModelVersion = "shadow-linear-v1";
    private const decimal TrendStackWeight = 0.30m;
    private const decimal MacdSignalWeight = 0.22m;
    private const decimal RsiMidlineWeight = 0.16m;
    private const decimal BollingerPressureWeight = 0.12m;
    private const decimal RelativeVolumeConfirmationWeight = 0.10m;
    private const decimal RegimeAlignmentWeight = 0.08m;
    private const decimal MomentumBiasWeight = 0.06m;
    private const decimal NeutralThreshold = 0.18m;

    public string Name => ProviderNameValue;

    public Task<AiSignalProviderAdapterResponse> EvaluateAsync(
        AiSignalProviderAdapterRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = request.EvaluationRequest.FeatureSnapshot;

        if (snapshot is null)
        {
            return Task.FromResult(AiSignalProviderAdapterResponse.Failure(
                ProviderNameValue,
                ModelVersion,
                AiSignalFallbackReason.FeatureSnapshotUnavailable,
                "Feature snapshot is required for shadow linear AI evaluation."));
        }

        var evaluation = Evaluate(snapshot);
        return Task.FromResult(AiSignalProviderAdapterResponse.Success(
            ProviderNameValue,
            ModelVersion,
            JsonSerializer.Serialize(evaluation)));
    }

    private static object Evaluate(TradingFeatureSnapshotModel snapshot)
    {
        if (snapshot.TradingContext.HasOpenPosition)
        {
            return CreateNeutralGuardrailPayload(
                "OpenPositionPresent",
                "Open position guard held the shadow model neutral.",
                "Shadow linear model stayed neutral because an open position already exists.");
        }

        if (snapshot.TradingContext.IsInCooldown)
        {
            return CreateNeutralGuardrailPayload(
                "CooldownActive",
                "Cooldown guard held the shadow model neutral.",
                "Shadow linear model stayed neutral because the bot is in cooldown.");
        }

        var contributions = new List<AiSignalContributionSnapshot>(capacity: 8);

        if (snapshot.Trend.Ema20 is decimal ema20 &&
            snapshot.Trend.Ema50 is decimal ema50 &&
            snapshot.Trend.Ema200 is decimal ema200)
        {
            if (ema20 > ema50 && ema50 > ema200)
            {
                contributions.Add(new AiSignalContributionSnapshot(
                    "TrendEmaStackBullish",
                    TrendStackWeight,
                    "EMA20 > EMA50 > EMA200 stacked bullish."));
            }
            else if (ema20 < ema50 && ema50 < ema200)
            {
                contributions.Add(new AiSignalContributionSnapshot(
                    "TrendEmaStackBearish",
                    -TrendStackWeight,
                    "EMA20 < EMA50 < EMA200 stacked bearish."));
            }
        }

        if (snapshot.Momentum.MacdLine is decimal macdLine &&
            snapshot.Momentum.MacdSignal is decimal macdSignal)
        {
            if (macdLine > macdSignal)
            {
                contributions.Add(new AiSignalContributionSnapshot(
                    "MacdLineAboveSignal",
                    MacdSignalWeight,
                    "MACD line stayed above signal line."));
            }
            else if (macdLine < macdSignal)
            {
                contributions.Add(new AiSignalContributionSnapshot(
                    "MacdLineBelowSignal",
                    -MacdSignalWeight,
                    "MACD line stayed below signal line."));
            }
        }

        if (snapshot.Momentum.Rsi is decimal rsi)
        {
            if (rsi >= 55m)
            {
                contributions.Add(new AiSignalContributionSnapshot(
                    "RsiAboveMidline",
                    RsiMidlineWeight,
                    $"RSI={FormatDecimal(rsi)} held above the midline."));
            }
            else if (rsi <= 45m)
            {
                contributions.Add(new AiSignalContributionSnapshot(
                    "RsiBelowMidline",
                    -RsiMidlineWeight,
                    $"RSI={FormatDecimal(rsi)} held below the midline."));
            }
        }

        if (snapshot.Volatility.BollingerPercentB is decimal bollingerPercentB)
        {
            if (bollingerPercentB >= 0.60m)
            {
                contributions.Add(new AiSignalContributionSnapshot(
                    "BollingerUpperPressure",
                    BollingerPressureWeight,
                    $"Bollinger %%B={FormatDecimal(bollingerPercentB)} stayed above 0.60."));
            }
            else if (bollingerPercentB <= 0.40m)
            {
                contributions.Add(new AiSignalContributionSnapshot(
                    "BollingerLowerPressure",
                    -BollingerPressureWeight,
                    $"Bollinger %%B={FormatDecimal(bollingerPercentB)} stayed below 0.40."));
            }
        }

        if (snapshot.Volume.RelativeVolume is decimal relativeVolume &&
            relativeVolume >= 1.10m)
        {
            var preVolumeScore = contributions.Sum(item => item.Contribution);
            if (preVolumeScore > 0m)
            {
                contributions.Add(new AiSignalContributionSnapshot(
                    "RelativeVolumeTrendConfirmation",
                    RelativeVolumeConfirmationWeight,
                    $"Relative volume={FormatDecimal(relativeVolume)} confirmed bullish pressure."));
            }
            else if (preVolumeScore < 0m)
            {
                contributions.Add(new AiSignalContributionSnapshot(
                    "RelativeVolumeTrendConfirmation",
                    -RelativeVolumeConfirmationWeight,
                    $"Relative volume={FormatDecimal(relativeVolume)} confirmed bearish pressure."));
            }
        }

        if (string.Equals(snapshot.PrimaryRegime, "TrendUp", StringComparison.OrdinalIgnoreCase))
        {
            contributions.Add(new AiSignalContributionSnapshot(
                "PrimaryRegimeTrendUp",
                RegimeAlignmentWeight,
                "Primary regime classified as TrendUp."));
        }
        else if (string.Equals(snapshot.PrimaryRegime, "TrendDown", StringComparison.OrdinalIgnoreCase))
        {
            contributions.Add(new AiSignalContributionSnapshot(
                "PrimaryRegimeTrendDown",
                -RegimeAlignmentWeight,
                "Primary regime classified as TrendDown."));
        }

        if (string.Equals(snapshot.MomentumBias, "Bullish", StringComparison.OrdinalIgnoreCase))
        {
            contributions.Add(new AiSignalContributionSnapshot(
                "MomentumBiasBullish",
                MomentumBiasWeight,
                "Momentum bias classified as Bullish."));
        }
        else if (string.Equals(snapshot.MomentumBias, "Bearish", StringComparison.OrdinalIgnoreCase))
        {
            contributions.Add(new AiSignalContributionSnapshot(
                "MomentumBiasBearish",
                -MomentumBiasWeight,
                "Momentum bias classified as Bearish."));
        }

        var advisoryScore = ClampSignedScore(contributions.Sum(item => item.Contribution));
        var direction = ResolveDirection(advisoryScore);
        var confidenceScore = ResolveConfidence(direction, advisoryScore);
        var topContributions = contributions
            .OrderByDescending(item => Math.Abs(item.Contribution))
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        var contributionSummary = topContributions.Length == 0
            ? "Top contributors: mixed features."
            : $"Top contributors: {string.Join(", ", topContributions.Select(FormatContributionSummary))}.";
        var reasonSummary = direction switch
        {
            AiSignalDirection.Long => $"Shadow linear score {FormatSignedDecimal(advisoryScore)} favored Long. {contributionSummary}",
            AiSignalDirection.Short => $"Shadow linear score {FormatSignedDecimal(advisoryScore)} favored Short. {contributionSummary}",
            _ => $"Shadow linear score {FormatSignedDecimal(advisoryScore)} stayed Neutral. {contributionSummary}"
        };

        return new
        {
            direction = direction.ToString(),
            confidenceScore,
            advisoryScore,
            reasonSummary,
            contributions = contributions.Select(item => new
            {
                code = item.Code,
                contribution = item.Contribution,
                summary = item.Summary
            })
        };
    }

    private static object CreateNeutralGuardrailPayload(
        string code,
        string contributionSummary,
        string reasonSummary)
    {
        return new
        {
            direction = AiSignalDirection.Neutral.ToString(),
            confidenceScore = 0.35m,
            advisoryScore = 0m,
            reasonSummary,
            contributions = new[]
            {
                new
                {
                    code,
                    contribution = 0m,
                    summary = contributionSummary
                }
            }
        };
    }

    private static AiSignalDirection ResolveDirection(decimal advisoryScore)
    {
        if (Math.Abs(advisoryScore) < NeutralThreshold)
        {
            return AiSignalDirection.Neutral;
        }

        return advisoryScore > 0m
            ? AiSignalDirection.Long
            : AiSignalDirection.Short;
    }

    private static decimal ResolveConfidence(AiSignalDirection direction, decimal advisoryScore)
    {
        var absoluteScore = Math.Abs(advisoryScore);

        return direction == AiSignalDirection.Neutral
            ? Math.Min(0.45m + absoluteScore, 0.60m)
            : Math.Min(0.55m + (absoluteScore * 0.40m), 0.98m);
    }

    private static decimal ClampSignedScore(decimal value)
    {
        return value switch
        {
            < -1m => -1m,
            > 1m => 1m,
            _ => decimal.Round(value, 6, MidpointRounding.AwayFromZero)
        };
    }

    private static string FormatContributionSummary(AiSignalContributionSnapshot contribution)
    {
        return $"{contribution.Code} {FormatSignedDecimal(contribution.Contribution)}";
    }

    private static string FormatSignedDecimal(decimal value)
    {
        return value > 0m
            ? $"+{FormatDecimal(value)}"
            : FormatDecimal(value);
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}

public sealed class OfflineAiSignalProviderAdapter : IAiSignalProviderAdapter
{
    public const string ProviderNameValue = "Offline";

    public string Name => ProviderNameValue;

    public Task<AiSignalProviderAdapterResponse> EvaluateAsync(
        AiSignalProviderAdapterRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(AiSignalProviderAdapterResponse.Success(
            ProviderNameValue,
            "offline-v1",
            JsonSerializer.Serialize(new
            {
                direction = AiSignalDirection.Neutral.ToString(),
                confidenceScore = 0.50m,
                reasonSummary = "Offline adapter returned a neutral AI overlay."
            })));
    }
}

public sealed class OpenAiSignalProviderAdapter : IAiSignalProviderAdapter
{
    public const string ProviderNameValue = "OpenAI";

    public string Name => ProviderNameValue;

    public Task<AiSignalProviderAdapterResponse> EvaluateAsync(
        AiSignalProviderAdapterRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var model = NormalizeOptional(request.Options.OpenAiModel);
        return Task.FromResult(string.IsNullOrWhiteSpace(model)
            ? AiSignalProviderAdapterResponse.Failure(
                ProviderNameValue,
                null,
                AiSignalFallbackReason.ConfigurationMissing,
                "OpenAI provider is selected but no model is configured.")
            : AiSignalProviderAdapterResponse.Failure(
                ProviderNameValue,
                model,
                AiSignalFallbackReason.ProviderUnavailable,
                "OpenAI provider network calls are not enabled in this phase."));
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalizedValue = value?.Trim();
        return string.IsNullOrWhiteSpace(normalizedValue) ? null : normalizedValue;
    }
}

public sealed class GeminiAiSignalProviderAdapter : IAiSignalProviderAdapter
{
    public const string ProviderNameValue = "Gemini";

    public string Name => ProviderNameValue;

    public Task<AiSignalProviderAdapterResponse> EvaluateAsync(
        AiSignalProviderAdapterRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var model = NormalizeOptional(request.Options.GeminiModel);
        return Task.FromResult(string.IsNullOrWhiteSpace(model)
            ? AiSignalProviderAdapterResponse.Failure(
                ProviderNameValue,
                null,
                AiSignalFallbackReason.ConfigurationMissing,
                "Gemini provider is selected but no model is configured.")
            : AiSignalProviderAdapterResponse.Failure(
                ProviderNameValue,
                model,
                AiSignalFallbackReason.ProviderUnavailable,
                "Gemini provider network calls are not enabled in this phase."));
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalizedValue = value?.Trim();
        return string.IsNullOrWhiteSpace(normalizedValue) ? null : normalizedValue;
    }
}
