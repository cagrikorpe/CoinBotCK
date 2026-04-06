using CoinBot.Application.Abstractions.Ai;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Ai;

public sealed class AiShadowDecisionService(ApplicationDbContext dbContext) : IAiShadowDecisionService
{
    public async Task<AiShadowDecisionSnapshot> CaptureAsync(
        AiShadowDecisionWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedUserId = dbContext.EnsureCurrentUserScope(request.UserId);
        var entity = new AiShadowDecision
        {
            Id = request.Id == Guid.Empty ? Guid.NewGuid() : request.Id,
            OwnerUserId = normalizedUserId,
            BotId = request.BotId,
            ExchangeAccountId = request.ExchangeAccountId,
            TradingStrategyId = request.TradingStrategyId,
            TradingStrategyVersionId = request.TradingStrategyVersionId,
            StrategySignalId = request.StrategySignalId,
            StrategySignalVetoId = request.StrategySignalVetoId,
            FeatureSnapshotId = request.FeatureSnapshotId,
            StrategyDecisionTraceId = request.StrategyDecisionTraceId,
            HypotheticalDecisionTraceId = request.HypotheticalDecisionTraceId,
            CorrelationId = NormalizeRequired(request.CorrelationId, 128, nameof(request.CorrelationId)),
            StrategyKey = NormalizeRequired(request.StrategyKey, 128, nameof(request.StrategyKey)),
            Symbol = NormalizeRequired(request.Symbol, 32, nameof(request.Symbol)).ToUpperInvariant(),
            Timeframe = NormalizeRequired(request.Timeframe, 16, nameof(request.Timeframe)),
            EvaluatedAtUtc = NormalizeTimestamp(request.EvaluatedAtUtc),
            MarketDataTimestampUtc = request.MarketDataTimestampUtc?.ToUniversalTime(),
            FeatureVersion = NormalizeOptional(request.FeatureVersion, 32),
            StrategyDirection = NormalizeDirection(request.StrategyDirection),
            StrategyConfidenceScore = request.StrategyConfidenceScore is >= 0 and <= 100
                ? request.StrategyConfidenceScore
                : null,
            StrategyDecisionOutcome = NormalizeOptional(request.StrategyDecisionOutcome, 64),
            StrategyDecisionCode = NormalizeOptional(request.StrategyDecisionCode, 64),
            StrategySummary = NormalizeOptional(request.StrategySummary, 1024),
            AiDirection = NormalizeDirection(request.AiDirection),
            AiConfidence = NormalizeConfidence(request.AiConfidence),
            AiReasonSummary = NormalizeRequired(request.AiReasonSummary, 512, nameof(request.AiReasonSummary)),
            AiProviderName = NormalizeRequired(request.AiProviderName, 64, nameof(request.AiProviderName)),
            AiProviderModel = NormalizeOptional(request.AiProviderModel, 128),
            AiLatencyMs = Math.Max(0, request.AiLatencyMs),
            AiIsFallback = request.AiIsFallback,
            AiFallbackReason = NormalizeOptional(request.AiFallbackReason, 64),
            RiskVetoPresent = request.RiskVetoPresent,
            RiskVetoReason = NormalizeOptional(request.RiskVetoReason, 64),
            RiskVetoSummary = NormalizeOptional(request.RiskVetoSummary, 1024),
            PilotSafetyBlocked = request.PilotSafetyBlocked,
            PilotSafetyReason = NormalizeOptional(request.PilotSafetyReason, 64),
            PilotSafetySummary = NormalizeOptional(request.PilotSafetySummary, 1024),
            TradingMode = request.TradingMode,
            Plane = request.Plane,
            FinalAction = NormalizeFinalAction(request.FinalAction),
            HypotheticalSubmitAllowed = request.HypotheticalSubmitAllowed,
            HypotheticalBlockReason = NormalizeOptional(request.HypotheticalBlockReason, 64),
            HypotheticalBlockSummary = NormalizeOptional(request.HypotheticalBlockSummary, 1024),
            NoSubmitReason = NormalizeRequired(request.NoSubmitReason, 64, nameof(request.NoSubmitReason)),
            FeatureSummary = NormalizeOptional(request.FeatureSummary, 1024),
            AgreementState = NormalizeAgreementState(request.AgreementState)
        };

        dbContext.AiShadowDecisions.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Map(entity);
    }

