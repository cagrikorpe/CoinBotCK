using System.Globalization;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Domain.Entities;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using HealthSnapshotEntity = CoinBot.Domain.Entities.HealthSnapshot;
using MarketScannerCandidateEntity = CoinBot.Domain.Entities.MarketScannerCandidate;
using MarketScannerHandoffAttemptEntity = CoinBot.Domain.Entities.MarketScannerHandoffAttempt;
using WorkerHeartbeatEntity = CoinBot.Domain.Entities.WorkerHeartbeat;

namespace CoinBot.Infrastructure.Administration;

public sealed class AdminMonitoringReadModelService(
    ApplicationDbContext dbContext,
    IMemoryCache memoryCache,
    TimeProvider timeProvider,
    IOptions<DataLatencyGuardOptions> dataLatencyGuardOptions,
    ISharedMarketDataCacheObservabilityCollector? sharedMarketDataCacheObservabilityCollector = null,
    IUltraDebugLogService? ultraDebugLogService = null) : IAdminMonitoringReadModelService
{
    private static readonly object CacheKey = new();
    private static readonly MemoryCacheEntryOptions SnapshotCacheOptions = new MemoryCacheEntryOptions()
        .SetSize(1)
        .SetAbsoluteExpiration(TimeSpan.FromSeconds(3));
    private readonly int staleThresholdMilliseconds = checked(dataLatencyGuardOptions.Value.StaleDataThresholdSeconds * 1000);

    public Task<MonitoringDashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return memoryCache.GetOrCreateAsync(
                CacheKey,
                async entry =>
                {
                    entry.SetOptions(SnapshotCacheOptions);
                    var utcNow = timeProvider.GetUtcNow().UtcDateTime;

                    var healthSnapshots = await dbContext.HealthSnapshots
                        .AsNoTracking()
                        .OrderBy(entity => entity.DisplayName)
                        .ToListAsync(cancellationToken);
                    var workerHeartbeats = await dbContext.WorkerHeartbeats
                        .AsNoTracking()
                        .OrderBy(entity => entity.WorkerName)
                        .ToListAsync(cancellationToken);
                    var scannerSnapshot = await LoadMarketScannerSnapshotAsync(cancellationToken);
                    var ultraDebugLogHealth = await LoadUltraDebugLogHealthAsync(cancellationToken);

                    return new MonitoringDashboardSnapshot(
                        healthSnapshots.Select(MapHealthSnapshot).ToArray(),
                        workerHeartbeats.Select(MapWorkerHeartbeat).ToArray(),
                        utcNow)
                    {
                        MarketScanner = scannerSnapshot,
                        UltraDebugLogHealth = ultraDebugLogHealth,
                        MarketDataCache = sharedMarketDataCacheObservabilityCollector?.GetSnapshot(utcNow)
                            ?? SharedMarketDataCacheHealthSnapshot.Empty(utcNow)
                    };
                },
                SnapshotCacheOptions)!;
    }

    private async Task<UltraDebugLogHealthSnapshot> LoadUltraDebugLogHealthAsync(CancellationToken cancellationToken)
    {
        if (ultraDebugLogService is null)
        {
            return UltraDebugLogHealthSnapshot.Empty();
        }

        try
        {
            return await ultraDebugLogService.GetHealthSnapshotAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return UltraDebugLogHealthSnapshot.Empty() with
            {
                LastEscalationReason = "Unavailable"
            };
        }
    }

    private async Task<MarketScannerDashboardSnapshot> LoadMarketScannerSnapshotAsync(CancellationToken cancellationToken)
    {
        var latestCycle = await dbContext.MarketScannerCycles
            .AsNoTracking()
            .Where(entity => !entity.IsDeleted)
            .OrderByDescending(entity => entity.CompletedAtUtc)
            .ThenByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestCycle is null)
        {
            return MarketScannerDashboardSnapshot.Empty();
        }

        var cycleCandidateEntities = await dbContext.MarketScannerCandidates
            .AsNoTracking()
            .Where(entity => entity.ScanCycleId == latestCycle.Id && !entity.IsDeleted)
            .OrderBy(entity => entity.Rank ?? int.MaxValue)
            .ThenBy(entity => entity.Symbol)
            .ToListAsync(cancellationToken);

        var validCandidateEntities = cycleCandidateEntities
            .Where(entity => !MarketScannerCandidateIntegrityGuard.HasLegacyDirtyMarketScore(entity))
            .ToArray();

        var topCandidateEntities = validCandidateEntities
            .Where(entity => entity.IsTopCandidate)
            .OrderBy(entity => entity.Rank ?? int.MaxValue)
            .ThenByDescending(entity => entity.Score)
            .ThenBy(entity => entity.Symbol, StringComparer.Ordinal)
            .ToArray();

        var rejectedSampleEntities = validCandidateEntities
            .Where(entity => !entity.IsEligible)
            .OrderBy(entity => entity.RejectionReason, StringComparer.Ordinal)
            .ThenBy(entity => entity.Symbol, StringComparer.Ordinal)
            .Take(5)
            .ToArray();

        var latestHandoffEntity = await dbContext.MarketScannerHandoffAttempts
            .AsNoTracking()
            .OrderByDescending(entity => entity.CompletedAtUtc)
            .ThenByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);
        var lastSuccessfulHandoffEntity = await dbContext.MarketScannerHandoffAttempts
            .AsNoTracking()
            .Where(entity => entity.ExecutionRequestStatus == "Prepared")
            .OrderByDescending(entity => entity.CompletedAtUtc)
            .ThenByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);
        var lastBlockedHandoffEntity = await dbContext.MarketScannerHandoffAttempts
            .AsNoTracking()
            .Where(entity => entity.ExecutionRequestStatus != "Prepared")
            .OrderByDescending(entity => entity.CompletedAtUtc)
            .ThenByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);
        var degradedModeStates = await LoadDegradedModeStatesAsync(
            [latestHandoffEntity, lastSuccessfulHandoffEntity, lastBlockedHandoffEntity],
            cancellationToken);

        var excludedCandidateCount = cycleCandidateEntities.Count - validCandidateEntities.Length;
        var excludedEligibleCount = cycleCandidateEntities.Count(entity => entity.IsEligible) - validCandidateEntities.Count(entity => entity.IsEligible);
        var bestCandidate = topCandidateEntities
            .OrderBy(entity => entity.Rank ?? int.MaxValue)
            .ThenBy(entity => entity.Symbol, StringComparer.Ordinal)
            .FirstOrDefault();
        var aiRankingFallbackCount = validCandidateEntities.Count(entity =>
            string.Equals(ResolveCandidateAiRankingMode(entity.ScoringSummary), "ClassicalFallback", StringComparison.Ordinal));
        var aiRankingSuppressionCount = validCandidateEntities.Count(entity =>
            string.Equals(ResolveCandidateAiRankingAdaptiveFilterState(entity.ScoringSummary), "Suppressed", StringComparison.Ordinal));
        var aiRankingTopCandidateChangedCount = ResolveAiRankingTopCandidateChangedCount(validCandidateEntities);
        var aiRankingStatus = ResolveAiRankingStatus(validCandidateEntities, topCandidateEntities);
        var cycleSummary = BuildMarketScannerCycleSummary(
            Math.Max(0, latestCycle.ScannedSymbolCount - excludedCandidateCount),
            bestCandidate,
            validCandidateEntities,
            latestCycle.Summary);
        var rejectionSummary = BuildMarketScannerRejectionSummary(validCandidateEntities);

        return new MarketScannerDashboardSnapshot(
            latestCycle.Id,
            NormalizeUtc(latestCycle.CompletedAtUtc),
            Math.Max(0, latestCycle.ScannedSymbolCount - excludedCandidateCount),
            Math.Max(0, latestCycle.EligibleCandidateCount - excludedEligibleCount),
            latestCycle.UniverseSource,
            bestCandidate?.Symbol,
            bestCandidate?.Score,
            topCandidateEntities.Select(MapMarketScannerCandidate).ToArray(),
            rejectedSampleEntities.Select(MapMarketScannerCandidate).ToArray(),
            MapMarketScannerHandoffAttempt(latestHandoffEntity, ResolveDegradedModeState(latestHandoffEntity, degradedModeStates)),
            MapMarketScannerHandoffAttempt(lastSuccessfulHandoffEntity, ResolveDegradedModeState(lastSuccessfulHandoffEntity, degradedModeStates)),
            MapMarketScannerHandoffAttempt(lastBlockedHandoffEntity, ResolveDegradedModeState(lastBlockedHandoffEntity, degradedModeStates)),
            cycleSummary,
            rejectionSummary,
            aiRankingFallbackCount,
            aiRankingSuppressionCount,
            aiRankingTopCandidateChangedCount,
            aiRankingStatus.Title,
            aiRankingStatus.Summary,
            aiRankingStatus.Reason);
    }

    private async Task<Dictionary<Guid, DegradedModeState>> LoadDegradedModeStatesAsync(
        IEnumerable<MarketScannerHandoffAttemptEntity?> handoffAttempts,
        CancellationToken cancellationToken)
    {
        var stateIds = handoffAttempts
            .Where(entity => entity is not null &&
                !string.IsNullOrWhiteSpace(entity.SelectedSymbol) &&
                !string.IsNullOrWhiteSpace(entity.SelectedTimeframe))
            .Select(entity => DegradedModeDefaults.ResolveStateId(entity!.SelectedSymbol, entity.SelectedTimeframe))
            .Distinct()
            .ToArray();

        if (stateIds.Length == 0)
        {
            return [];
        }

        return await dbContext.DegradedModeStates
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => stateIds.Contains(entity.Id))
            .ToDictionaryAsync(entity => entity.Id, cancellationToken);
    }

    private static DegradedModeState? ResolveDegradedModeState(
        MarketScannerHandoffAttemptEntity? entity,
        IReadOnlyDictionary<Guid, DegradedModeState> degradedModeStates)
    {
        if (entity is null ||
            string.IsNullOrWhiteSpace(entity.SelectedSymbol) ||
            string.IsNullOrWhiteSpace(entity.SelectedTimeframe))
        {
            return null;
        }

        return degradedModeStates.TryGetValue(
            DegradedModeDefaults.ResolveStateId(entity.SelectedSymbol, entity.SelectedTimeframe),
            out var degradedModeState)
            ? degradedModeState
            : null;
    }

    private MarketScannerHandoffSnapshot MapMarketScannerHandoffAttempt(
        MarketScannerHandoffAttemptEntity? entity,
        DegradedModeState? degradedModeState)
    {
        if (entity is null)
        {
            return MarketScannerHandoffSnapshot.Empty();
        }

        var isBlocked = !string.Equals(entity.ExecutionRequestStatus, "Prepared", StringComparison.Ordinal);
        var decisionAtUtc = NormalizeUtc(entity.CompletedAtUtc);
        var latencyReasonCode = ExecutionDecisionDiagnostics.ExtractToken("LatencyReason", entity.BlockerDetail, entity.GuardSummary)
            ?? degradedModeState?.ReasonCode.ToString();
        var continuityGapCount = ExecutionDecisionDiagnostics.ExtractIntToken("ContinuityGapCount", entity.BlockerDetail, entity.GuardSummary)
            ?? degradedModeState?.LatestContinuityGapCount;
        var decisionReasonCode = ExecutionDecisionDiagnostics.ResolveDecisionReasonCode(
            isBlocked,
            entity.BlockerCode,
            latencyReasonCode);
        var decisionReasonType = ExecutionDecisionDiagnostics.ResolveDecisionReasonType(
            decisionReasonCode,
            latencyReasonCode,
            entity.RiskOutcome,
            entity.StrategyDecisionOutcome);
        var marketDataLastCandleAtUtc = ExecutionDecisionDiagnostics.ExtractUtcToken("LastCandleAtUtc", entity.BlockerDetail, entity.GuardSummary)
            ?? NormalizeUtcNullable(degradedModeState?.LatestDataTimestampAtUtc);
        var continuityGapStartedAtUtc = NormalizeUtcNullable(degradedModeState?.LatestContinuityGapStartedAtUtc);
        var continuityGapLastSeenAtUtc = NormalizeUtcNullable(degradedModeState?.LatestContinuityGapLastSeenAtUtc);
        var continuityRecoveredAtUtc = NormalizeUtcNullable(degradedModeState?.LatestContinuityRecoveredAtUtc);
        var continuityState = ExecutionDecisionDiagnostics.ResolveContinuityState(
            latencyReasonCode,
            continuityGapCount,
            continuityRecoveredAtUtc);
        var decisionSummary = ExecutionDecisionDiagnostics.ResolveDecisionSummary(
            isBlocked,
            decisionReasonType,
            decisionReasonCode,
            entity.BlockerDetail);

        var candidateMarketScore = entity.CandidateMarketScore is { } marketScore && marketScore > 100m
            ? null
            : entity.CandidateMarketScore;

        return new MarketScannerHandoffSnapshot(
            entity.Id,
            entity.ScanCycleId,
            entity.SelectedCandidateId,
            entity.SelectedSymbol,
            entity.SelectedTimeframe,
            NormalizeUtc(entity.SelectedAtUtc),
            entity.CandidateRank,
            candidateMarketScore,
            entity.CandidateScore,
            entity.SelectionReason,
            entity.OwnerUserId,
            entity.BotId,
            entity.StrategyKey,
            entity.TradingStrategyId,
            entity.TradingStrategyVersionId,
            entity.StrategySignalId,
            entity.StrategySignalVetoId,
            entity.StrategyDecisionOutcome,
            entity.StrategyVetoReasonCode,
            entity.StrategyScore,
            entity.RiskOutcome,
            entity.RiskVetoReasonCode,
            entity.RiskSummary,
            entity.RiskCurrentDailyLossPercentage,
            entity.RiskMaxDailyLossPercentage,
            entity.RiskCurrentWeeklyLossPercentage,
            entity.RiskMaxWeeklyLossPercentage,
            entity.RiskCurrentLeverage,
            entity.RiskProjectedLeverage,
            entity.RiskMaxLeverage,
            entity.RiskCurrentSymbolExposurePercentage,
            entity.RiskProjectedSymbolExposurePercentage,
            entity.RiskMaxSymbolExposurePercentage,
            entity.RiskCurrentOpenPositions,
            entity.RiskProjectedOpenPositions,
            entity.RiskMaxConcurrentPositions,
            entity.RiskBaseAsset,
            entity.RiskCurrentCoinExposurePercentage,
            entity.RiskProjectedCoinExposurePercentage,
            entity.RiskMaxCoinExposurePercentage,
            entity.ExecutionRequestStatus,
            entity.ExecutionSide,
            entity.ExecutionOrderType,
            entity.ExecutionEnvironment,
            entity.ExecutionQuantity,
            entity.ExecutionPrice,
            entity.BlockerCode,
            entity.BlockerDetail,
            ResolveHandoffBlockerSummary(entity, isBlocked, decisionSummary),
            entity.GuardSummary,
            entity.CorrelationId,
            decisionAtUtc,
            ExecutionDecisionDiagnostics.ResolveDecisionOutcome(isBlocked),
            decisionAtUtc,
            decisionReasonType,
            decisionReasonCode,
            decisionSummary,
            marketDataLastCandleAtUtc,
            ExecutionDecisionDiagnostics.ResolveDecisionDataAgeMilliseconds(
                degradedModeState,
                decisionAtUtc,
                entity.BlockerDetail,
                entity.GuardSummary),
            staleThresholdMilliseconds,
            ExecutionDecisionDiagnostics.ResolveStaleReason(latencyReasonCode),
            continuityState,
            continuityGapCount,
            continuityGapStartedAtUtc,
            continuityGapLastSeenAtUtc,
            continuityRecoveredAtUtc);
    }
    private static CoinBot.Application.Abstractions.Monitoring.HealthSnapshot MapHealthSnapshot(HealthSnapshotEntity entity)
    {
        return new CoinBot.Application.Abstractions.Monitoring.HealthSnapshot(
            entity.SnapshotKey,
            entity.SentinelName,
            entity.DisplayName,
            entity.HealthState,
            entity.FreshnessTier,
            entity.CircuitBreakerState,
            entity.LastUpdatedAtUtc,
            new MonitoringMetricsSnapshot(
                entity.BinancePingMs,
                entity.WebSocketStaleDurationSeconds,
                entity.LastMessageAgeSeconds,
                entity.ReconnectCount,
                entity.StreamGapCount,
                entity.RateLimitUsage,
                entity.DbLatencyMs,
                entity.RedisLatencyMs,
                TryReadDetailMetric(entity.Detail, "ClockDriftMs"),
                entity.SignalRActiveConnectionCount,
                entity.WorkerLastHeartbeatAtUtc,
                entity.ConsecutiveFailureCount,
                entity.SnapshotAgeSeconds),
            entity.Detail,
            entity.ObservedAtUtc);
    }

    private static string? ResolveHandoffBlockerSummary(
        MarketScannerHandoffAttemptEntity entity,
        bool isBlocked,
        string decisionSummary)
    {
        if (!string.IsNullOrWhiteSpace(entity.BlockerSummary))
        {
            return entity.BlockerSummary.Trim();
        }

        if (!isBlocked)
        {
            return "Allowed: execution request prepared.";
        }

        var normalizedCode = string.IsNullOrWhiteSpace(entity.BlockerCode)
            ? "Blocked"
            : entity.BlockerCode.Trim();
        var humanSummary = ExecutionDecisionDiagnostics.ExtractHumanSummary(entity.BlockerDetail)
            ?? ExecutionDecisionDiagnostics.ExtractHumanSummary(entity.GuardSummary)
            ?? decisionSummary;

        return string.IsNullOrWhiteSpace(humanSummary)
            ? $"{normalizedCode}: scanner handoff blocked execution."
            : $"{normalizedCode}: {humanSummary}";
    }

    private static MarketScannerCandidateSnapshot MapMarketScannerCandidate(MarketScannerCandidateEntity entity)
    {
        return new MarketScannerCandidateSnapshot(
            entity.Symbol,
            entity.UniverseSource,
            NormalizeUtc(entity.ObservedAtUtc),
            entity.LastCandleAtUtc.HasValue ? NormalizeUtc(entity.LastCandleAtUtc.Value) : null,
            entity.LastPrice,
            entity.QuoteVolume24h,
            entity.MarketScore,
            entity.StrategyScore,
            entity.ScoringSummary,
            ResolveCandidateAdvisoryLabels(entity.ScoringSummary),
            ResolveCandidateAdvisoryReasonCodes(entity.ScoringSummary),
            ResolveCandidateAdvisorySummary(entity.ScoringSummary),
            ResolveCandidateAdvisoryShadowScore(entity.ScoringSummary),
            ResolveCandidateAdvisoryShadowContributions(entity.ScoringSummary),
            entity.IsEligible,
            entity.RejectionReason,
            entity.Score,
            entity.Rank,
            entity.IsTopCandidate,
            ResolveCandidateAiRankingMode(entity.ScoringSummary),
            ResolveCandidateAiRankingClassicalScore(entity.ScoringSummary),
            ResolveCandidateAiRankingCombinedScore(entity.ScoringSummary),
            ResolveCandidateAiRankingInfluenceWeight(entity.ScoringSummary),
            ResolveCandidateAiRankingOutcomeCoveragePercent(entity.ScoringSummary),
            ResolveCandidateAiRankingFallbackReason(entity.ScoringSummary),
            ResolveCandidateAiRankingAdaptiveFilterState(entity.ScoringSummary),
            ResolveCandidateAiRankingAdaptiveFilterReason(entity.ScoringSummary));
    }

    private static IReadOnlyCollection<string> ResolveCandidateAdvisoryLabels(string? scoringSummary)
    {
        var labelsValue = ExecutionDecisionDiagnostics.ExtractToken("ScannerLabels", scoringSummary);
        if (string.IsNullOrWhiteSpace(labelsValue))
        {
            return Array.Empty<string>();
        }

        return labelsValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyCollection<string> ResolveCandidateAdvisoryReasonCodes(string? scoringSummary)
    {
        var reasonCodesValue = ExecutionDecisionDiagnostics.ExtractToken("ScannerReasonCodes", scoringSummary);
        if (string.IsNullOrWhiteSpace(reasonCodesValue))
        {
            return Array.Empty<string>();
        }

        return reasonCodesValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? ResolveCandidateAdvisorySummary(string? scoringSummary)
    {
        return ExecutionDecisionDiagnostics.ExtractToken("ScannerReasonSummary", scoringSummary);
    }

    private static int? ResolveCandidateAdvisoryShadowScore(string? scoringSummary)
    {
        var shadowScoreValue = ExecutionDecisionDiagnostics.ExtractToken("ScannerShadowScore", scoringSummary);
        return int.TryParse(shadowScoreValue, out var shadowScore)
            ? shadowScore
            : null;
    }

    private static IReadOnlyCollection<string> ResolveCandidateAdvisoryShadowContributions(string? scoringSummary)
    {
        var contributionsValue = ExecutionDecisionDiagnostics.ExtractToken("ScannerShadowContributions", scoringSummary);
        if (string.IsNullOrWhiteSpace(contributionsValue))
        {
            return Array.Empty<string>();
        }

        var contributions = new List<string>();
        foreach (var item in contributionsValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            var formatted = FormatShadowContribution(item);
            if (contributions.Contains(formatted, StringComparer.Ordinal))
            {
                continue;
            }

            contributions.Add(formatted);
        }

        return contributions.ToArray();
    }

    private static string? ResolveCandidateAiRankingMode(string? scoringSummary)
    {
        return ExecutionDecisionDiagnostics.ExtractToken("ScannerRankingMode", scoringSummary);
    }

    private static decimal? ResolveCandidateAiRankingClassicalScore(string? scoringSummary)
    {
        return TryResolveDecimalToken(scoringSummary, "ScannerClassicalScore");
    }

    private static decimal? ResolveCandidateAiRankingCombinedScore(string? scoringSummary)
    {
        return TryResolveDecimalToken(scoringSummary, "ScannerCombinedScore");
    }

    private static decimal? ResolveCandidateAiRankingInfluenceWeight(string? scoringSummary)
    {
        return TryResolveDecimalToken(scoringSummary, "ScannerAiInfluenceWeight");
    }

    private static decimal? ResolveCandidateAiRankingOutcomeCoveragePercent(string? scoringSummary)
    {
        return TryResolveDecimalToken(scoringSummary, "ScannerOutcomeCoveragePercent");
    }

    private static string? ResolveCandidateAiRankingFallbackReason(string? scoringSummary)
    {
        return ExecutionDecisionDiagnostics.ExtractToken("ScannerRankingFallbackReason", scoringSummary);
    }

    private static string? ResolveCandidateAiRankingAdaptiveFilterState(string? scoringSummary)
    {
        return ExecutionDecisionDiagnostics.ExtractToken("ScannerAdaptiveFilterState", scoringSummary);
    }

    private static string? ResolveCandidateAiRankingAdaptiveFilterReason(string? scoringSummary)
    {
        return ExecutionDecisionDiagnostics.ExtractToken("ScannerAdaptiveFilterReason", scoringSummary);
    }

    private static decimal? TryResolveDecimalToken(string? scoringSummary, string token)
    {
        var value = ExecutionDecisionDiagnostics.ExtractToken(token, scoringSummary);
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedValue)
            ? parsedValue
            : null;
    }

    private static int ResolveAiRankingTopCandidateChangedCount(IReadOnlyCollection<MarketScannerCandidateEntity> entities)
    {
        var eligibleEntities = entities
            .Where(entity => entity.IsEligible)
            .ToArray();
        if (eligibleEntities.Length == 0)
        {
            return 0;
        }

        if (!eligibleEntities.Any(entity =>
                string.Equals(ResolveCandidateAiRankingMode(entity.ScoringSummary), "AdvisoryCombined", StringComparison.Ordinal)))
        {
            return 0;
        }

        var aiTopCandidate = eligibleEntities
            .OrderBy(entity => entity.Rank ?? int.MaxValue)
            .ThenBy(entity => entity.Symbol, StringComparer.Ordinal)
            .FirstOrDefault();
        var classicalTopCandidate = eligibleEntities
            .OrderByDescending(entity => ResolveCandidateAiRankingClassicalScore(entity.ScoringSummary) ?? entity.Score)
            .ThenBy(entity => entity.Symbol, StringComparer.Ordinal)
            .FirstOrDefault();

        return aiTopCandidate is not null &&
               classicalTopCandidate is not null &&
               !string.Equals(aiTopCandidate.Symbol, classicalTopCandidate.Symbol, StringComparison.Ordinal)
            ? 1
            : 0;
    }

    private static AiRankingStatusSnapshot ResolveAiRankingStatus(
        IReadOnlyCollection<MarketScannerCandidateEntity> validCandidateEntities,
        IReadOnlyCollection<MarketScannerCandidateEntity> topCandidateEntities)
    {
        var eligibleTopCandidates = topCandidateEntities
            .Where(entity => entity.IsEligible)
            .OrderBy(entity => entity.Rank ?? int.MaxValue)
            .ThenBy(entity => entity.Symbol, StringComparer.Ordinal)
            .ToArray();
        if (eligibleTopCandidates.Length > 0)
        {
            var rankingModes = eligibleTopCandidates
                .Select(entity => ResolveCandidateAiRankingMode(entity.ScoringSummary))
                .Where(mode => !string.IsNullOrWhiteSpace(mode))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (rankingModes.Length == 1 && string.Equals(rankingModes[0], "Disabled", StringComparison.Ordinal))
            {
                return new AiRankingStatusSnapshot(
                    "AI ranking disabled",
                    "AI ranking disabled — classical ranking active.",
                    "Disabled");
            }

            return new AiRankingStatusSnapshot(
                "AI ranking active",
                "AI ranking explainability is attached to eligible ranked candidates in the latest scanner cycle.",
                rankingModes.Length == 0
                    ? null
                    : string.Join(" · ", rankingModes));
        }

        var safeReasons = validCandidateEntities
            .SelectMany(ResolveAiRankingStatusReasons)
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        return new AiRankingStatusSnapshot(
            "AI ranking not active",
            "AI ranking not active: no eligible ranked candidates in latest scanner cycle.",
            safeReasons.Length == 0
                ? null
                : string.Join(" · ", safeReasons));
    }

    private static IEnumerable<string> ResolveAiRankingStatusReasons(MarketScannerCandidateEntity entity)
    {
        var rankingMode = ResolveCandidateAiRankingMode(entity.ScoringSummary);
        if (!string.IsNullOrWhiteSpace(rankingMode))
        {
            yield return rankingMode;
        }

        var adaptiveFilterReason = ResolveCandidateAiRankingAdaptiveFilterReason(entity.ScoringSummary);
        if (!string.IsNullOrWhiteSpace(adaptiveFilterReason))
        {
            yield return adaptiveFilterReason;
        }

        if (!string.IsNullOrWhiteSpace(entity.RejectionReason))
        {
            yield return entity.RejectionReason.Trim();
        }
    }

    private static string FormatShadowContribution(string contribution)
    {
        var separatorIndex = contribution.LastIndexOf(':');
        if (separatorIndex <= 0 || separatorIndex >= contribution.Length - 1)
        {
            return contribution.Trim();
        }

        var reasonCode = contribution[..separatorIndex].Trim();
        var points = contribution[(separatorIndex + 1)..].Trim();
        return string.IsNullOrWhiteSpace(reasonCode) || string.IsNullOrWhiteSpace(points)
            ? contribution.Trim()
            : $"{reasonCode} {points}";
    }

    private static string? BuildMarketScannerCycleSummary(
        int scannedSymbolCount,
        MarketScannerCandidateEntity? bestCandidate,
        IReadOnlyCollection<MarketScannerCandidateEntity> candidates,
        string? persistedSummary)
    {
        if (bestCandidate is not null)
        {
            return string.IsNullOrWhiteSpace(persistedSummary)
                ? $"Market scanner evaluated {scannedSymbolCount} symbols and ranked {bestCandidate.Symbol} #{bestCandidate.Rank} with score {bestCandidate.Score.ToString("0.####", CultureInfo.InvariantCulture)}."
                : persistedSummary;
        }

        if (scannedSymbolCount <= 0)
        {
            return string.IsNullOrWhiteSpace(persistedSummary)
                ? "Market scanner found no universe symbols."
                : persistedSummary;
        }

        var rejectionSummary = BuildMarketScannerRejectionSummary(candidates);
        return string.IsNullOrWhiteSpace(rejectionSummary)
            ? $"Market scanner evaluated {scannedSymbolCount} symbols and found no eligible candidates."
            : $"Market scanner evaluated {scannedSymbolCount} symbols and found no eligible candidates. Reasons={rejectionSummary}.";
    }

    private static string? BuildMarketScannerRejectionSummary(IReadOnlyCollection<MarketScannerCandidateEntity> candidates)
    {
        var rejectionGroups = candidates
            .Where(entity => !entity.IsEligible && !string.IsNullOrWhiteSpace(entity.RejectionReason))
            .GroupBy(entity => entity.RejectionReason!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(4)
            .Select(group =>
            {
                var sampleSymbols = group
                    .Select(entity => entity.Symbol)
                    .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(symbol => symbol, StringComparer.Ordinal)
                    .Take(2)
                    .ToArray();
                var samples = sampleSymbols.Length == 0
                    ? string.Empty
                    : $" [{string.Join(',', sampleSymbols)}]";
                return $"{group.Key}:{group.Count()}{samples}";
            })
            .ToArray();

        return rejectionGroups.Length == 0
            ? null
            : string.Join(" | ", rejectionGroups);
    }


    private static int? TryReadDetailMetric(string? detail, string metricName)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var segments = detail.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = segment[..separatorIndex].Trim();
            if (!string.Equals(key, metricName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = segment[(separatorIndex + 1)..].Trim();

            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue)
                ? parsedValue
                : null;
        }

        return null;
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

    private static CoinBot.Application.Abstractions.Monitoring.WorkerHeartbeat MapWorkerHeartbeat(WorkerHeartbeatEntity entity)
    {
        return new CoinBot.Application.Abstractions.Monitoring.WorkerHeartbeat(
            entity.WorkerKey,
            entity.WorkerName,
            entity.HealthState,
            entity.FreshnessTier,
            entity.CircuitBreakerState,
            entity.LastHeartbeatAtUtc,
            entity.LastUpdatedAtUtc,
            entity.ConsecutiveFailureCount,
            entity.LastErrorCode,
            entity.LastErrorMessage,
            entity.SnapshotAgeSeconds,
            entity.Detail);
    }

    private sealed record AiRankingStatusSnapshot(
        string Title,
        string Summary,
        string? Reason);
}
