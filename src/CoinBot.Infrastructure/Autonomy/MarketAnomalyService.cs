using System.Security.Cryptography;
using System.Text;
using CoinBot.Application.Abstractions.Autonomy;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Autonomy;

public sealed class MarketAnomalyService(
    ApplicationDbContext dbContext,
    IGlobalPolicyEngine globalPolicyEngine,
    IMarketDataService marketDataService,
    ISharedSymbolRegistry sharedSymbolRegistry,
    IAutonomyReviewQueueService reviewQueueService,
    IAutonomyIncidentHook incidentHook,
    IDataLatencyCircuitBreaker dataLatencyCircuitBreaker,
    IOptions<MarketAnomalyOptions> options,
    IOptions<AutonomyOptions> autonomyOptions,
    IOptions<MarketScannerOptions> scannerOptions,
    IOptions<DataLatencyGuardOptions> dataLatencyGuardOptions,
    IOptions<BinanceMarketDataOptions> marketDataOptions,
    TimeProvider timeProvider,
    ILogger<MarketAnomalyService> logger)
{
    internal const string SystemActor = "system:market-anomaly-engine";
    internal const string WorkerKey = "market-anomaly-engine";
    internal const string WorkerName = "Market Anomaly Engine";
    private const string PolicySource = "Autonomy.MarketAnomaly";
    private readonly MarketAnomalyOptions optionsValue = options.Value;
    private readonly AutonomyOptions autonomyOptionsValue = autonomyOptions.Value;
    private readonly int staleDataWarningSeconds = Math.Max(
        options.Value.StaleDataWarningSeconds,
        dataLatencyGuardOptions.Value.StaleDataThresholdSeconds);
    private readonly int staleDataCriticalSeconds = Math.Max(
        options.Value.StaleDataCriticalSeconds,
        Math.Max(
            scannerOptions.Value.MaxDataAgeSeconds,
            dataLatencyGuardOptions.Value.StopDataThresholdSeconds));
    private readonly string candleInterval = string.IsNullOrWhiteSpace(marketDataOptions.Value.KlineInterval)
        ? "1m"
        : marketDataOptions.Value.KlineInterval.Trim();

    internal async Task<MarketAnomalySweepResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var latencySnapshot = await dataLatencyCircuitBreaker.GetSnapshotAsync(
            correlationId: $"market-anomaly-sweep:{nowUtc:yyyyMMddHHmmss}",
            cancellationToken: cancellationToken);
        var candidateSymbols = await ResolveCandidateSymbolsAsync(nowUtc, cancellationToken);
        var evaluations = new List<MarketAnomalyEvaluationSnapshot>(candidateSymbols.Count);

        foreach (var symbol in candidateSymbols)
        {
            evaluations.Add(await EvaluateSymbolAsync(symbol, latencySnapshot, nowUtc, cancellationToken));
        }

        var result = BuildResult(nowUtc, candidateSymbols.Count, latencySnapshot, evaluations);
        await UpsertWorkerHeartbeatAsync(result, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Market anomaly sweep completed. Candidates={Candidates}; Evaluated={Evaluated}; PolicyUpdated={PolicyUpdated}; ReviewQueued={ReviewQueued}; Protected={Protected}; InsufficientData={InsufficientData}.",
            result.CandidateSymbolCount,
            result.Evaluations.Count,
            result.PolicyUpdatedCount,
            result.ReviewQueuedCount,
            result.AlreadyProtectedCount,
            result.InsufficientDataCount);

        return result;
    }

    private async Task<MarketAnomalyEvaluationSnapshot> EvaluateSymbolAsync(
        string symbol,
        DegradedModeSnapshot latencySnapshot,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        var correlationId = $"market-anomaly:{normalizedSymbol}:{nowUtc:yyyyMMddHHmmss}";
        var scopedLatencySnapshot = await dataLatencyCircuitBreaker.GetSnapshotAsync(
            correlationId: correlationId,
            symbol: normalizedSymbol,
            timeframe: candleInterval,
            cancellationToken: cancellationToken);
        var latestPrice = await marketDataService.GetLatestPriceAsync(normalizedSymbol, cancellationToken);
        var recentCandles = await dbContext.HistoricalMarketCandles
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.Symbol == normalizedSymbol &&
                entity.Interval == candleInterval)
            .OrderByDescending(entity => entity.CloseTimeUtc)
            .Take(optionsValue.LookbackCandles + 1)
            .ToListAsync(cancellationToken);

        recentCandles.Reverse();
        var policySnapshot = await globalPolicyEngine.GetSnapshotAsync(cancellationToken);

        var signalLabels = new List<string>(4);
        var metrics = new MarketAnomalyMetrics();
        var staleDataCritical = ApplyStaleDataSignals(scopedLatencySnapshot, latestPrice, recentCandles, nowUtc, signalLabels, metrics);
        var usedLatestPrice = latestPrice is not null;
        var usedHistoricalCandles = recentCandles.Count > 0;
        const bool usedDegradedMode = true;
        var hasEnoughHistory = recentCandles.Count >= optionsValue.MinimumHistoricalCandles + 1;

        if (!staleDataCritical && (!usedLatestPrice || !hasEnoughHistory))
        {
            var summary = !usedLatestPrice
                ? $"{normalizedSymbol} skipped because latest market price is missing."
                : $"{normalizedSymbol} skipped because historical candle depth is below {optionsValue.MinimumHistoricalCandles + 1}.";

            return new MarketAnomalyEvaluationSnapshot(
                normalizedSymbol,
                ProposedState: null,
                ExistingState: policySnapshot.Policy.SymbolRestrictions
                    .FirstOrDefault(item => string.Equals(item.Symbol, normalizedSymbol, StringComparison.Ordinal))
                    ?.State,
                ConfidenceScore: 0m,
                Decision: MarketAnomalyDecision.InsufficientData,
                PolicyUpdated: false,
                ReviewQueued: false,
                IncidentWritten: false,
                UsedLatestPrice: usedLatestPrice,
                UsedHistoricalCandles: usedHistoricalCandles,
                UsedDegradedMode: usedDegradedMode,
                Summary: summary,
                TriggerLabels: signalLabels.ToArray());
        }

        if (usedLatestPrice && hasEnoughHistory)
        {
            ApplyMarketStructureSignals(latestPrice!, recentCandles, signalLabels, metrics);
        }

        var proposedState = ResolveProposedState(metrics);
        var existingState = policySnapshot.Policy.SymbolRestrictions
            .FirstOrDefault(item => string.Equals(item.Symbol, normalizedSymbol, StringComparison.Ordinal))
            ?.State;

        if (!proposedState.HasValue)
        {
            return new MarketAnomalyEvaluationSnapshot(
                normalizedSymbol,
                ProposedState: null,
                ExistingState: existingState,
                ConfidenceScore: 0m,
                Decision: MarketAnomalyDecision.NoAction,
                PolicyUpdated: false,
                ReviewQueued: false,
                IncidentWritten: false,
                UsedLatestPrice: usedLatestPrice,
                UsedHistoricalCandles: usedHistoricalCandles,
                UsedDegradedMode: usedDegradedMode,
                Summary: $"{normalizedSymbol} remained within normal market thresholds.",
                TriggerLabels: signalLabels.ToArray());
        }

        var confidenceScore = ResolveConfidenceScore(metrics);
        if (existingState.HasValue && GetRestrictionRank(existingState.Value) >= GetRestrictionRank(proposedState.Value))
        {
            return new MarketAnomalyEvaluationSnapshot(
                normalizedSymbol,
                proposedState,
                existingState,
                confidenceScore,
                MarketAnomalyDecision.AlreadyProtected,
                PolicyUpdated: false,
                ReviewQueued: false,
                IncidentWritten: false,
                UsedLatestPrice: usedLatestPrice,
                UsedHistoricalCandles: usedHistoricalCandles,
                UsedDegradedMode: usedDegradedMode,
                Summary: $"{normalizedSymbol} anomaly matched an already-active {existingState.Value} restriction.",
                TriggerLabels: signalLabels.ToArray());
        }

        var canAutoApply = policySnapshot.Policy.AutonomyPolicy.Mode == AutonomyPolicyMode.LowRiskAutoAct &&
                           confidenceScore >= optionsValue.AutoApplyConfidenceThreshold &&
                           (1m - confidenceScore) <= autonomyOptionsValue.MaxFalsePositiveProbability &&
                           proposedState.Value != SymbolRestrictionState.ReviewOnly;
        var shouldQueueReview = !canAutoApply || proposedState.Value is SymbolRestrictionState.CloseOnly or SymbolRestrictionState.Blocked;
        var policyUpdated = false;

        if (canAutoApply)
        {
            await ApplyRestrictionAsync(
                normalizedSymbol,
                proposedState.Value,
                BuildPolicyReason(normalizedSymbol, proposedState.Value, signalLabels),
                correlationId,
                nowUtc,
                cancellationToken);
            policyUpdated = true;
        }

        if (shouldQueueReview)
        {
            await reviewQueueService.EnqueueAsync(
                new AutonomyReviewQueueEnqueueRequest(
                    ApprovalId: BuildApprovalId(normalizedSymbol, proposedState.Value),
                    ScopeKey: BuildScopeKey(normalizedSymbol),
                    SuggestedAction: AutonomySuggestedActions.PolicyChange,
                    ConfidenceScore: confidenceScore,
                    AffectedUsers: Array.Empty<string>(),
                    AffectedSymbols: [normalizedSymbol],
                    ExpiresAtUtc: nowUtc.AddMinutes(autonomyOptionsValue.ReviewQueueTtlMinutes),
                    Reason: BuildReviewReason(normalizedSymbol, proposedState.Value, signalLabels),
                    CorrelationId: correlationId),
                cancellationToken);
        }

        await incidentHook.WriteIncidentAsync(
            new AutonomyIncidentHookRequest(
                SystemActor,
                BuildScopeKey(normalizedSymbol),
                BuildIncidentSummary(normalizedSymbol, proposedState.Value, policyUpdated, shouldQueueReview),
                BuildIncidentDetail(normalizedSymbol, proposedState.Value, signalLabels, metrics),
                correlationId,
                ResolveIncidentSeverity(proposedState.Value)),
            cancellationToken);

        return new MarketAnomalyEvaluationSnapshot(
            normalizedSymbol,
            proposedState,
            existingState,
            confidenceScore,
            policyUpdated
                ? shouldQueueReview
                    ? MarketAnomalyDecision.PolicyUpdatedAndReviewQueued
                    : MarketAnomalyDecision.PolicyUpdated
                : MarketAnomalyDecision.ReviewQueued,
            policyUpdated,
            shouldQueueReview,
            IncidentWritten: true,
            UsedLatestPrice: usedLatestPrice,
            UsedHistoricalCandles: usedHistoricalCandles,
            UsedDegradedMode: usedDegradedMode,
            Summary: BuildDecisionSummary(normalizedSymbol, proposedState.Value, policyUpdated, shouldQueueReview),
            TriggerLabels: signalLabels.ToArray());
    }

    private async Task<IReadOnlyCollection<string>> ResolveCandidateSymbolsAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var trackedSymbols = await sharedSymbolRegistry.ListSymbolsAsync(cancellationToken);
        var recentWindowStartUtc = nowUtc.AddMinutes(-optionsValue.RecentSymbolWindowMinutes);
        var recentCandleSymbols = await dbContext.HistoricalMarketCandles
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.Interval == candleInterval &&
                entity.CloseTimeUtc >= recentWindowStartUtc)
            .Select(entity => entity.Symbol)
            .Distinct()
            .ToListAsync(cancellationToken);

        return trackedSymbols
            .Select(item => NormalizeSymbol(item.Symbol))
            .Concat(recentCandleSymbols.Select(NormalizeSymbol))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .Take(optionsValue.MaxSymbolsPerSweep)
            .ToArray();
    }

    private async Task ApplyRestrictionAsync(
        string symbol,
        SymbolRestrictionState proposedState,
        string reason,
        string correlationId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var snapshot = await globalPolicyEngine.GetSnapshotAsync(cancellationToken);
        var updatedRestrictions = snapshot.Policy.SymbolRestrictions
            .Where(item => !string.Equals(item.Symbol, symbol, StringComparison.Ordinal))
            .Concat([new SymbolRestriction(symbol, proposedState, reason, nowUtc, SystemActor)])
            .OrderBy(item => item.Symbol, StringComparer.Ordinal)
            .ToArray();
        var updatedPolicy = new RiskPolicySnapshot(
            snapshot.Policy.PolicyKey,
            snapshot.Policy.ExecutionGuardPolicy,
            snapshot.Policy.AutonomyPolicy,
            updatedRestrictions);

        await globalPolicyEngine.UpdateAsync(
            new GlobalPolicyUpdateRequest(
                updatedPolicy,
                SystemActor,
                reason,
                correlationId,
                PolicySource,
                IpAddress: null,
                UserAgent: null),
            cancellationToken);
    }

    private async Task UpsertWorkerHeartbeatAsync(MarketAnomalySweepResult result, CancellationToken cancellationToken)
    {
        var entity = await dbContext.WorkerHeartbeats
            .SingleOrDefaultAsync(item => item.WorkerKey == WorkerKey, cancellationToken);

        if (entity is null)
        {
            entity = new WorkerHeartbeat
            {
                Id = Guid.NewGuid(),
                WorkerKey = WorkerKey
            };
            dbContext.WorkerHeartbeats.Add(entity);
        }

        var (healthState, circuitState, errorCode) = ResolveWorkerState(result);
        entity.WorkerName = WorkerName;
        entity.HealthState = healthState;
        entity.FreshnessTier = MonitoringFreshnessTier.Hot;
        entity.CircuitBreakerState = circuitState;
        entity.LastHeartbeatAtUtc = result.EvaluatedAtUtc;
        entity.LastUpdatedAtUtc = result.EvaluatedAtUtc;
        entity.ConsecutiveFailureCount = 0;
        entity.LastErrorCode = errorCode;
        entity.LastErrorMessage = Truncate(result.Summary, 1024);
        entity.SnapshotAgeSeconds = 0;
        entity.Detail = BuildWorkerDetail(result);
    }

    private bool ApplyStaleDataSignals(
        DegradedModeSnapshot latencySnapshot,
        MarketPriceSnapshot? latestPrice,
        IReadOnlyList<HistoricalMarketCandle> recentCandles,
        DateTime nowUtc,
        ICollection<string> signalLabels,
        MarketAnomalyMetrics metrics)
    {
        var latestObservedAtUtc = latestPrice is null
            ? (DateTime?)null
            : latestPrice.ReceivedAtUtc > latestPrice.ObservedAtUtc
                ? latestPrice.ReceivedAtUtc
                : latestPrice.ObservedAtUtc;
        var latestCandleCloseTimeUtc = recentCandles.Count == 0
            ? (DateTime?)null
            : NormalizeUtc(recentCandles[^1].CloseTimeUtc);

        metrics.PriceAgeSeconds = latestObservedAtUtc.HasValue
            ? Math.Max(0, (int)Math.Round((nowUtc - latestObservedAtUtc.Value).TotalSeconds, MidpointRounding.AwayFromZero))
            : null;
        metrics.CandleAgeSeconds = latestCandleCloseTimeUtc.HasValue
            ? Math.Max(0, (int)Math.Round((nowUtc - latestCandleCloseTimeUtc.Value).TotalSeconds, MidpointRounding.AwayFromZero))
            : null;

        var isCritical = !latencySnapshot.IsNormal ||
                         metrics.PriceAgeSeconds >= staleDataCriticalSeconds ||
                         metrics.CandleAgeSeconds >= staleDataCriticalSeconds;

        if (isCritical)
        {
            signalLabels.Add("StaleData");
            metrics.SeverityScore += 4;
            return true;
        }

        if (metrics.PriceAgeSeconds >= staleDataWarningSeconds ||
            metrics.CandleAgeSeconds >= staleDataWarningSeconds)
        {
            signalLabels.Add("DataAge");
            metrics.SeverityScore += 1;
        }

        return false;
    }

    private void ApplyMarketStructureSignals(
        MarketPriceSnapshot latestPrice,
        IReadOnlyList<HistoricalMarketCandle> recentCandles,
        ICollection<string> signalLabels,
        MarketAnomalyMetrics metrics)
    {
        var latestCandle = recentCandles[^1];
        var historicalCandles = recentCandles.Take(recentCandles.Count - 1).ToArray();
        var referenceClose = historicalCandles[^1].ClosePrice > 0m ? historicalCandles[^1].ClosePrice : latestCandle.ClosePrice;
        metrics.PriceShockPercent = referenceClose <= 0m ? 0m : Math.Abs(latestPrice.Price - referenceClose) / referenceClose;

        if (metrics.PriceShockPercent >= optionsValue.PriceShockSeverePercent)
        {
            signalLabels.Add(latestPrice.Price < referenceClose ? "FlashCrash" : "PriceSpike");
            metrics.SeverityScore += 3;
        }
        else if (metrics.PriceShockPercent >= optionsValue.PriceShockModeratePercent)
        {
            signalLabels.Add(latestPrice.Price < referenceClose ? "FlashCrashWatch" : "PriceSpikeWatch");
            metrics.SeverityScore += 1;
        }

        var historicalRangeMedian = Median(historicalCandles.Select(ComputeRangePercent));
        var latestRangePercent = ComputeRangePercent(latestCandle);
        metrics.RangeRatio = historicalRangeMedian <= 0m ? 0m : latestRangePercent / historicalRangeMedian;

        if (latestRangePercent >= optionsValue.RangeFloorPercent)
        {
            if (metrics.RangeRatio >= optionsValue.RangeRatioSevere)
            {
                signalLabels.Add("SpreadAnomaly");
                metrics.SeverityScore += 2;
            }
            else if (metrics.RangeRatio >= optionsValue.RangeRatioModerate)
            {
                signalLabels.Add("SpreadWatch");
                metrics.SeverityScore += 1;
            }
        }

        var historicalVolumeMedian = Median(historicalCandles.Select(item => item.Volume));
        metrics.VolumeRatio = historicalVolumeMedian <= 0m ? null : latestCandle.Volume / historicalVolumeMedian;

        if (metrics.VolumeRatio <= optionsValue.VolumeRatioSevere &&
            (metrics.PriceShockPercent >= optionsValue.PriceShockModeratePercent || metrics.RangeRatio >= optionsValue.RangeRatioModerate))
        {
            signalLabels.Add("LiquidityBreak");
            metrics.SeverityScore += 2;
        }
        else if (metrics.VolumeRatio <= optionsValue.VolumeRatioModerate && metrics.RangeRatio >= 1.5m)
        {
            signalLabels.Add("LiquidityWatch");
            metrics.SeverityScore += 1;
        }
    }

    private SymbolRestrictionState? ResolveProposedState(MarketAnomalyMetrics metrics)
    {
        var compoundedExtreme =
            metrics.SeverityScore >= 6 ||
            (metrics.PriceShockPercent >= optionsValue.PriceShockSeverePercent &&
             metrics.RangeRatio >= optionsValue.RangeRatioSevere &&
             metrics.VolumeRatio <= optionsValue.VolumeRatioSevere) ||
            metrics.SeverityScore >= 4 && metrics.PriceAgeSeconds >= optionsValue.StaleDataCriticalSeconds;

        if (compoundedExtreme)
        {
            return SymbolRestrictionState.Blocked;
        }

        var corroboratedSevereShock =
            metrics.PriceShockPercent >= optionsValue.PriceShockSeverePercent &&
            (metrics.RangeRatio >= optionsValue.RangeRatioModerate ||
             metrics.VolumeRatio <= optionsValue.VolumeRatioModerate ||
             metrics.PriceAgeSeconds >= optionsValue.StaleDataWarningSeconds);

        if (corroboratedSevereShock || metrics.SeverityScore >= 5)
        {
            return SymbolRestrictionState.CloseOnly;
        }

        if (metrics.SeverityScore >= 3)
        {
            return SymbolRestrictionState.ReduceOnly;
        }

        return metrics.SeverityScore >= 2
            ? SymbolRestrictionState.ReviewOnly
            : null;
    }

    private static decimal ResolveConfidenceScore(MarketAnomalyMetrics metrics)
    {
        var signalBonus = metrics.SeverityScore >= 4 ? 0.08m : 0.03m;
        return Math.Clamp(0.52m + (metrics.SeverityScore * 0.08m) + signalBonus, 0m, 0.99m);
    }

    private static (MonitoringHealthState HealthState, CircuitBreakerStateCode CircuitState, string ErrorCode) ResolveWorkerState(MarketAnomalySweepResult result)
    {
        if (result.CandidateSymbolCount == 0)
        {
            return (MonitoringHealthState.Unknown, CircuitBreakerStateCode.Degraded, "NoSymbols");
        }

        if (result.Evaluations.Any(item => item.ProposedState is SymbolRestrictionState.Blocked or SymbolRestrictionState.CloseOnly))
        {
            return (MonitoringHealthState.Critical, CircuitBreakerStateCode.Cooldown, result.ReviewQueuedCount > 0 ? "HighRiskReviewQueued" : "HighRiskRestrictionApplied");
        }

        if (result.ReviewQueuedCount > 0 || result.PolicyUpdatedCount > 0 || result.InsufficientDataCount > 0)
        {
            return (MonitoringHealthState.Warning, CircuitBreakerStateCode.Degraded, result.InsufficientDataCount > 0 ? "InsufficientData" : "AnomalyMitigationActive");
        }

        return (MonitoringHealthState.Healthy, CircuitBreakerStateCode.Closed, "Healthy");
    }

    private static MarketAnomalySweepResult BuildResult(
        DateTime nowUtc,
        int candidateSymbolCount,
        DegradedModeSnapshot latencySnapshot,
        IReadOnlyCollection<MarketAnomalyEvaluationSnapshot> evaluations)
    {
        var policyUpdatedCount = evaluations.Count(item => item.PolicyUpdated);
        var reviewQueuedCount = evaluations.Count(item => item.ReviewQueued);
        var alreadyProtectedCount = evaluations.Count(item => item.Decision == MarketAnomalyDecision.AlreadyProtected);
        var insufficientDataCount = evaluations.Count(item => item.Decision == MarketAnomalyDecision.InsufficientData);
        var highestState = evaluations
            .Where(item => item.ProposedState.HasValue)
            .OrderByDescending(item => GetRestrictionRank(item.ProposedState!.Value))
            .Select(item => item.ProposedState)
            .FirstOrDefault();
        var summary = candidateSymbolCount == 0
            ? "Market anomaly sweep found no candidate symbols."
            : $"Sweep evaluated {evaluations.Count} symbols. PolicyUpdated={policyUpdatedCount}; ReviewQueued={reviewQueuedCount}; Protected={alreadyProtectedCount}; InsufficientData={insufficientDataCount}; Latency={latencySnapshot.StateCode}/{latencySnapshot.ReasonCode}; HighestState={highestState?.ToString() ?? "None"}.";

        return new MarketAnomalySweepResult(
            nowUtc,
            candidateSymbolCount,
            evaluations,
            policyUpdatedCount,
            reviewQueuedCount,
            alreadyProtectedCount,
            insufficientDataCount,
            summary);
    }

    private static IncidentSeverity ResolveIncidentSeverity(SymbolRestrictionState state)
    {
        return state switch
        {
            SymbolRestrictionState.Blocked or SymbolRestrictionState.CloseOnly => IncidentSeverity.Critical,
            SymbolRestrictionState.ReduceOnly => IncidentSeverity.Warning,
            _ => IncidentSeverity.Info
        };
    }

    private static string BuildPolicyReason(string symbol, SymbolRestrictionState state, IReadOnlyCollection<string> signalLabels)
    {
        return Truncate($"Market anomaly engine set {symbol} to {state}. Triggers={string.Join(",", signalLabels.OrderBy(item => item, StringComparer.Ordinal))}.", 512)
            ?? $"Market anomaly engine set {symbol} to {state}.";
    }

    private static string BuildReviewReason(string symbol, SymbolRestrictionState state, IReadOnlyCollection<string> signalLabels)
    {
        return Truncate($"Market anomaly review for {symbol} recommends {state}. Triggers={string.Join("+", signalLabels.OrderBy(item => item, StringComparer.Ordinal))}.", 512)
            ?? $"Market anomaly review for {symbol} recommends {state}.";
    }

    private static string BuildIncidentSummary(string symbol, SymbolRestrictionState state, bool policyUpdated, bool reviewQueued)
    {
        if (policyUpdated && reviewQueued)
        {
            return $"{symbol} moved to {state} and queued for manual review.";
        }

        return policyUpdated
            ? $"{symbol} moved to {state} after market anomaly detection."
            : $"{symbol} anomaly queued for manual review with {state} recommendation.";
    }

    private static string BuildIncidentDetail(string symbol, SymbolRestrictionState state, IReadOnlyCollection<string> signalLabels, MarketAnomalyMetrics metrics)
    {
        var detail =
            $"Symbol={symbol}; ProposedState={state}; Triggers={string.Join(",", signalLabels.OrderBy(item => item, StringComparer.Ordinal))}; PriceShockPct={metrics.PriceShockPercent:0.####}; RangeRatio={metrics.RangeRatio:0.####}; VolumeRatio={(metrics.VolumeRatio?.ToString("0.####") ?? "missing")}; PriceAgeSeconds={(metrics.PriceAgeSeconds?.ToString() ?? "missing")}; CandleAgeSeconds={(metrics.CandleAgeSeconds?.ToString() ?? "missing")}; SeverityScore={metrics.SeverityScore}";

        return Truncate(detail, 8192) ?? $"Symbol={symbol}; ProposedState={state}";
    }

    private static string BuildDecisionSummary(string symbol, SymbolRestrictionState state, bool policyUpdated, bool reviewQueued)
    {
        if (policyUpdated && reviewQueued)
        {
            return $"{symbol} restriction {state} applied and review queued.";
        }

        return policyUpdated
            ? $"{symbol} restriction {state} applied."
            : $"{symbol} review queued with {state} recommendation.";
    }

    private static string BuildScopeKey(string symbol)
    {
        return $"SYMBOL:{NormalizeSymbol(symbol)}";
    }

    private static string BuildApprovalId(string symbol, SymbolRestrictionState state)
    {
        var payload = $"{NormalizeSymbol(symbol)}|{state}|{Guid.NewGuid():N}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return $"autonomy-{Convert.ToHexStringLower(hash)[..20]}";
    }

    private static string BuildWorkerDetail(MarketAnomalySweepResult result)
    {
        var highestState = result.Evaluations
            .Where(item => item.ProposedState.HasValue)
            .OrderByDescending(item => GetRestrictionRank(item.ProposedState!.Value))
            .Select(item => item.ProposedState!.Value.ToString())
            .FirstOrDefault() ?? "None";
        var detail =
            $"Candidates={result.CandidateSymbolCount}; Evaluated={result.Evaluations.Count}; PolicyUpdated={result.PolicyUpdatedCount}; ReviewQueued={result.ReviewQueuedCount}; Protected={result.AlreadyProtectedCount}; InsufficientData={result.InsufficientDataCount}; HighestState={highestState}; Summary={result.Summary}";

        return Truncate(detail, 2048) ?? "Market anomaly worker completed.";
    }

    private static int GetRestrictionRank(SymbolRestrictionState state)
    {
        return state switch
        {
            SymbolRestrictionState.ReviewOnly => 1,
            SymbolRestrictionState.ReduceOnly => 2,
            SymbolRestrictionState.CloseOnly => 3,
            SymbolRestrictionState.Blocked => 4,
            _ => 0
        };
    }

    private static decimal ComputeRangePercent(HistoricalMarketCandle candle)
    {
        var reference = candle.ClosePrice > 0m ? candle.ClosePrice : candle.OpenPrice;
        return reference <= 0m ? 0m : Math.Abs(candle.HighPrice - candle.LowPrice) / reference;
    }

    private static decimal Median(IEnumerable<decimal> values)
    {
        var ordered = values.Where(value => value > 0m).OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
        {
            return 0m;
        }

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2m
            : ordered[middle];
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

    private static string NormalizeSymbol(string symbol)
    {
        var normalized = symbol?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("The symbol is required.", nameof(symbol));
        }

        return normalized;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}
