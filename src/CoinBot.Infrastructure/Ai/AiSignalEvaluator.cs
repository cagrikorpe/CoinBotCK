using System.Diagnostics;
using System.Text.Json;
using CoinBot.Application.Abstractions.Ai;
using CoinBot.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Ai;

public sealed class AiSignalEvaluator(
    IEnumerable<IAiSignalProviderAdapter> providerAdapters,
    IOptions<AiSignalOptions> options,
    TimeProvider timeProvider,
    ILogger<AiSignalEvaluator> logger) : IAiSignalEvaluator
{
    private readonly IReadOnlyDictionary<string, IAiSignalProviderAdapter> providers = providerAdapters
        .GroupBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

    private readonly AiSignalOptions optionsValue = options.Value;

    public async Task<AiSignalEvaluationResult> EvaluateAsync(
        AiSignalEvaluationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        if (!optionsValue.Enabled)
        {
            return CreateFallback(
                AiSignalFallbackReason.Disabled,
                "AI signal evaluation is disabled.",
                request.FeatureSnapshot?.Id,
                providerName: NormalizeProviderName(optionsValue.SelectedProvider),
                providerModel: null,
                latencyMs: 0,
                evaluatedAtUtc: nowUtc);
        }

        if (request.FeatureSnapshot is null)
        {
            return CreateFallback(
                AiSignalFallbackReason.FeatureSnapshotUnavailable,
                "Feature snapshot is unavailable for AI evaluation.",
                null,
                providerName: NormalizeProviderName(optionsValue.SelectedProvider),
                providerModel: null,
                latencyMs: 0,
                evaluatedAtUtc: nowUtc);
        }

        if (request.FeatureSnapshot.SnapshotState != FeatureSnapshotState.Ready)
        {
            return CreateFallback(
                AiSignalFallbackReason.FeatureSnapshotNotReady,
                $"Feature snapshot state '{request.FeatureSnapshot.SnapshotState}' is not ready for AI evaluation.",
                request.FeatureSnapshot.Id,
                providerName: NormalizeProviderName(optionsValue.SelectedProvider),
                providerModel: null,
                latencyMs: 0,
                evaluatedAtUtc: nowUtc);
        }

        var providerName = NormalizeProviderName(optionsValue.SelectedProvider);
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return CreateFallback(
                AiSignalFallbackReason.ConfigurationMissing,
                "AI provider selection is missing.",
                request.FeatureSnapshot.Id,
                providerName: "Unknown",
                providerModel: null,
                latencyMs: 0,
                evaluatedAtUtc: nowUtc);
        }

        if (!providers.TryGetValue(providerName, out var adapter))
        {
            return CreateFallback(
                AiSignalFallbackReason.UnsupportedProvider,
                $"AI provider '{providerName}' is not supported.",
                request.FeatureSnapshot.Id,
                providerName,
                providerModel: null,
                latencyMs: 0,
                evaluatedAtUtc: nowUtc);
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var timeoutTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutTokenSource.CancelAfter(optionsValue.TimeoutMs);

            var response = await adapter.EvaluateAsync(
                new AiSignalProviderAdapterRequest(request, optionsValue),
                timeoutTokenSource.Token);
            var latencyMs = ResolveLatencyMilliseconds(stopwatch.ElapsedMilliseconds);

            if (response.FailureReason.HasValue)
            {
                return CreateFallback(
                    response.FailureReason.Value,
                    response.FailureSummary ?? "AI provider returned a fallback response.",
                    request.FeatureSnapshot.Id,
                    response.ProviderName,
                    response.ProviderModel,
                    latencyMs,
                    nowUtc);
            }

            if (string.IsNullOrWhiteSpace(response.Payload))
            {
                return CreateFallback(
                    AiSignalFallbackReason.InvalidPayload,
                    "AI provider returned an empty payload.",
                    request.FeatureSnapshot.Id,
                    response.ProviderName,
                    response.ProviderModel,
                    latencyMs,
                    nowUtc);
            }

            return ParsePayload(
                response.ProviderName,
                response.ProviderModel,
                response.Payload,
                request.FeatureSnapshot.Id,
                latencyMs,
                nowUtc);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateFallback(
                AiSignalFallbackReason.Timeout,
                "AI provider timed out and returned a neutral fallback.",
                request.FeatureSnapshot.Id,
                providerName,
                providerModel: null,
                ResolveLatencyMilliseconds(stopwatch.ElapsedMilliseconds),
                nowUtc);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "AI signal evaluation failed for provider {ProviderName}.",
                providerName);

            return CreateFallback(
                AiSignalFallbackReason.EvaluationException,
                "AI evaluation raised an exception and returned a neutral fallback.",
                request.FeatureSnapshot.Id,
                providerName,
                providerModel: null,
                ResolveLatencyMilliseconds(stopwatch.ElapsedMilliseconds),
                nowUtc);
        }
    }

    private AiSignalEvaluationResult ParsePayload(
        string providerName,
        string? providerModel,
        string payload,
        Guid featureSnapshotId,
        int latencyMs,
        DateTime evaluatedAtUtc)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var rawDirection = ResolveString(root, "direction", "signalDirection", "signal");
            var rawReasonSummary = ResolveString(root, "reasonSummary", "summary", "reason");
            var confidenceScore = ResolveDecimal(root, "confidenceScore", "confidence");

            if (string.IsNullOrWhiteSpace(rawDirection) || !confidenceScore.HasValue)
            {
                return CreateFallback(
                    AiSignalFallbackReason.InvalidPayload,
                    "AI provider payload is missing direction or confidence.",
                    featureSnapshotId,
                    providerName,
                    providerModel,
                    latencyMs,
                    evaluatedAtUtc);
            }

            if (!Enum.TryParse<AiSignalDirection>(rawDirection.Trim(), ignoreCase: true, out var direction))
            {
                return CreateFallback(
                    AiSignalFallbackReason.UnsupportedResponse,
                    $"AI provider returned unsupported direction '{rawDirection.Trim()}'.",
                    featureSnapshotId,
                    providerName,
                    providerModel,
                    latencyMs,
                    evaluatedAtUtc);
            }

            if (confidenceScore.Value < 0m || confidenceScore.Value > 1m)
            {
                return CreateFallback(
                    AiSignalFallbackReason.UnsupportedResponse,
                    "AI provider returned an unsupported confidence score.",
                    featureSnapshotId,
                    providerName,
                    providerModel,
                    latencyMs,
                    evaluatedAtUtc);
            }

            var summary = TrimReason(
                string.IsNullOrWhiteSpace(rawReasonSummary)
                    ? BuildDefaultSummary(direction)
                    : rawReasonSummary!);

            return new AiSignalEvaluationResult(
                direction,
                confidenceScore.Value,
                summary,
                featureSnapshotId,
                providerName,
                providerModel,
                latencyMs,
                IsFallback: false,
                FallbackReason: null,
                RawResponseCaptured: false,
                evaluatedAtUtc);
        }
        catch (JsonException)
        {
            return CreateFallback(
                AiSignalFallbackReason.InvalidPayload,
                "AI provider payload could not be parsed.",
                featureSnapshotId,
                providerName,
                providerModel,
                latencyMs,
                evaluatedAtUtc);
        }
    }

    private AiSignalEvaluationResult CreateFallback(
        AiSignalFallbackReason reason,
        string summary,
        Guid? featureSnapshotId,
        string providerName,
        string? providerModel,
        int latencyMs,
        DateTime evaluatedAtUtc)
    {
        return AiSignalEvaluationResult.NeutralFallback(
            reason,
            TrimReason(summary),
            featureSnapshotId,
            providerName,
            providerModel,
            latencyMs,
            evaluatedAtUtc);
    }

    private static string? ResolveString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var propertyValue))
            {
                continue;
            }

            return propertyValue.ValueKind switch
            {
                JsonValueKind.String => propertyValue.GetString(),
                JsonValueKind.Number => propertyValue.GetRawText(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                _ => propertyValue.GetRawText()
            };
        }

        return null;
    }

    private static decimal? ResolveDecimal(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var propertyValue))
            {
                continue;
            }

            if (propertyValue.ValueKind == JsonValueKind.Number && propertyValue.TryGetDecimal(out var decimalValue))
            {
                return decimalValue;
            }

            if (propertyValue.ValueKind == JsonValueKind.String &&
                decimal.TryParse(propertyValue.GetString(), out var parsedValue))
            {
                return parsedValue;
            }
        }

        return null;
    }

    private static int ResolveLatencyMilliseconds(long elapsedMilliseconds)
    {
        return elapsedMilliseconds <= 0L
            ? 0
            : elapsedMilliseconds >= int.MaxValue
                ? int.MaxValue
                : (int)elapsedMilliseconds;
    }

    private string TrimReason(string value)
    {
        var normalizedValue = value.Trim();
        return normalizedValue.Length <= optionsValue.MaxReasonLength
            ? normalizedValue
            : normalizedValue[..optionsValue.MaxReasonLength];
    }

    private static string NormalizeProviderName(string? providerName)
    {
        var normalizedProviderName = providerName?.Trim();
        return string.IsNullOrWhiteSpace(normalizedProviderName)
            ? string.Empty
            : normalizedProviderName;
    }

    private static string BuildDefaultSummary(AiSignalDirection direction)
    {
        return direction switch
        {
            AiSignalDirection.Long => "AI provider favored a long setup.",
            AiSignalDirection.Short => "AI provider favored a short setup.",
            _ => "AI provider stayed neutral."
        };
    }
}