    public async Task<AiShadowDecisionSnapshot?> GetLatestAsync(
        string userId,
        Guid botId,
        string symbol,
        string timeframe,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = dbContext.EnsureCurrentUserScope(userId);
        var normalizedSymbol = NormalizeRequired(symbol, 32, nameof(symbol)).ToUpperInvariant();
        var normalizedTimeframe = NormalizeRequired(timeframe, 16, nameof(timeframe));

        var entity = await dbContext.AiShadowDecisions
            .AsNoTracking()
            .Where(item => item.OwnerUserId == normalizedUserId &&
                           item.BotId == botId &&
                           item.Symbol == normalizedSymbol &&
                           item.Timeframe == normalizedTimeframe &&
                           !item.IsDeleted)
            .OrderByDescending(item => item.EvaluatedAtUtc)
            .ThenByDescending(item => item.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyCollection<AiShadowDecisionSnapshot>> ListRecentAsync(
        string userId,
        Guid botId,
        string symbol,
        string timeframe,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = dbContext.EnsureCurrentUserScope(userId);
        var normalizedSymbol = NormalizeRequired(symbol, 32, nameof(symbol)).ToUpperInvariant();
        var normalizedTimeframe = NormalizeRequired(timeframe, 16, nameof(timeframe));
        var normalizedTake = take <= 0 ? 20 : Math.Min(take, 200);

        var entities = await dbContext.AiShadowDecisions
            .AsNoTracking()
            .Where(item => item.OwnerUserId == normalizedUserId &&
                           item.BotId == botId &&
                           item.Symbol == normalizedSymbol &&
                           item.Timeframe == normalizedTimeframe &&
                           !item.IsDeleted)
            .OrderByDescending(item => item.EvaluatedAtUtc)
            .ThenByDescending(item => item.CreatedDate)
            .Take(normalizedTake)
            .ToListAsync(cancellationToken);

        return entities.Select(Map).ToArray();
    }

    public async Task<AiShadowDecisionSummarySnapshot> GetSummaryAsync(
        string userId,
        Guid botId,
        string symbol,
        string timeframe,
        int take = 200,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = dbContext.EnsureCurrentUserScope(userId);
        var normalizedSymbol = NormalizeRequired(symbol, 32, nameof(symbol)).ToUpperInvariant();
        var normalizedTimeframe = NormalizeRequired(timeframe, 16, nameof(timeframe));
        var normalizedTake = take <= 0 ? 200 : Math.Min(take, 1000);

        var rows = await dbContext.AiShadowDecisions
            .AsNoTracking()
            .Where(item => item.OwnerUserId == normalizedUserId &&
                           item.BotId == botId &&
                           item.Symbol == normalizedSymbol &&
                           item.Timeframe == normalizedTimeframe &&
                           !item.IsDeleted)
            .OrderByDescending(item => item.EvaluatedAtUtc)
            .ThenByDescending(item => item.CreatedDate)
            .Take(normalizedTake)
            .ToListAsync(cancellationToken);

        var averageAiConfidence = rows.Count == 0
            ? 0m
            : decimal.Round(rows.Average(item => item.AiConfidence), 4, MidpointRounding.AwayFromZero);

        return new AiShadowDecisionSummarySnapshot(
            normalizedUserId,
            botId,
            normalizedSymbol,
            normalizedTimeframe,
            rows.Count,
            rows.Count(item => string.Equals(item.FinalAction, "ShadowOnly", StringComparison.Ordinal)),
            rows.Count(item => string.Equals(item.FinalAction, "NoSubmit", StringComparison.Ordinal)),
            rows.Count(item => string.Equals(item.AiDirection, "Long", StringComparison.Ordinal)),
            rows.Count(item => string.Equals(item.AiDirection, "Short", StringComparison.Ordinal)),
            rows.Count(item => string.Equals(item.AiDirection, "Neutral", StringComparison.Ordinal)),
            rows.Count(item => item.AiIsFallback),
            rows.Count(item => item.RiskVetoPresent),
            rows.Count(item => item.PilotSafetyBlocked),
            rows.Count(item => string.Equals(item.AgreementState, "Agreement", StringComparison.Ordinal)),
            rows.Count(item => string.Equals(item.AgreementState, "Disagreement", StringComparison.Ordinal)),
            rows.Count(item => string.Equals(item.AgreementState, "NotApplicable", StringComparison.Ordinal)),
            averageAiConfidence,
            rows.Count(item => item.AiConfidence >= 0.70m),
            rows.Count(item => item.AiConfidence >= 0.40m && item.AiConfidence < 0.70m),
            rows.Count(item => item.AiConfidence < 0.40m),
            BuildBuckets(rows.Select(item => item.NoSubmitReason)),
            BuildBuckets(rows.Select(item => item.HypotheticalBlockReason)));
    }

    private static IReadOnlyCollection<AiShadowMetricBucketSnapshot> BuildBuckets(IEnumerable<string?> values)
    {
        return values
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())
            .GroupBy(item => item, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new AiShadowMetricBucketSnapshot(group.Key, group.Count()))
            .ToArray();
    }

    private static AiShadowDecisionSnapshot Map(AiShadowDecision entity)
    {
        return new AiShadowDecisionSnapshot(
            entity.Id,
            entity.OwnerUserId,
            entity.BotId,
            entity.ExchangeAccountId,
            entity.TradingStrategyId,
            entity.TradingStrategyVersionId,
            entity.StrategySignalId,
            entity.StrategySignalVetoId,
            entity.FeatureSnapshotId,
            entity.StrategyDecisionTraceId,
            entity.HypotheticalDecisionTraceId,
            entity.CorrelationId,
            entity.StrategyKey,
            entity.Symbol,
            entity.Timeframe,
            entity.EvaluatedAtUtc,
            entity.MarketDataTimestampUtc,
            entity.FeatureVersion,
            entity.StrategyDirection,
            entity.StrategyConfidenceScore,
            entity.StrategyDecisionOutcome,
            entity.StrategyDecisionCode,
            entity.StrategySummary,
            entity.AiDirection,
            entity.AiConfidence,
            entity.AiReasonSummary,
            entity.AiProviderName,
            entity.AiProviderModel,
            entity.AiLatencyMs,
            entity.AiIsFallback,
            entity.AiFallbackReason,
            entity.RiskVetoPresent,
            entity.RiskVetoReason,
            entity.RiskVetoSummary,
            entity.PilotSafetyBlocked,
            entity.PilotSafetyReason,
            entity.PilotSafetySummary,
            entity.TradingMode,
            entity.Plane,
            entity.FinalAction,
            entity.HypotheticalSubmitAllowed,
            entity.HypotheticalBlockReason,
            entity.HypotheticalBlockSummary,
            entity.NoSubmitReason,
            entity.FeatureSummary,
            entity.AgreementState,
            entity.CreatedDate);
    }

    private static string NormalizeDirection(string? value)
    {
        return value?.Trim() switch
        {
            "Long" => "Long",
            "Short" => "Short",
            _ => "Neutral"
        };
    }

    private static string NormalizeFinalAction(string? value)
    {
        return value?.Trim() switch
        {
            "ShadowOnly" => "ShadowOnly",
            "NoSubmit" => "NoSubmit",
            _ => throw new ArgumentException("Shadow final action is invalid.", nameof(value))
        };
    }

    private static string NormalizeAgreementState(string? value)
    {
        return value?.Trim() switch
        {
            "Agreement" => "Agreement",
            "Disagreement" => "Disagreement",
            _ => "NotApplicable"
        };
    }

    private static decimal NormalizeConfidence(decimal value)
    {
        if (value < 0m)
        {
            return 0m;
        }

        if (value > 1m)
        {
            return 1m;
        }

        return decimal.Round(value, 6, MidpointRounding.AwayFromZero);
    }

    private static string NormalizeRequired(string? value, int maxLength, string parameterName)
    {
        var normalizedValue = NormalizeOptional(value, maxLength);

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        return normalizedValue;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalizedValue = value.Trim();
        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : normalizedValue[..maxLength];
    }

    private static DateTime NormalizeTimestamp(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
