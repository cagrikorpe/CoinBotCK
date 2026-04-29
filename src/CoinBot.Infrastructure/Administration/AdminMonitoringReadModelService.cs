using System.Globalization;
using CoinBot.Application.Abstractions.Ai;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Jobs;
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
    IOptions<BotExecutionPilotOptions>? botExecutionPilotOptions = null,
    ISharedMarketDataCacheObservabilityCollector? sharedMarketDataCacheObservabilityCollector = null,
    IUltraDebugLogService? ultraDebugLogService = null) : IAdminMonitoringReadModelService
{
    private static readonly object CacheKey = new();
    private static readonly MemoryCacheEntryOptions SnapshotCacheOptions = new MemoryCacheEntryOptions()
        .SetSize(1)
        .SetAbsoluteExpiration(TimeSpan.FromSeconds(3));
    private readonly int staleThresholdMilliseconds = checked(dataLatencyGuardOptions.Value.StaleDataThresholdSeconds * 1000);
    private readonly BotExecutionPilotOptions botExecutionPilotOptionsValue = botExecutionPilotOptions?.Value ?? new();

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
                    var operationalObservability = await LoadOperationalObservabilityAsync(
                        utcNow,
                        healthSnapshots,
                        workerHeartbeats,
                        scannerSnapshot,
                        ultraDebugLogHealth,
                        cancellationToken);

                    return new MonitoringDashboardSnapshot(
                        healthSnapshots.Select(MapHealthSnapshot).ToArray(),
                        workerHeartbeats.Select(MapWorkerHeartbeat).ToArray(),
                        utcNow)
                    {
                        MarketScanner = scannerSnapshot,
                        UltraDebugLogHealth = ultraDebugLogHealth,
                        OperationalObservability = operationalObservability,
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

    private async Task<OperationalObservabilitySnapshot> LoadOperationalObservabilityAsync(
        DateTime utcNow,
        IReadOnlyCollection<HealthSnapshotEntity> healthSnapshotEntities,
        IReadOnlyCollection<WorkerHeartbeatEntity> workerHeartbeatEntities,
        MarketScannerDashboardSnapshot scannerSnapshot,
        UltraDebugLogHealthSnapshot ultraDebugLogHealth,
        CancellationToken cancellationToken)
    {
        var windowStartUtc = utcNow.AddHours(-24);
        var blockedHandoffRows = await dbContext.MarketScannerHandoffAttempts
            .AsNoTracking()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.CompletedAtUtc >= windowStartUtc &&
                entity.ExecutionRequestStatus != "Prepared")
            .OrderByDescending(entity => entity.CompletedAtUtc)
            .Take(50)
            .Select(entity => new
            {
                entity.BlockerCode
            })
            .ToListAsync(cancellationToken);
        var exitPnlEvidence = await LoadExitPnlEvidenceAsync(windowStartUtc, cancellationToken);
        var strategyProfitQuality = await LoadStrategyProfitQualityAsync(utcNow.AddDays(-30), cancellationToken);
        var enabledBotRows = await dbContext.TradingBots
            .AsNoTracking()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.IsEnabled &&
                entity.ExchangeAccountId.HasValue &&
                entity.Symbol != null)
            .Select(entity => new MonitoringBotScopeRow(
                entity.Id,
                entity.OwnerUserId,
                entity.ExchangeAccountId!.Value,
                entity.Symbol!))
            .ToListAsync(cancellationToken);
        var openPositionRows = await dbContext.ExchangePositions
            .AsNoTracking()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.Plane == ExchangeDataPlane.Futures &&
                entity.Quantity != 0m)
            .Select(entity => new MonitoringOpenPositionRow(
                entity.ExchangeAccountId,
                entity.OwnerUserId,
                entity.Symbol))
            .ToListAsync(cancellationToken);
        var syncStateRows = await dbContext.ExchangeAccountSyncStates
            .AsNoTracking()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.Plane == ExchangeDataPlane.Futures)
            .OrderByDescending(entity => entity.UpdatedDate)
            .ThenByDescending(entity => entity.CreatedDate)
            .ToListAsync(cancellationToken);
        var privatePlaneStaleRejectCount = await dbContext.ExecutionOrders
            .AsNoTracking()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.CreatedDate >= windowStartUtc &&
                !entity.SubmittedToBroker &&
                entity.FailureCode == "PrivatePlaneStale")
            .CountAsync(cancellationToken);
        var decisionRows = await dbContext.AiShadowDecisions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.CreatedDate >= windowStartUtc)
            .OrderByDescending(entity => entity.CreatedDate)
            .Take(200)
            .Select(entity => new
            {
                entity.Id,
                entity.FinalAction,
                entity.NoSubmitReason,
                entity.CreatedDate
            })
            .ToListAsync(cancellationToken);
        var decisionIds = decisionRows
            .Select(entity => entity.Id)
            .ToArray();
        var outcomeRows = decisionIds.Length == 0
            ? Array.Empty<Guid>()
            : await dbContext.AiShadowDecisionOutcomes
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    !entity.IsDeleted &&
                    decisionIds.Contains(entity.AiShadowDecisionId) &&
                    entity.HorizonKind == AiShadowOutcomeDefaults.OfficialHorizonKind &&
                    entity.HorizonValue == AiShadowOutcomeDefaults.OfficialHorizonValue)
                .Select(entity => entity.AiShadowDecisionId)
                .Distinct()
                .ToArrayAsync(cancellationToken);
        var executionSwitchEntity = await dbContext.GlobalExecutionSwitches
            .AsNoTracking()
            .OrderByDescending(entity => entity.UpdatedDate)
            .FirstOrDefaultAsync(cancellationToken);
        var globalSystemStateEntity = await dbContext.GlobalSystemStates
            .AsNoTracking()
            .OrderByDescending(entity => entity.UpdatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        var blockedReasons = BuildReasonBuckets(
            blockedHandoffRows.Select(entity => entity.BlockerCode),
            4,
            "Blocked");
        var noSubmitReasons = BuildReasonBuckets(
            decisionRows
                .Where(entity => !string.Equals(entity.FinalAction, "Submit", StringComparison.OrdinalIgnoreCase))
                .Select(entity => entity.NoSubmitReason),
            4,
            "NoSubmit");
        var executionReadiness = BuildExecutionReadinessSnapshot(
            executionSwitchEntity,
            globalSystemStateEntity,
            scannerSnapshot.LastSuccessfulHandoff,
            scannerSnapshot.LastBlockedHandoff);
        var recentDecisionCount = decisionRows.Count;
        var recentOutcomeCount = outcomeRows.Length;
        var coveragePercent = recentDecisionCount == 0
            ? 0m
            : Math.Round((decimal)recentOutcomeCount * 100m / recentDecisionCount, 2, MidpointRounding.AwayFromZero);
        var coverageSummary = recentDecisionCount == 0
            ? "No recent shadow decisions."
            : $"{recentOutcomeCount}/{recentDecisionCount} recent shadow decisions scored at official horizon.";
        var systemHealthState = ResolveSystemHealthState(healthSnapshotEntities, workerHeartbeatEntities);
        var systemHealthSummary = BuildSystemHealthSummary(healthSnapshotEntities, workerHeartbeatEntities);
        var workerHeartbeatSummary = BuildWorkerHeartbeatSummary(workerHeartbeatEntities);
        var diskPressureState = ResolveObservableDiskPressureState(ultraDebugLogHealth);
        var logSystemState = ResolveLogSystemState(ultraDebugLogHealth);
        var janitorSummary = BuildJanitorSummary(ultraDebugLogHealth);
        var exportSummary = BuildExportSummary(ultraDebugLogHealth);
        var pilotConfigEvidence = BuildPilotConfigEvidence(botExecutionPilotOptionsValue);
        var privateSyncEvidence = BuildPrivateSyncEvidence(
            utcNow,
            botExecutionPilotOptionsValue,
            enabledBotRows,
            openPositionRows,
            syncStateRows,
            privatePlaneStaleRejectCount);
        var criticalWarnings = BuildCriticalWarnings(
            healthSnapshotEntities,
            workerHeartbeatEntities,
            scannerSnapshot,
            ultraDebugLogHealth,
            diskPressureState,
            executionReadiness,
            recentDecisionCount,
            coveragePercent);

        return new OperationalObservabilitySnapshot(
            OverallState: ResolveOverallState(systemHealthState, executionReadiness.State, logSystemState, recentDecisionCount, coveragePercent),
            SystemHealthState: systemHealthState,
            SystemHealthSummary: systemHealthSummary,
            WorkerHeartbeatSummary: workerHeartbeatSummary,
            LastScannerCycleCompletedAtUtc: scannerSnapshot.LastScanCompletedAtUtc,
            LastScannerCycleSummary: scannerSnapshot.CycleSummary ?? "No scanner cycle yet.",
            TopCandidateSymbol: scannerSnapshot.BestCandidateSymbol,
            EligibleCandidateCount: scannerSnapshot.EligibleCandidateCount,
            LatestHandoffSummary: BuildLatestHandoffSummary(scannerSnapshot.LatestHandoff),
            ExecutionReadiness: executionReadiness,
            RecentAiShadowDecisionCount: recentDecisionCount,
            RecentAiShadowDecisionOutcomeCount: recentOutcomeCount,
            AiShadowOutcomeCoveragePercent: coveragePercent,
            AiShadowOutcomeCoverageSummary: coverageSummary,
            BlockedReasons: blockedReasons,
            NoSubmitReasons: noSubmitReasons,
            LogSystemState: logSystemState,
            DiskPressureState: diskPressureState,
            JanitorSummary: janitorSummary,
            ExportSummary: exportSummary,
            CriticalWarnings: criticalWarnings)
        {
            ExitPnlEvidence = exitPnlEvidence,
            StrategyProfitQuality = strategyProfitQuality,
            PilotConfigEvidence = pilotConfigEvidence,
            PrivateSyncEvidence = privateSyncEvidence
        };
    }

    private async Task<OperationalExitPnlEvidenceSnapshot> LoadExitPnlEvidenceAsync(
        DateTime windowStartUtc,
        CancellationToken cancellationToken)
    {
        var exitRows = await dbContext.DecisionTraces
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.CreatedAtUtc >= windowStartUtc &&
                entity.DecisionSummary != null &&
                entity.DecisionSummary.Contains("ExitReason="))
            .OrderByDescending(entity => entity.CreatedAtUtc)
            .Take(200)
            .Select(entity => new
            {
                entity.Symbol,
                entity.SignalType,
                entity.DecisionReasonCode,
                entity.DecisionSummary,
                entity.DecisionAtUtc,
                entity.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        var parsedRows = exitRows
            .Select(entity => ParseExitPnlEvidence(
                entity.Symbol,
                entity.SignalType,
                entity.DecisionReasonCode,
                entity.DecisionSummary,
                entity.DecisionAtUtc,
                entity.CreatedAtUtc))
            .Where(entity => entity is not null)
            .Cast<ExitPnlEvidenceRow>()
            .ToArray();

        if (parsedRows.Length == 0)
        {
            return OperationalExitPnlEvidenceSnapshot.Empty();
        }

        var latestRow = parsedRows
            .OrderByDescending(entity => entity.ObservedAtUtc)
            .ThenBy(entity => entity.Symbol, StringComparer.Ordinal)
            .First();

        return new OperationalExitPnlEvidenceSnapshot(
            LastExitCount: parsedRows.Length,
            ProfitableExitCount: parsedRows.Count(entity => entity.EstimatedPnlQuote.GetValueOrDefault() > 0m),
            UnprofitableExitBlockedCount: parsedRows.Count(entity => string.Equals(entity.ExitReason, "BlockedUnprofitable", StringComparison.Ordinal)),
            StopLossExitCount: parsedRows.Count(entity => string.Equals(entity.ExitReason, "StopLoss", StringComparison.Ordinal)),
            TakeProfitExitCount: parsedRows.Count(entity => string.Equals(entity.ExitReason, "TakeProfit", StringComparison.Ordinal)),
            LastExitReason: latestRow.ExitReason,
            LastEstimatedPnlQuote: latestRow.EstimatedPnlQuote,
            LastEstimatedPnlPct: latestRow.EstimatedPnlPct,
            LastExitAtUtc: latestRow.ObservedAtUtc,
            LastExitSymbol: latestRow.Symbol,
            LastExitSide: latestRow.CloseSide,
            LastExitReduceOnly: latestRow.ReduceOnly);
    }

    private async Task<OperationalStrategyProfitQualitySnapshot> LoadStrategyProfitQualityAsync(
        DateTime windowStartUtc,
        CancellationToken cancellationToken)
    {
        var orderRows = await dbContext.ExecutionOrders
            .AsNoTracking()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.Plane == ExchangeDataPlane.Futures &&
                entity.ExecutionEnvironment == ExecutionEnvironment.BinanceTestnet &&
                entity.SubmittedToBroker &&
                entity.CreatedDate >= windowStartUtc &&
                (entity.SignalType == StrategySignalType.Entry || entity.SignalType == StrategySignalType.Exit) &&
                entity.Quantity > 0m)
            .OrderBy(entity => entity.CreatedDate)
            .ThenBy(entity => entity.SubmittedAtUtc ?? entity.CreatedDate)
            .ThenBy(entity => entity.Id)
            .Take(500)
            .Select(entity => new StrategyProfitOrderRow(
                entity.Id,
                entity.StrategySignalId,
                entity.BotId,
                entity.ExchangeAccountId,
                entity.OwnerUserId,
                entity.StrategyKey,
                entity.Symbol,
                entity.SignalType,
                entity.Side,
                entity.Quantity,
                entity.FilledQuantity,
                entity.Price,
                entity.AverageFillPrice,
                entity.ReduceOnly,
                entity.CreatedDate,
                entity.SubmittedAtUtc,
                entity.LastStateChangedAtUtc))
            .ToListAsync(cancellationToken);

        if (orderRows.Count == 0)
        {
            return OperationalStrategyProfitQualitySnapshot.Empty();
        }

        var strategySignalIds = orderRows
            .Select(entity => entity.StrategySignalId)
            .Distinct()
            .ToArray();
        var signalTypeById = strategySignalIds.Length == 0
            ? new Dictionary<Guid, StrategySignalType>()
            : await dbContext.TradingStrategySignals
                .AsNoTracking()
                .Where(entity => strategySignalIds.Contains(entity.Id) && !entity.IsDeleted)
                .ToDictionaryAsync(entity => entity.Id, entity => entity.SignalType, cancellationToken);
        var decisionTraceBySignalId = strategySignalIds.Length == 0
            ? new Dictionary<Guid, DecisionTraceSummaryRow>()
            : (await dbContext.DecisionTraces
                    .AsNoTracking()
                    .IgnoreQueryFilters()
                    .Where(entity =>
                        !entity.IsDeleted &&
                        entity.StrategySignalId.HasValue &&
                        strategySignalIds.Contains(entity.StrategySignalId.Value))
                    .OrderByDescending(entity => entity.CreatedAtUtc)
                    .ThenByDescending(entity => entity.CreatedDate)
                    .Select(entity => new
                    {
                        StrategySignalId = entity.StrategySignalId!.Value,
                        entity.DecisionReasonCode,
                        entity.DecisionSummary,
                        entity.DecisionAtUtc,
                        entity.CreatedAtUtc
                    })
                    .ToListAsync(cancellationToken))
                .GroupBy(entity => entity.StrategySignalId)
                .ToDictionary(
                    group => group.Key,
                    group =>
                    {
                        var latest = group.First();
                        return new DecisionTraceSummaryRow(
                            latest.DecisionReasonCode,
                            latest.DecisionSummary,
                            NormalizeUtc(latest.DecisionAtUtc ?? latest.CreatedAtUtc));
                    });

        var openEntryLots = new Dictionary<StrategyProfitPairingKey, Queue<OpenEntryLot>>();
        var pairedTrades = new List<PairedTradeEvidenceRow>();
        var unpairedExitCount = 0;

        foreach (var orderRow in orderRows)
        {
            var quantity = ResolveEffectiveOrderQuantity(orderRow);
            var price = ResolveEffectiveOrderPrice(orderRow);
            var trace = decisionTraceBySignalId.GetValueOrDefault(orderRow.StrategySignalId);
            var pairingKey = new StrategyProfitPairingKey(
                orderRow.OwnerUserId,
                orderRow.BotId,
                orderRow.ExchangeAccountId,
                orderRow.Symbol);

            if (orderRow.SignalType == StrategySignalType.Entry && !orderRow.ReduceOnly)
            {
                EnqueueOpenEntryLot(openEntryLots, pairingKey, new OpenEntryLot(
                    strategyKey: string.IsNullOrWhiteSpace(orderRow.StrategyKey) ? "n/a" : orderRow.StrategyKey.Trim(),
                    symbol: string.IsNullOrWhiteSpace(orderRow.Symbol) ? "n/a" : orderRow.Symbol.Trim(),
                    entrySide: orderRow.Side,
                    entrySource: ResolveEntrySource(trace),
                    entryPrice: price,
                    remainingQuantity: quantity,
                    usedEstimatedPricing: !(orderRow.AverageFillPrice.HasValue && orderRow.AverageFillPrice.Value > 0m),
                    openedAtUtc: NormalizeUtc(orderRow.SubmittedAtUtc ?? orderRow.LastStateChangedAtUtc)));
                continue;
            }

            if (orderRow.SignalType != StrategySignalType.Exit || !orderRow.ReduceOnly)
            {
                continue;
            }

            if (quantity <= 0m || !price.HasValue || price.Value <= 0m)
            {
                unpairedExitCount++;
                continue;
            }

            if (!openEntryLots.TryGetValue(pairingKey, out var openLots) || openLots.Count == 0)
            {
                unpairedExitCount++;
                continue;
            }

            var remainingExitQuantity = quantity;
            while (remainingExitQuantity > 0m && openLots.Count > 0)
            {
                var openLot = openLots.Peek();
                if (openLot.RemainingQuantity <= 0m || !openLot.EntryPrice.HasValue || openLot.EntryPrice.Value <= 0m)
                {
                    openLots.Dequeue();
                    continue;
                }

                var matchedQuantity = Math.Min(openLot.RemainingQuantity, remainingExitQuantity);
                var netPnlQuote = CalculateNetPnlQuote(openLot.EntrySide, openLot.EntryPrice.Value, price.Value, matchedQuantity);
                var netPnlPct = CalculateNetPnlPct(openLot.EntrySide, openLot.EntryPrice.Value, price.Value);
                var mfeQuote = CalculateExcursionQuote(
                    openLot.EntrySide,
                    openLot.EntryPrice.Value,
                    matchedQuantity,
                    TryParseDecimalToken("PeakReferencePrice", trace?.DecisionSummary));
                var maeQuote = CalculateExcursionQuote(
                    openLot.EntrySide,
                    openLot.EntryPrice.Value,
                    matchedQuantity,
                    TryParseDecimalToken("AdverseReferencePrice", trace?.DecisionSummary));

                pairedTrades.Add(new PairedTradeEvidenceRow(
                    openLot.StrategyKey,
                    openLot.Symbol,
                    openLot.EntrySource,
                    ResolveExitSource(orderRow, trace, signalTypeById),
                    matchedQuantity,
                    netPnlQuote,
                    netPnlPct,
                    mfeQuote,
                    maeQuote,
                    NormalizeUtc(orderRow.SubmittedAtUtc ?? orderRow.LastStateChangedAtUtc),
                    openLot.UsedEstimatedPricing || !(orderRow.AverageFillPrice.HasValue && orderRow.AverageFillPrice.Value > 0m)));

                openLot.RemainingQuantity -= matchedQuantity;
                remainingExitQuantity -= matchedQuantity;

                if (openLot.RemainingQuantity <= 0m)
                {
                    openLots.Dequeue();
                }
            }

            if (remainingExitQuantity > 0m)
            {
                unpairedExitCount++;
            }
        }

        var unpairedEntryCount = openEntryLots.Values.Sum(queue => queue.Count(entity => entity.RemainingQuantity > 0m));
        if (pairedTrades.Count == 0)
        {
            return OperationalStrategyProfitQualitySnapshot.Empty() with
            {
                UnpairedEntryCount = unpairedEntryCount,
                UnpairedExitCount = unpairedExitCount,
                Summary = $"No paired trades found. Unpaired entries={unpairedEntryCount}; Unpaired exits={unpairedExitCount}."
            };
        }

        var overallWins = pairedTrades.Count(entity => entity.NetPnlQuote > 0m);
        var overallLosses = pairedTrades.Count(entity => entity.NetPnlQuote < 0m);
        var overallWinRate = pairedTrades.Count == 0
            ? 0m
            : Math.Round((decimal)overallWins * 100m / pairedTrades.Count, 2, MidpointRounding.AwayFromZero);
        var overallAverageProfit = ResolveAverageMetric(pairedTrades.Where(entity => entity.NetPnlQuote > 0m).Select(entity => entity.NetPnlQuote));
        var overallAverageLoss = ResolveAverageMetric(pairedTrades.Where(entity => entity.NetPnlQuote < 0m).Select(entity => entity.NetPnlQuote));
        var overallAverageNetPnlQuote = ResolveAverageMetric(pairedTrades.Select(entity => entity.NetPnlQuote));
        var overallAverageNetPnlPct = ResolveAverageMetric(pairedTrades.Select(entity => entity.NetPnlPct));
        var overallMaxFavorableExcursionQuote = pairedTrades
            .Where(entity => entity.MaxFavorableExcursionQuote.HasValue)
            .Select(entity => entity.MaxFavorableExcursionQuote!.Value)
            .DefaultIfEmpty()
            .Max();
        var overallMaxAdverseExcursionQuote = pairedTrades
            .Where(entity => entity.MaxAdverseExcursionQuote.HasValue)
            .Select(entity => entity.MaxAdverseExcursionQuote!.Value)
            .DefaultIfEmpty()
            .Max();
        var strategySummaries = pairedTrades
            .GroupBy(entity => new
            {
                entity.StrategyKey,
                entity.Symbol,
                entity.EntrySource,
                entity.ExitSource
            })
            .Select(group =>
            {
                var rows = group.ToArray();
                var wins = rows.Count(entity => entity.NetPnlQuote > 0m);
                var losses = rows.Count(entity => entity.NetPnlQuote < 0m);
                var winRate = rows.Length == 0
                    ? 0m
                    : Math.Round((decimal)wins * 100m / rows.Length, 2, MidpointRounding.AwayFromZero);
                var avgProfit = ResolveAverageMetric(rows.Where(entity => entity.NetPnlQuote > 0m).Select(entity => entity.NetPnlQuote));
                var avgLoss = ResolveAverageMetric(rows.Where(entity => entity.NetPnlQuote < 0m).Select(entity => entity.NetPnlQuote));
                var avgNetQuote = ResolveAverageMetric(rows.Select(entity => entity.NetPnlQuote));
                var avgNetPct = ResolveAverageMetric(rows.Select(entity => entity.NetPnlPct));
                var maxFavorable = rows
                    .Where(entity => entity.MaxFavorableExcursionQuote.HasValue)
                    .Select(entity => entity.MaxFavorableExcursionQuote!.Value)
                    .DefaultIfEmpty()
                    .Max();
                var maxAdverse = rows
                    .Where(entity => entity.MaxAdverseExcursionQuote.HasValue)
                    .Select(entity => entity.MaxAdverseExcursionQuote!.Value)
                    .DefaultIfEmpty()
                    .Max();
                var lastClosedTradeAtUtc = rows.Max(entity => entity.ClosedAtUtc);
                var usesEstimatedPricing = rows.Any(entity => entity.UsesEstimatedPricing);

                return new OperationalStrategyProfitQualityRowSnapshot(
                    StrategyKey: group.Key.StrategyKey,
                    Symbol: group.Key.Symbol,
                    EntrySource: group.Key.EntrySource,
                    ExitSource: group.Key.ExitSource,
                    PairedTradeCount: rows.Length,
                    WinCount: wins,
                    LossCount: losses,
                    WinRatePercent: winRate,
                    AverageProfitQuote: avgProfit,
                    AverageLossQuote: avgLoss,
                    AverageNetPnlQuote: avgNetQuote,
                    AverageNetPnlPct: avgNetPct,
                    MaxFavorableExcursionQuote: maxFavorable > 0m ? maxFavorable : null,
                    MaxAdverseExcursionQuote: maxAdverse > 0m ? maxAdverse : null,
                    LastClosedTradeAtUtc: lastClosedTradeAtUtc,
                    UsesEstimatedPricing: usesEstimatedPricing,
                    Summary: $"{rows.Length} trades · {wins}W/{losses}L · win {winRate.ToString("0.##", CultureInfo.InvariantCulture)}% · avg pnl {(avgNetQuote.HasValue ? avgNetQuote.Value.ToString("0.####", CultureInfo.InvariantCulture) : "n/a")}.");
            })
            .OrderByDescending(entity => entity.LastClosedTradeAtUtc)
            .ThenByDescending(entity => entity.PairedTradeCount)
            .ThenBy(entity => entity.StrategyKey, StringComparer.Ordinal)
            .ThenBy(entity => entity.Symbol, StringComparer.Ordinal)
            .ThenBy(entity => entity.ExitSource, StringComparer.Ordinal)
            .ToArray();

        var lastClosedTradeAtUtc = pairedTrades.Max(entity => entity.ClosedAtUtc);
        var summary = $"{pairedTrades.Count} paired trades · {overallWins}W/{overallLosses}L · win {overallWinRate.ToString("0.##", CultureInfo.InvariantCulture)}% · unpaired entries {unpairedEntryCount} · unpaired exits {unpairedExitCount}.";

        return new OperationalStrategyProfitQualitySnapshot(
            PairedTradeCount: pairedTrades.Count,
            UnpairedEntryCount: unpairedEntryCount,
            UnpairedExitCount: unpairedExitCount,
            WinCount: overallWins,
            LossCount: overallLosses,
            WinRatePercent: overallWinRate,
            AverageProfitQuote: overallAverageProfit,
            AverageLossQuote: overallAverageLoss,
            AverageNetPnlQuote: overallAverageNetPnlQuote,
            AverageNetPnlPct: overallAverageNetPnlPct,
            MaxFavorableExcursionQuote: overallMaxFavorableExcursionQuote > 0m ? overallMaxFavorableExcursionQuote : null,
            MaxAdverseExcursionQuote: overallMaxAdverseExcursionQuote > 0m ? overallMaxAdverseExcursionQuote : null,
            LastClosedTradeAtUtc: lastClosedTradeAtUtc,
            Summary: summary,
            StrategySummaries: strategySummaries);
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
        var recentBlockedReasonCodes = await dbContext.MarketScannerHandoffAttempts
            .AsNoTracking()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.ExecutionRequestStatus != "Prepared")
            .OrderByDescending(entity => entity.CompletedAtUtc)
            .ThenByDescending(entity => entity.CreatedDate)
            .Select(entity => entity.BlockerCode)
            .Take(100)
            .ToListAsync(cancellationToken);
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
            topCandidateEntities.Length,
            latestCycle.UniverseSource,
            bestCandidate?.Symbol,
            bestCandidate?.Score,
            BuildBestCandidateRankingSummary(bestCandidate),
            topCandidateEntities.Select(MapMarketScannerCandidate).ToArray(),
            rejectedSampleEntities.Select(MapMarketScannerCandidate).ToArray(),
            MapMarketScannerHandoffAttempt(latestHandoffEntity, ResolveDegradedModeState(latestHandoffEntity, degradedModeStates)),
            MapMarketScannerHandoffAttempt(lastSuccessfulHandoffEntity, ResolveDegradedModeState(lastSuccessfulHandoffEntity, degradedModeStates)),
            MapMarketScannerHandoffAttempt(lastBlockedHandoffEntity, ResolveDegradedModeState(lastBlockedHandoffEntity, degradedModeStates)),
            cycleSummary,
            rejectionSummary,
            BuildBlockedReasonSummary(recentBlockedReasonCodes, IsDirectionalConflictReasonCode),
            BuildBlockedReasonSummary(recentBlockedReasonCodes, IsSameDirectionReasonCode),
            BuildBlockedReasonSummary(recentBlockedReasonCodes, IsDuplicateReasonCode),
            BuildBlockedReasonSummary(recentBlockedReasonCodes, IsGuardrailReasonCode),
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
            TryParseDecimalToken("CandidateScore", entity.ScoringSummary),
            TryParseDecimalToken("RiskPenalty", entity.ScoringSummary),
            TryParseDecimalToken("VolatilityScore", entity.ScoringSummary),
            TryParseDecimalToken("LiquidityScore", entity.ScoringSummary),
            ExecutionDecisionDiagnostics.ExtractToken("TrendAlignment", entity.ScoringSummary),
            ExecutionDecisionDiagnostics.ExtractToken("DirectionalConflictStatus", entity.ScoringSummary),
            ExecutionDecisionDiagnostics.ExtractToken("RankingDecision", entity.ScoringSummary),
            ExecutionDecisionDiagnostics.ExtractToken("RankingReasonCode", entity.ScoringSummary),
            ExecutionDecisionDiagnostics.ExtractToken("RankingSummary", entity.ScoringSummary),
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

    private static string? BuildBestCandidateRankingSummary(MarketScannerCandidateEntity? bestCandidate)
    {
        if (bestCandidate is null)
        {
            return null;
        }

        var scoringSummary = bestCandidate.ScoringSummary;
        var rankingDecision = ExecutionDecisionDiagnostics.ExtractToken("RankingDecision", scoringSummary) ?? "Selected";
        var rankingReasonCode = ExecutionDecisionDiagnostics.ExtractToken("RankingReasonCode", scoringSummary) ?? "n/a";
        var candidateScore = TryParseDecimalToken("CandidateScore", scoringSummary) ?? bestCandidate.Score;
        var marketScore = TryParseDecimalToken("MarketScore", scoringSummary) ?? bestCandidate.MarketScore;
        var strategyScore = ExecutionDecisionDiagnostics.ExtractToken("StrategyScore", scoringSummary)
            ?? (bestCandidate.StrategyScore?.ToString(CultureInfo.InvariantCulture) ?? "n/a");
        var riskPenalty = TryParseDecimalToken("RiskPenalty", scoringSummary);
        var volatilityScore = TryParseDecimalToken("VolatilityScore", scoringSummary);
        var liquidityScore = TryParseDecimalToken("LiquidityScore", scoringSummary);
        var trendAlignment = ExecutionDecisionDiagnostics.ExtractToken("TrendAlignment", scoringSummary) ?? "n/a";
        var directionalConflictStatus = ExecutionDecisionDiagnostics.ExtractToken("DirectionalConflictStatus", scoringSummary) ?? "n/a";

        return $"{rankingDecision} · {rankingReasonCode} · Score {candidateScore.ToString("0.####", CultureInfo.InvariantCulture)} · Market {marketScore.ToString("0.####", CultureInfo.InvariantCulture)} · Strategy {strategyScore} · Risk penalty {(riskPenalty?.ToString("0.####", CultureInfo.InvariantCulture) ?? "n/a")} · Volatility {(volatilityScore?.ToString("0.####", CultureInfo.InvariantCulture) ?? "n/a")} · Liquidity {(liquidityScore?.ToString("0.####", CultureInfo.InvariantCulture) ?? "n/a")} · Trend {trendAlignment} · Conflict {directionalConflictStatus}";
    }

    private static string? BuildBlockedReasonSummary(
        IEnumerable<string?> reasonCodes,
        Func<string, bool> predicate)
    {
        var groups = reasonCodes
            .Select(reason => string.IsNullOrWhiteSpace(reason) ? null : reason.Trim())
            .Where(reason => reason is not null)
            .Cast<string>()
            .Where(predicate)
            .GroupBy(reason => reason, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(4)
            .Select(group => $"{group.Key}:{group.Count()}")
            .ToArray();

        return groups.Length == 0
            ? null
            : string.Join(" | ", groups);
    }

    private static bool IsDirectionalConflictReasonCode(string reasonCode)
    {
        return reasonCode.StartsWith("DirectionalConflict", StringComparison.Ordinal);
    }

    private static bool IsSameDirectionReasonCode(string reasonCode)
    {
        return reasonCode.StartsWith("SameDirection", StringComparison.Ordinal);
    }

    private static bool IsDuplicateReasonCode(string reasonCode)
    {
        return reasonCode.Contains("Duplicate", StringComparison.Ordinal);
    }

    private static bool IsGuardrailReasonCode(string reasonCode)
    {
        return reasonCode.StartsWith("Stale", StringComparison.Ordinal) ||
               reasonCode.StartsWith("Continuity", StringComparison.Ordinal) ||
               reasonCode.Contains("Cooldown", StringComparison.Ordinal) ||
               reasonCode.Contains("Allowlist", StringComparison.Ordinal) ||
               reasonCode.StartsWith("SymbolExecution", StringComparison.Ordinal) ||
               reasonCode.StartsWith("SymbolAllowlist", StringComparison.Ordinal) ||
               reasonCode.StartsWith("Risk", StringComparison.Ordinal) ||
               reasonCode.StartsWith("UserExecutionRisk", StringComparison.Ordinal) ||
               reasonCode.StartsWith("TradeMaster", StringComparison.Ordinal) ||
               reasonCode.StartsWith("PrivatePlaneStale", StringComparison.Ordinal);
    }

    private static string ResolveSystemHealthState(
        IReadOnlyCollection<HealthSnapshotEntity> healthSnapshotEntities,
        IReadOnlyCollection<WorkerHeartbeatEntity> workerHeartbeatEntities)
    {
        var severity = 0;
        foreach (var entity in healthSnapshotEntities)
        {
            severity = Math.Max(severity, ResolveMonitoringHealthSeverity(entity.HealthState));
        }

        foreach (var entity in workerHeartbeatEntities)
        {
            severity = Math.Max(severity, ResolveMonitoringHealthSeverity(entity.HealthState));
            severity = Math.Max(severity, ResolveWorkerFreshnessSeverity(entity.FreshnessTier));
        }

        return severity switch
        {
            >= 4 => "Critical",
            3 => "Degraded",
            2 => "Warning",
            1 => "Healthy",
            _ => "Unknown"
        };
    }

    private static string BuildSystemHealthSummary(
        IReadOnlyCollection<HealthSnapshotEntity> healthSnapshotEntities,
        IReadOnlyCollection<WorkerHeartbeatEntity> workerHeartbeatEntities)
    {
        if (healthSnapshotEntities.Count == 0 && workerHeartbeatEntities.Count == 0)
        {
            return "No health or worker heartbeat snapshot yet.";
        }

        var criticalSentinels = healthSnapshotEntities.Count(entity => entity.HealthState == MonitoringHealthState.Critical);
        var warningOrWorseSentinels = healthSnapshotEntities.Count(entity => entity.HealthState is MonitoringHealthState.Warning or MonitoringHealthState.Degraded or MonitoringHealthState.Critical);
        var staleWorkers = workerHeartbeatEntities.Count(entity => entity.FreshnessTier == MonitoringFreshnessTier.Stale);
        return $"{criticalSentinels} critical sentinels · {warningOrWorseSentinels} warning+ health signals · {staleWorkers} stale workers.";
    }

    private static string BuildWorkerHeartbeatSummary(IReadOnlyCollection<WorkerHeartbeatEntity> workerHeartbeatEntities)
    {
        if (workerHeartbeatEntities.Count == 0)
        {
            return "No worker heartbeat yet.";
        }

        var healthyWorkers = workerHeartbeatEntities.Count(entity => entity.HealthState == MonitoringHealthState.Healthy);
        var staleWorkers = workerHeartbeatEntities.Count(entity => entity.FreshnessTier == MonitoringFreshnessTier.Stale);
        var criticalWorkers = workerHeartbeatEntities.Count(entity => entity.HealthState == MonitoringHealthState.Critical);
        return $"{healthyWorkers}/{workerHeartbeatEntities.Count} workers healthy · {staleWorkers} stale · {criticalWorkers} critical.";
    }

    private static OperationalExecutionReadinessSnapshot BuildExecutionReadinessSnapshot(
        GlobalExecutionSwitch? executionSwitchEntity,
        GlobalSystemState? globalSystemStateEntity,
        MarketScannerHandoffSnapshot lastSuccessfulHandoff,
        MarketScannerHandoffSnapshot lastBlockedHandoff)
    {
        var tradeMasterState = executionSwitchEntity?.TradeMasterState.ToString();
        var defaultMode = executionSwitchEntity is null
            ? null
            : executionSwitchEntity.DemoModeEnabled
                ? ExecutionEnvironment.Demo.ToString()
                : ExecutionEnvironment.Live.ToString();
        var globalSystemState = globalSystemStateEntity?.State.ToString();
        var latestBlockedReasonCode = lastBlockedHandoff.DecisionReasonCode ?? lastBlockedHandoff.BlockerCode;

        if (globalSystemStateEntity is not null &&
            globalSystemStateEntity.State != GlobalSystemStateKind.Active)
        {
            return new OperationalExecutionReadinessSnapshot(
                State: "Blocked",
                Summary: $"Global system state {globalSystemStateEntity.State} blocks execution ({globalSystemStateEntity.ReasonCode}).",
                ReasonCode: globalSystemStateEntity.ReasonCode,
                TradeMasterState: tradeMasterState,
                DefaultMode: defaultMode,
                GlobalSystemState: globalSystemState,
                LatestPreparedAtUtc: lastSuccessfulHandoff.CompletedAtUtc,
                LatestBlockedAtUtc: lastBlockedHandoff.CompletedAtUtc,
                LatestBlockedReasonCode: latestBlockedReasonCode);
        }

        if (executionSwitchEntity is not null &&
            executionSwitchEntity.TradeMasterState != TradeMasterSwitchState.Armed)
        {
            return new OperationalExecutionReadinessSnapshot(
                State: "Blocked",
                Summary: $"Trade master {executionSwitchEntity.TradeMasterState}; classical execution gate stays fail-closed.",
                ReasonCode: nameof(TradeMasterSwitchState.Disarmed),
                TradeMasterState: tradeMasterState,
                DefaultMode: defaultMode,
                GlobalSystemState: globalSystemState,
                LatestPreparedAtUtc: lastSuccessfulHandoff.CompletedAtUtc,
                LatestBlockedAtUtc: lastBlockedHandoff.CompletedAtUtc,
                LatestBlockedReasonCode: latestBlockedReasonCode);
        }

        if (lastBlockedHandoff.CompletedAtUtc.HasValue &&
            (!lastSuccessfulHandoff.CompletedAtUtc.HasValue || lastBlockedHandoff.CompletedAtUtc >= lastSuccessfulHandoff.CompletedAtUtc))
        {
            return new OperationalExecutionReadinessSnapshot(
                State: "Watching",
                Summary: $"Latest handoff blocked by {latestBlockedReasonCode ?? "Unknown"} while execution controls remain armed.",
                ReasonCode: latestBlockedReasonCode,
                TradeMasterState: tradeMasterState,
                DefaultMode: defaultMode,
                GlobalSystemState: globalSystemState,
                LatestPreparedAtUtc: lastSuccessfulHandoff.CompletedAtUtc,
                LatestBlockedAtUtc: lastBlockedHandoff.CompletedAtUtc,
                LatestBlockedReasonCode: latestBlockedReasonCode);
        }

        if (executionSwitchEntity is null && globalSystemStateEntity is null)
        {
            return OperationalExecutionReadinessSnapshot.Empty();
        }

        return new OperationalExecutionReadinessSnapshot(
            State: "Ready",
            Summary: $"Trade master armed; global system {globalSystemState ?? "Unknown"}; default mode {defaultMode ?? "Unknown"}.",
            ReasonCode: null,
            TradeMasterState: tradeMasterState,
            DefaultMode: defaultMode,
            GlobalSystemState: globalSystemState,
            LatestPreparedAtUtc: lastSuccessfulHandoff.CompletedAtUtc,
            LatestBlockedAtUtc: lastBlockedHandoff.CompletedAtUtc,
            LatestBlockedReasonCode: latestBlockedReasonCode);
    }

    private static IReadOnlyCollection<OperationalReasonBucketSnapshot> BuildReasonBuckets(
        IEnumerable<string?> reasons,
        int take,
        string fallbackReasonCode)
    {
        return reasons
            .Select(reason => string.IsNullOrWhiteSpace(reason) ? fallbackReasonCode : reason.Trim())
            .GroupBy(reason => reason, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(take)
            .Select(group => new OperationalReasonBucketSnapshot(group.Key, group.Count()))
            .ToArray();
    }

    private static OperationalPilotConfigEvidenceSnapshot BuildPilotConfigEvidence(BotExecutionPilotOptions options)
    {
        var allowedSymbols = options.ResolveNormalizedAllowedSymbols();
        var allowedSymbolsSummary = allowedSymbols.Length switch
        {
            0 => "all",
            <= 5 => string.Join(", ", allowedSymbols),
            _ => string.Join(", ", allowedSymbols.Take(5)) + $" +{allowedSymbols.Length - 5} more"
        };

        return new OperationalPilotConfigEvidenceSnapshot(
            AutoManageAdoptedPositions: options.AutoManageAdoptedPositions,
            ExecutionDispatchMode: options.ExecutionDispatchMode.ToString(),
            AllowedSymbolCount: allowedSymbols.Length,
            AllowedSymbolsSummary: allowedSymbolsSummary,
            AllowedBotIdCount: options.ResolveNormalizedAllowedBotIds().Length,
            AllowedUserIdCount: options.ResolveNormalizedAllowedUserIds().Length,
            AllowedExchangeAccountIdCount: null);
    }

    private static OperationalPrivateSyncEvidenceSnapshot BuildPrivateSyncEvidence(
        DateTime utcNow,
        BotExecutionPilotOptions options,
        IReadOnlyCollection<MonitoringBotScopeRow> enabledBotRows,
        IReadOnlyCollection<MonitoringOpenPositionRow> openPositionRows,
        IReadOnlyCollection<ExchangeAccountSyncState> syncStateRows,
        int privatePlaneStaleRejectCount)
    {
        if (syncStateRows.Count == 0)
        {
            return OperationalPrivateSyncEvidenceSnapshot.Empty() with
            {
                Summary = $"No futures private sync evidence yet. PrivatePlaneStaleRejects={privatePlaneStaleRejectCount}.",
                PrivatePlaneStaleRejectCount = privatePlaneStaleRejectCount
            };
        }

        var latestSyncRows = syncStateRows
            .GroupBy(entity => new MonitoringAccountScopeKey(entity.ExchangeAccountId, entity.OwnerUserId, entity.Plane))
            .Select(group => group
                .OrderByDescending(entity => entity.UpdatedDate)
                .ThenByDescending(entity => entity.CreatedDate)
                .First())
            .ToArray();
        var enabledBotScopeKeys = enabledBotRows
            .Select(entity => new MonitoringAccountScopeKey(entity.ExchangeAccountId, entity.OwnerUserId, ExchangeDataPlane.Futures))
            .ToHashSet();
        var openPositionKeys = openPositionRows
            .Select(entity => new MonitoringPositionScopeKey(
                entity.ExchangeAccountId,
                entity.OwnerUserId,
                NormalizeSymbol(entity.Symbol)))
            .ToHashSet();
        var allowedSymbolSet = new HashSet<string>(options.ResolveNormalizedAllowedSymbols(), StringComparer.Ordinal);
        var allowedBotIdSet = new HashSet<string>(options.ResolveNormalizedAllowedBotIds(), StringComparer.OrdinalIgnoreCase);
        var allowedUserIdSet = new HashSet<string>(options.ResolveNormalizedAllowedUserIds(), StringComparer.Ordinal);
        var pilotScopedBotRows = enabledBotRows
            .Where(entity => MatchesPilotScope(entity, allowedSymbolSet, allowedBotIdSet, allowedUserIdSet))
            .ToArray();
        var pilotScopedAccountKeys = pilotScopedBotRows
            .Select(entity => new MonitoringAccountScopeKey(entity.ExchangeAccountId, entity.OwnerUserId, ExchangeDataPlane.Futures))
            .ToHashSet();
        var pilotScopedOpenPositionAccountKeys = pilotScopedBotRows
            .Where(entity => openPositionKeys.Contains(new MonitoringPositionScopeKey(
                entity.ExchangeAccountId,
                entity.OwnerUserId,
                NormalizeSymbol(entity.Symbol))))
            .Select(entity => new MonitoringAccountScopeKey(entity.ExchangeAccountId, entity.OwnerUserId, ExchangeDataPlane.Futures))
            .ToHashSet();
        var enabledOpenPositionAccountKeys = enabledBotRows
            .Where(entity => openPositionKeys.Contains(new MonitoringPositionScopeKey(
                entity.ExchangeAccountId,
                entity.OwnerUserId,
                NormalizeSymbol(entity.Symbol))))
            .Select(entity => new MonitoringAccountScopeKey(entity.ExchangeAccountId, entity.OwnerUserId, ExchangeDataPlane.Futures))
            .ToHashSet();

        var selectedSyncRow = latestSyncRows
            .OrderByDescending(entity => ResolvePrivateSyncPriority(
                new MonitoringAccountScopeKey(entity.ExchangeAccountId, entity.OwnerUserId, entity.Plane),
                pilotScopedOpenPositionAccountKeys,
                pilotScopedAccountKeys,
                enabledOpenPositionAccountKeys,
                enabledBotScopeKeys))
            .ThenByDescending(entity => entity.UpdatedDate)
            .ThenByDescending(entity => entity.CreatedDate)
            .FirstOrDefault();
        var inactiveBlockedCredentialAccountCount = latestSyncRows.Count(entity =>
            string.Equals(entity.LastErrorCode, "CredentialAccessBlocked", StringComparison.OrdinalIgnoreCase) &&
            !enabledBotScopeKeys.Contains(new MonitoringAccountScopeKey(entity.ExchangeAccountId, entity.OwnerUserId, entity.Plane)));

        if (selectedSyncRow is null)
        {
            return OperationalPrivateSyncEvidenceSnapshot.Empty() with
            {
                Summary = $"No futures private sync evidence yet. PrivatePlaneStaleRejects={privatePlaneStaleRejectCount}.",
                PrivatePlaneStaleRejectCount = privatePlaneStaleRejectCount,
                InactiveBlockedCredentialAccountCount = inactiveBlockedCredentialAccountCount
            };
        }

        var parsedDriftSummary = ParseDriftSummary(selectedSyncRow.DriftSummary);
        var lastStateReconciledAtUtc = NormalizeUtcNullable(selectedSyncRow.LastStateReconciledAtUtc);
        var updatedDateUtc = NormalizeUtc(selectedSyncRow.UpdatedDate);
        var syncReferenceAtUtc = lastStateReconciledAtUtc ?? updatedDateUtc;
        var syncAgeSeconds = Math.Max(
            0,
            (int)Math.Round(
                (utcNow - syncReferenceAtUtc).TotalSeconds,
                MidpointRounding.AwayFromZero));
        var state = ResolvePrivateSyncEvidenceState(selectedSyncRow, syncAgeSeconds, options.PrivatePlaneFreshnessThresholdSeconds);
        var balanceMismatches = parsedDriftSummary.BalanceMismatches?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
        var positionMismatches = parsedDriftSummary.PositionMismatches?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
        var snapshotSource = parsedDriftSummary.SnapshotSource ?? "n/a";
        var summary = $"Stream={selectedSyncRow.PrivateStreamConnectionState}; Drift={selectedSyncRow.DriftStatus}; BalanceMismatches={balanceMismatches}; PositionMismatches={positionMismatches}; SnapshotSource={snapshotSource}; SyncAgeSec={syncAgeSeconds}; PrivatePlaneStaleRejects={privatePlaneStaleRejectCount}; InactiveBlockedCredentialAccounts={inactiveBlockedCredentialAccountCount}; LastErrorCode={selectedSyncRow.LastErrorCode ?? "n/a"}";

        return new OperationalPrivateSyncEvidenceSnapshot(
            State: state,
            Summary: summary,
            ExchangeAccountId: selectedSyncRow.ExchangeAccountId.ToString("D"),
            Plane: selectedSyncRow.Plane.ToString(),
            PrivateStreamConnectionState: selectedSyncRow.PrivateStreamConnectionState.ToString(),
            DriftStatus: selectedSyncRow.DriftStatus.ToString(),
            DriftSummary: string.IsNullOrWhiteSpace(selectedSyncRow.DriftSummary) ? "n/a" : selectedSyncRow.DriftSummary.Trim(),
            BalanceMismatches: parsedDriftSummary.BalanceMismatches,
            PositionMismatches: parsedDriftSummary.PositionMismatches,
            SnapshotSource: snapshotSource,
            LastDriftDetectedAtUtc: NormalizeUtcNullable(selectedSyncRow.LastDriftDetectedAtUtc),
            LastBalanceSyncedAtUtc: NormalizeUtcNullable(selectedSyncRow.LastBalanceSyncedAtUtc),
            LastPositionSyncedAtUtc: NormalizeUtcNullable(selectedSyncRow.LastPositionSyncedAtUtc),
            LastStateReconciledAtUtc: lastStateReconciledAtUtc,
            SyncAgeSeconds: syncAgeSeconds,
            ConsecutiveStreamFailureCount: selectedSyncRow.ConsecutiveStreamFailureCount,
            LastErrorCode: string.IsNullOrWhiteSpace(selectedSyncRow.LastErrorCode) ? null : selectedSyncRow.LastErrorCode.Trim(),
            UpdatedDate: updatedDateUtc,
            InactiveBlockedCredentialAccountCount: inactiveBlockedCredentialAccountCount,
            PrivatePlaneStaleRejectCount: privatePlaneStaleRejectCount);
    }

    private static bool MatchesPilotScope(
        MonitoringBotScopeRow entity,
        IReadOnlySet<string> allowedSymbols,
        IReadOnlySet<string> allowedBotIds,
        IReadOnlySet<string> allowedUserIds)
    {
        if (allowedSymbols.Count > 0 && !allowedSymbols.Contains(NormalizeSymbol(entity.Symbol)))
        {
            return false;
        }

        if (allowedBotIds.Count > 0 && !allowedBotIds.Contains(entity.Id.ToString("N")))
        {
            return false;
        }

        return allowedUserIds.Count == 0 || allowedUserIds.Contains(entity.OwnerUserId);
    }

    private static int ResolvePrivateSyncPriority(
        MonitoringAccountScopeKey accountScopeKey,
        IReadOnlySet<MonitoringAccountScopeKey> pilotScopedOpenPositionAccountKeys,
        IReadOnlySet<MonitoringAccountScopeKey> pilotScopedAccountKeys,
        IReadOnlySet<MonitoringAccountScopeKey> enabledOpenPositionAccountKeys,
        IReadOnlySet<MonitoringAccountScopeKey> enabledBotScopeKeys)
    {
        if (pilotScopedOpenPositionAccountKeys.Contains(accountScopeKey))
        {
            return 4;
        }

        if (pilotScopedAccountKeys.Contains(accountScopeKey))
        {
            return 3;
        }

        if (enabledOpenPositionAccountKeys.Contains(accountScopeKey))
        {
            return 2;
        }

        return enabledBotScopeKeys.Contains(accountScopeKey) ? 1 : 0;
    }

    private static string ResolvePrivateSyncEvidenceState(
        ExchangeAccountSyncState entity,
        int syncAgeSeconds,
        int freshnessThresholdSeconds)
    {
        if (entity.PrivateStreamConnectionState == ExchangePrivateStreamConnectionState.Connected &&
            entity.DriftStatus == ExchangeStateDriftStatus.InSync &&
            string.IsNullOrWhiteSpace(entity.LastErrorCode) &&
            syncAgeSeconds <= freshnessThresholdSeconds)
        {
            return "Healthy";
        }

        if (!string.IsNullOrWhiteSpace(entity.LastErrorCode) ||
            entity.ConsecutiveStreamFailureCount > 0 ||
            syncAgeSeconds > freshnessThresholdSeconds)
        {
            return "Warning";
        }

        return entity.PrivateStreamConnectionState is ExchangePrivateStreamConnectionState.Connecting or ExchangePrivateStreamConnectionState.Reconnecting ||
               entity.DriftStatus == ExchangeStateDriftStatus.DriftDetected
            ? "Warning"
            : "Watching";
    }

    private static DriftSummaryParts ParseDriftSummary(string? driftSummary)
    {
        if (string.IsNullOrWhiteSpace(driftSummary))
        {
            return DriftSummaryParts.Empty;
        }

        int? balanceMismatches = null;
        int? positionMismatches = null;
        string? snapshotSource = null;
        var segments = driftSummary.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex == segment.Length - 1)
            {
                continue;
            }

            var key = segment[..separatorIndex].Trim();
            var value = segment[(separatorIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (string.Equals(key, "BalanceMismatches", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBalanceMismatches))
            {
                balanceMismatches = parsedBalanceMismatches;
                continue;
            }

            if (string.Equals(key, "PositionMismatches", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPositionMismatches))
            {
                positionMismatches = parsedPositionMismatches;
                continue;
            }

            if (string.Equals(key, "SnapshotSource", StringComparison.OrdinalIgnoreCase))
            {
                snapshotSource = value;
            }
        }

        return new DriftSummaryParts(balanceMismatches, positionMismatches, snapshotSource);
    }

    private static string ResolveLogSystemState(UltraDebugLogHealthSnapshot ultraDebugLogHealth)
    {
        if (IsUnavailableLogHealth(ultraDebugLogHealth))
        {
            return "Unknown";
        }

        if (!ultraDebugLogHealth.IsWritable)
        {
            return "Degraded";
        }

        return ultraDebugLogHealth.DiskPressureState;
    }

    private static string ResolveObservableDiskPressureState(UltraDebugLogHealthSnapshot ultraDebugLogHealth)
    {
        return IsUnavailableLogHealth(ultraDebugLogHealth)
            ? "Unknown"
            : ultraDebugLogHealth.DiskPressureState;
    }

    private static string BuildJanitorSummary(UltraDebugLogHealthSnapshot ultraDebugLogHealth)
    {
        if (!ultraDebugLogHealth.LastRetentionCompletedAtUtc.HasValue)
        {
            return "No janitor heartbeat yet.";
        }

        var result = ultraDebugLogHealth.LastRetentionSucceeded switch
        {
            true => "success",
            false => "failed",
            _ => "n/a"
        };
        return $"Last janitor {ultraDebugLogHealth.LastRetentionCompletedAtUtc.Value.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC · {result} · {ultraDebugLogHealth.LastRetentionReasonCode ?? "n/a"}.";
    }

    private static string BuildExportSummary(UltraDebugLogHealthSnapshot ultraDebugLogHealth)
    {
        return ultraDebugLogHealth.IsExportAvailable
            ? "Masked log export available."
            : "Masked log export degraded.";
    }

    private static IReadOnlyCollection<OperationalWarningSnapshot> BuildCriticalWarnings(
        IReadOnlyCollection<HealthSnapshotEntity> healthSnapshotEntities,
        IReadOnlyCollection<WorkerHeartbeatEntity> workerHeartbeatEntities,
        MarketScannerDashboardSnapshot scannerSnapshot,
        UltraDebugLogHealthSnapshot ultraDebugLogHealth,
        string diskPressureState,
        OperationalExecutionReadinessSnapshot executionReadiness,
        int recentDecisionCount,
        decimal coveragePercent)
    {
        var warnings = new List<OperationalWarningSnapshot>();
        var criticalSentinelNames = healthSnapshotEntities
            .Where(entity => entity.HealthState == MonitoringHealthState.Critical)
            .Select(entity => entity.DisplayName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (criticalSentinelNames.Length > 0)
        {
            warnings.Add(new OperationalWarningSnapshot(
                Code: "CriticalHealth",
                Summary: $"{criticalSentinelNames.Length} critical health snapshot: {string.Join(", ", criticalSentinelNames)}.",
                Tone: "critical"));
        }

        var staleWorkerNames = workerHeartbeatEntities
            .Where(entity =>
                entity.FreshnessTier == MonitoringFreshnessTier.Stale ||
                entity.HealthState == MonitoringHealthState.Critical)
            .Select(entity => entity.WorkerName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (staleWorkerNames.Length > 0)
        {
            warnings.Add(new OperationalWarningSnapshot(
                Code: "WorkerHeartbeat",
                Summary: $"Worker heartbeat requires review: {string.Join(", ", staleWorkerNames)}.",
                Tone: "warning"));
        }

        if (string.Equals(executionReadiness.State, "Blocked", StringComparison.Ordinal))
        {
            warnings.Add(new OperationalWarningSnapshot(
                Code: "ExecutionBlocked",
                Summary: executionReadiness.Summary,
                Tone: "critical"));
        }
        else if (string.Equals(executionReadiness.State, "Watching", StringComparison.Ordinal))
        {
            warnings.Add(new OperationalWarningSnapshot(
                Code: "ExecutionWatching",
                Summary: executionReadiness.Summary,
                Tone: "warning"));
        }

        if (recentDecisionCount > 0 && coveragePercent < 100m)
        {
            warnings.Add(new OperationalWarningSnapshot(
                Code: "AiOutcomeCoverage",
                Summary: $"AI outcome coverage is {coveragePercent.ToString("0.##", CultureInfo.InvariantCulture)}% across {recentDecisionCount} recent shadow decisions.",
                Tone: coveragePercent == 0m ? "critical" : "warning"));
        }

        if (!string.Equals(diskPressureState, "Healthy", StringComparison.Ordinal) &&
            !string.Equals(diskPressureState, "Unknown", StringComparison.Ordinal))
        {
            warnings.Add(new OperationalWarningSnapshot(
                Code: "DiskPressure",
                Summary: $"Log system state {diskPressureState} · reason {ultraDebugLogHealth.LastEscalationReason ?? "n/a"}.",
                Tone: string.Equals(diskPressureState, "Critical", StringComparison.Ordinal) ? "critical" : "warning"));
        }

        if (ultraDebugLogHealth.LastRetentionSucceeded == false)
        {
            warnings.Add(new OperationalWarningSnapshot(
                Code: "JanitorFailed",
                Summary: $"Latest janitor run failed ({ultraDebugLogHealth.LastRetentionReasonCode ?? "n/a"}).",
                Tone: "warning"));
        }

        if (scannerSnapshot.LastBlockedHandoff.CompletedAtUtc.HasValue &&
            !string.Equals(scannerSnapshot.LastBlockedHandoff.ExecutionRequestStatus, "Prepared", StringComparison.Ordinal) &&
            !warnings.Any(item => string.Equals(item.Code, "ExecutionBlocked", StringComparison.Ordinal) || string.Equals(item.Code, "ExecutionWatching", StringComparison.Ordinal)))
        {
            warnings.Add(new OperationalWarningSnapshot(
                Code: "LatestBlockedHandoff",
                Summary: $"Latest scanner handoff blocked by {scannerSnapshot.LastBlockedHandoff.DecisionReasonCode ?? scannerSnapshot.LastBlockedHandoff.BlockerCode ?? "Unknown"}.",
                Tone: "warning"));
        }

        return warnings.Take(5).ToArray();
    }

    private static string ResolveOverallState(
        string systemHealthState,
        string executionReadinessState,
        string logSystemState,
        int recentDecisionCount,
        decimal coveragePercent)
    {
        var severity = Math.Max(
            Math.Max(ResolveNamedStateSeverity(systemHealthState), ResolveNamedStateSeverity(logSystemState)),
            ResolveExecutionReadinessSeverity(executionReadinessState));
        if (recentDecisionCount > 0 && coveragePercent == 0m)
        {
            severity = Math.Max(severity, 4);
        }
        else if (recentDecisionCount > 0 && coveragePercent < 100m)
        {
            severity = Math.Max(severity, 2);
        }

        return severity switch
        {
            >= 4 => "Critical",
            3 => "Degraded",
            2 => "Warning",
            1 => "Healthy",
            _ => "Unknown"
        };
    }

    private static string BuildLatestHandoffSummary(MarketScannerHandoffSnapshot handoff)
    {
        if (!handoff.CompletedAtUtc.HasValue)
        {
            return "No handoff attempt yet.";
        }

        var handoffSummary = handoff.BlockerSummary
            ?? handoff.DecisionSummary
            ?? handoff.SelectionReason;
        var prefix = $"{handoff.ExecutionRequestStatus} · {(handoff.SelectedSymbol ?? "n/a")}";
        return $"{prefix} · {TrimSummary(handoffSummary, 144)}";
    }

    private static string TrimSummary(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "n/a";
        }

        var normalized = value.Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..(maxLength - 1)].TrimEnd() + "…";
    }

    private static ExitPnlEvidenceRow? ParseExitPnlEvidence(
        string symbol,
        string signalType,
        string? decisionReasonCode,
        string? decisionSummary,
        DateTime? decisionAtUtc,
        DateTime createdAtUtc)
    {
        var exitReason = ExecutionDecisionDiagnostics.ExtractToken("ExitReason", decisionSummary);
        if (string.IsNullOrWhiteSpace(exitReason))
        {
            return null;
        }

        return new ExitPnlEvidenceRow(
            Symbol: string.IsNullOrWhiteSpace(symbol) ? "n/a" : symbol.Trim(),
            SignalType: string.IsNullOrWhiteSpace(signalType) ? "n/a" : signalType.Trim(),
            DecisionReasonCode: string.IsNullOrWhiteSpace(decisionReasonCode) ? null : decisionReasonCode.Trim(),
            ExitReason: exitReason,
            EstimatedPnlQuote: TryParseDecimalToken("EstimatedPnlQuote", decisionSummary),
            EstimatedPnlPct: TryParseDecimalToken("EstimatedPnlPct", decisionSummary),
            CloseSide: ExecutionDecisionDiagnostics.ExtractToken("CloseSide", decisionSummary),
            ReduceOnly: TryParseBooleanToken("ReduceOnly", decisionSummary),
            ObservedAtUtc: NormalizeUtc(decisionAtUtc ?? createdAtUtc));
    }

    private static void EnqueueOpenEntryLot(
        IDictionary<StrategyProfitPairingKey, Queue<OpenEntryLot>> buckets,
        StrategyProfitPairingKey key,
        OpenEntryLot lot)
    {
        if (!buckets.TryGetValue(key, out var queue))
        {
            queue = new Queue<OpenEntryLot>();
            buckets[key] = queue;
        }

        queue.Enqueue(lot);
    }

    private static string ResolveEntrySource(DecisionTraceSummaryRow? trace)
    {
        var token = ExecutionDecisionDiagnostics.ExtractToken("EntrySource", trace?.DecisionSummary);
        return string.IsNullOrWhiteSpace(token) || string.Equals(token, "n/a", StringComparison.OrdinalIgnoreCase)
            ? "StrategyEntry"
            : token;
    }

    private static string ResolveExitSource(
        StrategyProfitOrderRow order,
        DecisionTraceSummaryRow? trace,
        IReadOnlyDictionary<Guid, StrategySignalType> signalTypeById)
    {
        if (string.Equals(order.StrategyKey, "__admin_manual_close__", StringComparison.Ordinal))
        {
            return "Manual";
        }

        var token = ExecutionDecisionDiagnostics.ExtractToken("ExitSource", trace?.DecisionSummary);
        if (!string.IsNullOrWhiteSpace(token) &&
            !string.Equals(token, "n/a", StringComparison.OrdinalIgnoreCase))
        {
            return token;
        }

        if (order.SignalType == StrategySignalType.Exit &&
            order.ReduceOnly &&
            signalTypeById.TryGetValue(order.StrategySignalId, out var sourceSignalType) &&
            sourceSignalType == StrategySignalType.Entry)
        {
            return "ReverseSignal";
        }

        return trace?.DecisionReasonCode switch
        {
            "TakeProfitTriggered" => "TakeProfit",
            "StopLossTriggered" => "StopLoss",
            "TrailingStopTriggered" => "TrailingTakeProfit",
            "BreakEvenTriggered" => "RiskExit",
            _ => order.ReduceOnly ? "UnknownCloseOnly" : "n/a"
        };
    }

    private static decimal? ResolveEffectiveOrderPrice(StrategyProfitOrderRow order)
    {
        if (order.AverageFillPrice.HasValue && order.AverageFillPrice.Value > 0m)
        {
            return order.AverageFillPrice.Value;
        }

        return order.Price > 0m ? order.Price : null;
    }

    private static decimal ResolveEffectiveOrderQuantity(StrategyProfitOrderRow order)
    {
        return order.FilledQuantity > 0m
            ? order.FilledQuantity
            : (order.Quantity > 0m ? order.Quantity : 0m);
    }

    private static decimal CalculateNetPnlQuote(
        ExecutionOrderSide entrySide,
        decimal entryPrice,
        decimal exitPrice,
        decimal quantity)
    {
        if (entrySide == ExecutionOrderSide.Buy)
        {
            return Math.Round((exitPrice - entryPrice) * quantity, 8, MidpointRounding.AwayFromZero);
        }

        return Math.Round((entryPrice - exitPrice) * quantity, 8, MidpointRounding.AwayFromZero);
    }

    private static decimal CalculateNetPnlPct(
        ExecutionOrderSide entrySide,
        decimal entryPrice,
        decimal exitPrice)
    {
        if (entryPrice <= 0m)
        {
            return 0m;
        }

        var pnlPct = entrySide == ExecutionOrderSide.Buy
            ? ((exitPrice - entryPrice) / entryPrice) * 100m
            : ((entryPrice - exitPrice) / entryPrice) * 100m;

        return Math.Round(pnlPct, 8, MidpointRounding.AwayFromZero);
    }

    private static decimal? CalculateExcursionQuote(
        ExecutionOrderSide entrySide,
        decimal entryPrice,
        decimal quantity,
        decimal? referencePrice)
    {
        if (!referencePrice.HasValue || referencePrice.Value <= 0m || entryPrice <= 0m || quantity <= 0m)
        {
            return null;
        }

        var excursion = entrySide == ExecutionOrderSide.Buy
            ? (referencePrice.Value - entryPrice) * quantity
            : (entryPrice - referencePrice.Value) * quantity;
        excursion = Math.Round(excursion, 8, MidpointRounding.AwayFromZero);
        return excursion > 0m ? excursion : null;
    }

    private static decimal? ResolveAverageMetric(IEnumerable<decimal> values)
    {
        var materialized = values.ToArray();
        if (materialized.Length == 0)
        {
            return null;
        }

        return Math.Round(materialized.Average(), 8, MidpointRounding.AwayFromZero);
    }

    private static decimal? TryParseDecimalToken(string key, params string?[] sources)
    {
        var tokenValue = ExecutionDecisionDiagnostics.ExtractToken(key, sources);
        return decimal.TryParse(tokenValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedValue)
            ? parsedValue
            : null;
    }

    private static bool? TryParseBooleanToken(string key, params string?[] sources)
    {
        var tokenValue = ExecutionDecisionDiagnostics.ExtractToken(key, sources);
        return bool.TryParse(tokenValue, out var parsedValue)
            ? parsedValue
            : null;
    }

    private static int ResolveMonitoringHealthSeverity(MonitoringHealthState state)
    {
        return state switch
        {
            MonitoringHealthState.Critical => 4,
            MonitoringHealthState.Degraded => 3,
            MonitoringHealthState.Warning => 2,
            MonitoringHealthState.Healthy => 1,
            _ => 0
        };
    }

    private static int ResolveWorkerFreshnessSeverity(MonitoringFreshnessTier freshnessTier)
    {
        return freshnessTier switch
        {
            MonitoringFreshnessTier.Stale => 4,
            MonitoringFreshnessTier.Cold => 2,
            MonitoringFreshnessTier.Warm => 1,
            MonitoringFreshnessTier.Hot => 1,
            _ => 0
        };
    }

    private static int ResolveNamedStateSeverity(string? state)
    {
        return state switch
        {
            "Critical" => 4,
            "Blocked" => 4,
            "Degraded" => 3,
            "Disabled" => 3,
            "Warning" => 2,
            "Watching" => 2,
            "Healthy" => 1,
            "Ready" => 1,
            _ => 0
        };
    }

    private static int ResolveExecutionReadinessSeverity(string? state)
    {
        return state switch
        {
            "Blocked" => 4,
            "Watching" => 2,
            "Ready" => 1,
            _ => 0
        };
    }

    private static bool IsUnavailableLogHealth(UltraDebugLogHealthSnapshot ultraDebugLogHealth)
    {
        return !ultraDebugLogHealth.IsWritable &&
               !ultraDebugLogHealth.IsTailAvailable &&
               !ultraDebugLogHealth.IsExportAvailable &&
               !ultraDebugLogHealth.LastCheckedAtUtc.HasValue &&
               string.Equals(ultraDebugLogHealth.LastEscalationReason, "Unavailable", StringComparison.Ordinal);
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

    private static string NormalizeSymbol(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();
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

    private readonly record struct MonitoringAccountScopeKey(
        Guid ExchangeAccountId,
        string OwnerUserId,
        ExchangeDataPlane Plane);

    private readonly record struct MonitoringPositionScopeKey(
        Guid ExchangeAccountId,
        string OwnerUserId,
        string Symbol);

    private sealed record MonitoringBotScopeRow(
        Guid Id,
        string OwnerUserId,
        Guid ExchangeAccountId,
        string Symbol);

    private sealed record MonitoringOpenPositionRow(
        Guid ExchangeAccountId,
        string OwnerUserId,
        string Symbol);

    private sealed record DriftSummaryParts(
        int? BalanceMismatches,
        int? PositionMismatches,
        string? SnapshotSource)
    {
        public static DriftSummaryParts Empty { get; } = new(null, null, null);
    }

    private sealed record ExitPnlEvidenceRow(
        string Symbol,
        string SignalType,
        string? DecisionReasonCode,
        string ExitReason,
        decimal? EstimatedPnlQuote,
        decimal? EstimatedPnlPct,
        string? CloseSide,
        bool? ReduceOnly,
        DateTime ObservedAtUtc);

    private sealed record StrategyProfitOrderRow(
        Guid Id,
        Guid StrategySignalId,
        Guid? BotId,
        Guid? ExchangeAccountId,
        string OwnerUserId,
        string StrategyKey,
        string Symbol,
        StrategySignalType SignalType,
        ExecutionOrderSide Side,
        decimal Quantity,
        decimal FilledQuantity,
        decimal Price,
        decimal? AverageFillPrice,
        bool ReduceOnly,
        DateTime CreatedDate,
        DateTime? SubmittedAtUtc,
        DateTime LastStateChangedAtUtc);

    private sealed record DecisionTraceSummaryRow(
        string? DecisionReasonCode,
        string? DecisionSummary,
        DateTime ObservedAtUtc);

    private sealed class OpenEntryLot
    {
        public OpenEntryLot(
            string strategyKey,
            string symbol,
            ExecutionOrderSide entrySide,
            string entrySource,
            decimal? entryPrice,
            decimal remainingQuantity,
            bool usedEstimatedPricing,
            DateTime openedAtUtc)
        {
            StrategyKey = strategyKey;
            Symbol = symbol;
            EntrySide = entrySide;
            EntrySource = entrySource;
            EntryPrice = entryPrice;
            RemainingQuantity = remainingQuantity;
            UsedEstimatedPricing = usedEstimatedPricing;
            OpenedAtUtc = openedAtUtc;
        }

        public string StrategyKey { get; }

        public string Symbol { get; }

        public ExecutionOrderSide EntrySide { get; }

        public string EntrySource { get; }

        public decimal? EntryPrice { get; }

        public decimal RemainingQuantity { get; set; }

        public bool UsedEstimatedPricing { get; }

        public DateTime OpenedAtUtc { get; }
    }

    private readonly record struct StrategyProfitPairingKey(
        string OwnerUserId,
        Guid? BotId,
        Guid? ExchangeAccountId,
        string Symbol);

    private sealed record PairedTradeEvidenceRow(
        string StrategyKey,
        string Symbol,
        string EntrySource,
        string ExitSource,
        decimal MatchedQuantity,
        decimal NetPnlQuote,
        decimal NetPnlPct,
        decimal? MaxFavorableExcursionQuote,
        decimal? MaxAdverseExcursionQuote,
        DateTime ClosedAtUtc,
        bool UsesEstimatedPricing);
}
