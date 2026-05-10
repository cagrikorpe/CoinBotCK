using System.Globalization;
using System.Text.Json;
using CoinBot.Application.Abstractions.Features;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Features;

public sealed class MlFeatureSnapshotService(
    ApplicationDbContext dbContext,
    IOptions<BotExecutionPilotOptions> botExecutionPilotOptions,
    TimeProvider timeProvider,
    ILogger<MlFeatureSnapshotService> logger,
    IOptions<ExecutionRuntimeOptions>? executionRuntimeOptions = null) : IMlFeatureSnapshotService
{
    private const string SchemaVersionValue = "MLFS-1.v2";
    private readonly int privatePlaneFreshnessThresholdMilliseconds =
        checked(Math.Max(1, botExecutionPilotOptions.Value.PrivatePlaneFreshnessThresholdSeconds) * 1000);
    private readonly ExecutionRuntimeOptions executionRuntimeOptionsValue = executionRuntimeOptions?.Value ?? new ExecutionRuntimeOptions();

    public async Task<MlFeatureSnapshotModel> CaptureAsync(
        MlFeatureSnapshotCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var ownerUserId = dbContext.EnsureCurrentUserScope(request.OwnerUserId);
        var symbol = NormalizeRequired(request.Symbol, nameof(request.Symbol)).ToUpperInvariant();
        var timeframe = NormalizeRequired(request.Timeframe, nameof(request.Timeframe));
        var strategyKey = NormalizeRequired(request.StrategyKey, nameof(request.StrategyKey));
        var capturedAtUtc = NormalizeUtc(request.CapturedAtUtc == default
            ? timeProvider.GetUtcNow().UtcDateTime
            : request.CapturedAtUtc);

        var latestFeatureSnapshot = await dbContext.TradingFeatureSnapshots
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                entity.BotId == request.BotId &&
                entity.Symbol == symbol &&
                entity.Timeframe == timeframe &&
                !entity.IsDeleted &&
                entity.EvaluatedAtUtc <= capturedAtUtc)
            .OrderByDescending(entity => entity.FeatureAnchorTimeUtc ?? entity.EvaluatedAtUtc)
            .ThenByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);

        var strategyVersion = request.TradingStrategyVersionId.HasValue
            ? await dbContext.TradingStrategyVersions
                .AsNoTracking()
                .Where(entity =>
                    entity.OwnerUserId == ownerUserId &&
                    entity.Id == request.TradingStrategyVersionId.Value &&
                    !entity.IsDeleted)
                .OrderByDescending(entity => entity.CreatedDate)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        var privatePlaneState = await ResolvePrivatePlaneStateAsync(
            ownerUserId,
            request.ExchangeAccountId,
            cancellationToken);
        var pnlReference = await ResolvePnlReferenceAsync(
            ownerUserId,
            request.BotId,
            request.ExchangeAccountId,
            symbol,
            request.ExecutionEnvironment,
            cancellationToken);

        var scannerScore = request.ScannerScore ?? TryParseDecimalToken(request.CandidateScoringSummary, "CandidateScore");
        var marketScore = request.MarketScore ?? TryParseDecimalToken(request.CandidateScoringSummary, "MarketScore");
        var strategyScore = request.StrategyScore ?? TryParseIntToken(request.CandidateScoringSummary, "StrategyScore");
        var riskPenalty = request.RiskPenalty ?? TryParseDecimalToken(request.CandidateScoringSummary, "RiskPenalty");
        var volatilityScore = TryParseDecimalToken(request.CandidateScoringSummary, "VolatilityScore");
        var liquidityScore = TryParseDecimalToken(request.CandidateScoringSummary, "LiquidityScore");
        var advisoryDirection = ExtractTokenValue(request.CandidateScoringSummary, "AdvisoryDirection");
        var scannerTrendAlignment = ExtractTokenValue(request.CandidateScoringSummary, "ScannerTrendAlignment");
        var trendState = ResolveTrendState(latestFeatureSnapshot, advisoryDirection);
        var volatilityState = ResolveVolatilityState(latestFeatureSnapshot, volatilityScore);
        var liquidityState = ResolveLiquidityState(liquidityScore);
        var featureCompletenessState = ResolveFeatureCompletenessState(latestFeatureSnapshot);
        var featureCompletenessSummary = ResolveFeatureCompletenessSummary(latestFeatureSnapshot);
        var baselineScore = MlBaselineScoringEngine.Calculate(
            new MlBaselineScoreInput(
                strategyScore,
                volatilityScore,
                liquidityScore,
                riskPenalty,
                request.StrategyDirection,
                advisoryDirection,
                scannerTrendAlignment,
                trendState,
                volatilityState,
                liquidityState,
                latestFeatureSnapshot,
                pnlReference.RealizedPnl,
                pnlReference.UnrealizedPnl));

        var entity = new MlFeatureSnapshot
        {
            Symbol = symbol,
            Timeframe = timeframe,
            StrategyKey = strategyKey,
            StrategyTemplateKey = ResolveTemplateKey(strategyVersion?.DefinitionJson),
            StrategySchemaVersion = strategyVersion?.SchemaVersion,
            SignalDirection = NormalizeExplicitState(request.StrategyDirection, "Unavailable"),
            ScannerScore = scannerScore,
            MarketScore = marketScore,
            StrategyScore = strategyScore,
            RiskPenalty = riskPenalty,
            TrendState = trendState,
            VolatilityState = volatilityState,
            LiquidityState = liquidityState,
            MarketFreshnessState = NormalizeExplicitState(ExtractTokenValue(request.CandidateScoringSummary, "FreshnessState"), "Unavailable"),
            MarketFreshnessReason = NormalizeOptional(ExtractTokenValue(request.CandidateScoringSummary, "FreshnessReason")),
            MarketFreshnessSource = NormalizeOptional(ExtractTokenValue(request.CandidateScoringSummary, "FreshnessSource")),
            HistoricalFallbackState = NormalizeExplicitState(ExtractTokenValue(request.CandidateScoringSummary, "HistoricalFallbackState"), "None"),
            PrivatePlaneFreshnessState = privatePlaneState.State,
            PrivatePlaneFreshnessReason = privatePlaneState.Reason,
            GuardDecision = NormalizeExplicitState(request.GuardDecision, "NotEvaluated"),
            GuardReasonCode = NormalizeOptional(request.GuardReasonCode),
            ExecutionDecision = NormalizeExplicitState(request.ExecutionDecision, "NotRequested"),
            OrderSignalType = request.OrderSignalType,
            OrderState = request.OrderState,
            SubmittedToBroker = request.SubmittedToBroker,
            ReduceOnly = request.ReduceOnly,
            PositionUnrealizedPnl = pnlReference.UnrealizedPnl,
            PositionRealizedPnl = pnlReference.RealizedPnl,
            SignalConfidenceScore = baselineScore.SignalConfidenceScore,
            TrendAlignmentScore = baselineScore.TrendAlignmentScore,
            VolatilityScore = baselineScore.VolatilityScore,
            LiquidityScore = baselineScore.LiquidityScore,
            RecentPerformanceScore = baselineScore.RecentPerformanceScore,
            DrawdownPenalty = baselineScore.DrawdownPenalty,
            SampleQualityScore = baselineScore.SampleQualityScore,
            FeatureCompletenessScore = baselineScore.FeatureCompletenessScore,
            CombinedBaselineScore = baselineScore.CombinedBaselineScore,
            BaselineScoreSummary = baselineScore.Summary,
            FeatureCompletenessState = featureCompletenessState,
            FeatureCompletenessSummary = featureCompletenessSummary,
            SchemaVersion = SchemaVersionValue,
            CapturedAtUtc = capturedAtUtc,
            FeatureAnchorTimeUtc = latestFeatureSnapshot?.FeatureAnchorTimeUtc,
            MarketDataTimestampUtc = latestFeatureSnapshot?.MarketDataTimestampUtc,
            ExecutionEnvironment = request.ExecutionEnvironment
        };

        dbContext.MlFeatureSnapshots.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogDebug(
            "ML feature snapshot captured for {Symbol} {Timeframe}. GuardDecision={GuardDecision} ExecutionDecision={ExecutionDecision} CombinedBaselineScore={CombinedBaselineScore}",
            entity.Symbol,
            entity.Timeframe,
            entity.GuardDecision,
            entity.ExecutionDecision,
            entity.CombinedBaselineScore);

        return Map(entity);
    }

    private async Task<PrivatePlaneFreshnessSnapshot> ResolvePrivatePlaneStateAsync(
        string ownerUserId,
        Guid? exchangeAccountId,
        CancellationToken cancellationToken)
    {
        if (!exchangeAccountId.HasValue)
        {
            return new PrivatePlaneFreshnessSnapshot("Unavailable", "ExchangeAccountMissing");
        }

        var syncState = await dbContext.ExchangeAccountSyncStates
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                entity.ExchangeAccountId == exchangeAccountId.Value &&
                entity.Plane == ExchangeDataPlane.Futures &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.UpdatedDate)
            .ThenByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (syncState is null)
        {
            return new PrivatePlaneFreshnessSnapshot("Unavailable", "PrivatePlaneUnavailable");
        }

        var lastPrivateSyncAtUtc = ResolveLastPrivateSyncAtUtc(syncState);
        if (!lastPrivateSyncAtUtc.HasValue)
        {
            return new PrivatePlaneFreshnessSnapshot("Unavailable", "PrivateSyncMissing");
        }

        var ageMilliseconds = ResolveAgeMilliseconds(timeProvider.GetUtcNow().UtcDateTime, lastPrivateSyncAtUtc.Value);
        if (syncState.PrivateStreamConnectionState != ExchangePrivateStreamConnectionState.Connected)
        {
            return new PrivatePlaneFreshnessSnapshot("Stale", "PrivateStreamDisconnected");
        }

        if (syncState.DriftStatus != ExchangeStateDriftStatus.InSync)
        {
            return new PrivatePlaneFreshnessSnapshot("Stale", "PrivatePlaneDrifted");
        }

        if (ageMilliseconds > privatePlaneFreshnessThresholdMilliseconds)
        {
            return new PrivatePlaneFreshnessSnapshot("Stale", "PrivatePlaneAgeExceeded");
        }

        return new PrivatePlaneFreshnessSnapshot("Fresh", "PrivatePlaneFresh");
    }

    private async Task<PnlReferenceSnapshot> ResolvePnlReferenceAsync(
        string ownerUserId,
        Guid botId,
        Guid? exchangeAccountId,
        string symbol,
        ExecutionEnvironment? executionEnvironment,
        CancellationToken cancellationToken)
    {
        if (executionEnvironment.HasValue &&
            ExecutionEnvironmentSemantics.UsesInternalDemoExecution(
                executionEnvironment.Value,
                executionRuntimeOptionsValue.AllowInternalDemoExecution))
        {
            var demoPosition = await dbContext.DemoPositions
                .AsNoTracking()
                .Where(entity =>
                    entity.OwnerUserId == ownerUserId &&
                    entity.BotId == botId &&
                    entity.Symbol == symbol &&
                    !entity.IsDeleted)
                .OrderByDescending(entity => entity.LastValuationAtUtc ?? entity.LastFilledAtUtc ?? entity.UpdatedDate)
                .ThenByDescending(entity => entity.CreatedDate)
                .FirstOrDefaultAsync(cancellationToken);

            return demoPosition is null
                ? PnlReferenceSnapshot.Empty
                : new PnlReferenceSnapshot(demoPosition.UnrealizedPnl, demoPosition.RealizedPnl);
        }

        if (!exchangeAccountId.HasValue)
        {
            return PnlReferenceSnapshot.Empty;
        }

        var exchangePosition = await dbContext.ExchangePositions
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                entity.ExchangeAccountId == exchangeAccountId.Value &&
                entity.Plane == ExchangeDataPlane.Futures &&
                entity.Symbol == symbol &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.SyncedAtUtc)
            .ThenByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);

        return exchangePosition is null
            ? PnlReferenceSnapshot.Empty
            : new PnlReferenceSnapshot(exchangePosition.UnrealizedProfit, null);
    }

    private static DateTime? ResolveLastPrivateSyncAtUtc(ExchangeAccountSyncState syncState)
    {
        DateTime? latest = null;
        Consider(syncState.LastPrivateStreamEventAtUtc);
        Consider(syncState.LastBalanceSyncedAtUtc);
        Consider(syncState.LastPositionSyncedAtUtc);
        Consider(syncState.LastStateReconciledAtUtc);
        return latest;

        void Consider(DateTime? value)
        {
            var normalizedValue = NormalizeUtcNullable(value);
            if (!normalizedValue.HasValue)
            {
                return;
            }

            if (!latest.HasValue || normalizedValue.Value > latest.Value)
            {
                latest = normalizedValue.Value;
            }
        }
    }

    private static int ResolveAgeMilliseconds(DateTime observedAtUtc, DateTime candidateAtUtc)
    {
        var delta = (NormalizeUtc(observedAtUtc) - NormalizeUtc(candidateAtUtc)).TotalMilliseconds;
        if (delta <= 0)
        {
            return 0;
        }

        return delta >= int.MaxValue
            ? int.MaxValue
            : (int)Math.Round(delta, MidpointRounding.AwayFromZero);
    }

    private static string ResolveTrendState(TradingFeatureSnapshot? latestFeatureSnapshot, string? advisoryDirection)
    {
        if (!string.IsNullOrWhiteSpace(latestFeatureSnapshot?.PrimaryRegime))
        {
            return latestFeatureSnapshot.PrimaryRegime;
        }

        return advisoryDirection switch
        {
            "Bullish" => "ScannerBullish",
            "Bearish" => "ScannerBearish",
            "CompressionSetup" => "ScannerCompressionSetup",
            _ => "Unavailable"
        };
    }

    private static string ResolveVolatilityState(TradingFeatureSnapshot? latestFeatureSnapshot, decimal? volatilityScore)
    {
        if (!string.IsNullOrWhiteSpace(latestFeatureSnapshot?.VolatilityState))
        {
            return latestFeatureSnapshot.VolatilityState;
        }

        return ResolveBucketState(volatilityScore);
    }

    private static string ResolveLiquidityState(decimal? liquidityScore)
    {
        return ResolveBucketState(liquidityScore);
    }

    private static string ResolveBucketState(decimal? score)
    {
        if (!score.HasValue)
        {
            return "Unavailable";
        }

        return score.Value switch
        {
            >= 80m => "High",
            >= 50m => "Moderate",
            _ => "Low"
        };
    }

    private static string ResolveFeatureCompletenessState(TradingFeatureSnapshot? latestFeatureSnapshot)
    {
        return latestFeatureSnapshot?.SnapshotState.ToString() ?? "Unavailable";
    }

    private static string ResolveFeatureCompletenessSummary(TradingFeatureSnapshot? latestFeatureSnapshot)
    {
        if (latestFeatureSnapshot is null)
        {
            return "Unavailable:TradingFeatureSnapshotMissing";
        }

        return FormattableString.Invariant(
            $"State={latestFeatureSnapshot.SnapshotState}; Quality={latestFeatureSnapshot.QualityReasonCode}; SampleCount={latestFeatureSnapshot.SampleCount}/{latestFeatureSnapshot.RequiredSampleCount}; Missing={(string.IsNullOrWhiteSpace(latestFeatureSnapshot.MissingFeatureSummary) ? "none" : latestFeatureSnapshot.MissingFeatureSummary)}");
    }

    private static string? ResolveTemplateKey(string? definitionJson)
    {
        if (string.IsNullOrWhiteSpace(definitionJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(definitionJson);
            if (document.RootElement.TryGetProperty("metadata", out var metadata) &&
                metadata.ValueKind == JsonValueKind.Object &&
                metadata.TryGetProperty("templateKey", out var templateKeyElement))
            {
                var templateKey = templateKeyElement.GetString();
                return NormalizeOptional(templateKey);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static decimal? TryParseDecimalToken(string? summary, string key)
    {
        var value = ExtractTokenValue(summary, key);
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedValue)
            ? parsedValue
            : null;
    }

    private static int? TryParseIntToken(string? summary, string key)
    {
        var value = ExtractTokenValue(summary, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue)
            ? parsedValue
            : null;
    }

    private static string? ExtractTokenValue(string? summary, string key)
    {
        if (string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var tokenPrefix = key + "=";
        var startIndex = summary.IndexOf(tokenPrefix, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            return null;
        }

        startIndex += tokenPrefix.Length;
        var endIndex = summary.IndexOf(';', startIndex);
        var value = endIndex >= 0
            ? summary[startIndex..endIndex]
            : summary[startIndex..];

        return NormalizeOptional(value);
    }

    private static MlFeatureSnapshotModel Map(MlFeatureSnapshot entity)
    {
        return new MlFeatureSnapshotModel(
            entity.Id,
            entity.Symbol,
            entity.Timeframe,
            entity.StrategyKey,
            entity.StrategyTemplateKey,
            entity.StrategySchemaVersion,
            entity.SignalDirection,
            entity.ScannerScore,
            entity.MarketScore,
            entity.StrategyScore,
            entity.RiskPenalty,
            entity.TrendState,
            entity.VolatilityState,
            entity.LiquidityState,
            entity.MarketFreshnessState,
            entity.MarketFreshnessReason,
            entity.MarketFreshnessSource,
            entity.HistoricalFallbackState,
            entity.PrivatePlaneFreshnessState,
            entity.PrivatePlaneFreshnessReason,
            entity.GuardDecision,
            entity.GuardReasonCode,
            entity.ExecutionDecision,
            entity.OrderSignalType,
            entity.OrderState,
            entity.SubmittedToBroker,
            entity.ReduceOnly,
            entity.PositionUnrealizedPnl,
            entity.PositionRealizedPnl,
            entity.SignalConfidenceScore,
            entity.TrendAlignmentScore,
            entity.VolatilityScore,
            entity.LiquidityScore,
            entity.RecentPerformanceScore,
            entity.DrawdownPenalty,
            entity.SampleQualityScore,
            entity.FeatureCompletenessScore,
            entity.CombinedBaselineScore,
            entity.BaselineScoreSummary,
            entity.FeatureCompletenessState,
            entity.FeatureCompletenessSummary,
            entity.SchemaVersion,
            entity.CapturedAtUtc,
            entity.FeatureAnchorTimeUtc,
            entity.MarketDataTimestampUtc,
            entity.ExecutionEnvironment);
    }

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalizedValue = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        return normalizedValue;
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

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static DateTime? NormalizeUtcNullable(DateTime? value)
    {
        return value.HasValue
            ? NormalizeUtc(value.Value)
            : null;
    }

    private sealed record PrivatePlaneFreshnessSnapshot(
        string State,
        string? Reason);

    private sealed record PnlReferenceSnapshot(
        decimal? UnrealizedPnl,
        decimal? RealizedPnl)
    {
        public static PnlReferenceSnapshot Empty { get; } = new(null, null);
    }
}
