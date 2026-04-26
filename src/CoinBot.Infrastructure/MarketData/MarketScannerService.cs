using System.Globalization;
using System.Text;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Ai;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Strategies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.MarketData;

public sealed class MarketScannerService(
    ApplicationDbContext dbContext,
    IMarketDataService marketDataService,
    ISharedSymbolRegistry sharedSymbolRegistry,
    IOptions<MarketScannerOptions> scannerOptions,
    IOptions<BinanceMarketDataOptions> marketDataOptions,
    TimeProvider timeProvider,
    ILogger<MarketScannerService> logger,
    MarketScannerHandoffService? handoffService = null,
    IIndicatorDataService? indicatorDataService = null,
    IStrategyEvaluatorService? strategyEvaluatorService = null,
    IOptions<BotExecutionPilotOptions>? botExecutionPilotOptions = null,
    IBinanceHistoricalKlineClient? historicalKlineClient = null,
    IUltraDebugLogService? ultraDebugLogService = null,
    IAiShadowDecisionService? aiShadowDecisionService = null)
{
    internal const string WorkerKey = "market-scanner";
    internal const string WorkerName = "Market Scanner";
    private const int CandidateScoringSummaryMaxLength = 2048;
    private const int TrendBreakoutShadowScore = 55;
    private const int CompressionBreakoutSetupShadowScore = 25;
    private const decimal MaxSqlClientDecimal38Scale18Value = 79_228_162_514.264337593543950335m;

    private readonly BotExecutionPilotOptions botExecutionOptionsValue = botExecutionPilotOptions?.Value ?? new();
    private readonly MarketScannerOptions scannerOptionsValue = scannerOptions.Value;
    private readonly string klineInterval = string.IsNullOrWhiteSpace(marketDataOptions.Value.KlineInterval)
        ? "1m"
        : marketDataOptions.Value.KlineInterval.Trim();
    private readonly string[] configuredSeedSymbols = marketDataOptions.Value.SeedSymbols ?? [];
    private readonly string[] allowedQuoteAssets = (scannerOptions.Value.AllowedQuoteAssets ?? [])
        .Select(item => item?.Trim().ToUpperInvariant())
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Distinct(StringComparer.Ordinal)
        .OrderByDescending(item => item.Length)
        .ThenBy(item => item, StringComparer.Ordinal)
        .ToArray();
    private readonly ExecutionEnvironment signalEvaluationMode =
        (botExecutionPilotOptions?.Value ?? new BotExecutionPilotOptions()).SignalEvaluationMode;
    private readonly Dictionary<string, AiRankingOutcomeCoverageSummary> aiRankingOutcomeCoverageCache = new(StringComparer.Ordinal);

    public async Task<MarketScannerCycle> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        aiRankingOutcomeCoverageCache.Clear();
        await ArchiveLegacyDirtyCandidatesAsync(cancellationToken);

        var startedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var universe = await ResolveUniverseAsync(startedAtUtc, cancellationToken);
        await AuditPoisonedHistoricalCandlesAsync(universe, startedAtUtc, cancellationToken);
        var cycle = new MarketScannerCycle
        {
            Id = Guid.NewGuid(),
            StartedAtUtc = startedAtUtc,
            UniverseSource = ResolveUniverseSummary(universe),
            Summary = "Market scanner cycle started."
        };

        dbContext.MarketScannerCycles.Add(cycle);

        var candidates = new List<MarketScannerCandidate>(universe.Count);
        foreach (var candidate in universe)
        {
            candidates.Add(await EvaluateCandidateAsync(cycle.Id, candidate, cancellationToken));
        }

        ApplyAdaptiveFilteringSuppressionGuardrail(candidates);
        ApplyRanking(candidates);

        cycle.CompletedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        cycle.ScannedSymbolCount = candidates.Count;
        cycle.EligibleCandidateCount = candidates.Count(item => item.IsEligible);
        cycle.TopCandidateCount = candidates.Count(item => item.IsTopCandidate);
        cycle.BestCandidateSymbol = candidates
            .Where(item => item.IsTopCandidate)
            .OrderBy(item => item.Rank ?? int.MaxValue)
            .ThenBy(item => item.Symbol, StringComparer.Ordinal)
            .Select(item => item.Symbol)
            .FirstOrDefault();
        cycle.BestCandidateScore = candidates
            .Where(item => item.IsTopCandidate)
            .OrderBy(item => item.Rank ?? int.MaxValue)
            .ThenBy(item => item.Symbol, StringComparer.Ordinal)
            .Select(item => (decimal?)item.Score)
            .FirstOrDefault();
        var freshnessPause = ResolveFreshnessPause(candidates);
        cycle.Summary = freshnessPause is null
            ? BuildCycleSummary(cycle, candidates)
            : freshnessPause.Summary;

        if (freshnessPause is not null)
        {
            logger.LogWarning(
                "Market scanner entered freshness pause. ScanCycleId={ScanCycleId} Summary={Summary}",
                cycle.Id,
                freshnessPause.Summary);
        }

        IReadOnlyCollection<MarketScannerCandidate> persistedCandidates = freshnessPause is null
            ? candidates
            : Array.Empty<MarketScannerCandidate>();

        NormalizeScannerPersistenceNumerics(cycle, persistedCandidates);
        ValidateNumericEnvelope(cycle, persistedCandidates);

        if (persistedCandidates.Count > 0)
        {
            dbContext.MarketScannerCandidates.AddRange(persistedCandidates);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (handoffService is not null && freshnessPause is null)
        {
            await handoffService.RunOnceAsync(cycle.Id, cancellationToken);
        }

        await UpsertWorkerHeartbeatAsync(cycle, candidates, freshnessPause, cancellationToken);
        await SaveWorkerHeartbeatAsync(cycle, candidates, freshnessPause, cancellationToken);

        logger.LogInformation(
            "Market scanner cycle completed. ScanCycleId={ScanCycleId} Scanned={ScannedSymbolCount} Eligible={EligibleCandidateCount} TopCandidates={TopCandidateCount} BestCandidate={BestCandidateSymbol}.",
            cycle.Id,
            cycle.ScannedSymbolCount,
            cycle.EligibleCandidateCount,
            cycle.TopCandidateCount,
            cycle.BestCandidateSymbol ?? "n/a");

        if (ultraDebugLogService is not null)
        {
            var cycleDurationMilliseconds = Math.Max(
                0,
                (int)Math.Round(
                    (cycle.CompletedAtUtc - startedAtUtc).TotalMilliseconds,
                    MidpointRounding.AwayFromZero));
            var latestCandleAtUtc = candidates
                .Where(item => item.LastCandleAtUtc.HasValue)
                .OrderByDescending(item => item.LastCandleAtUtc)
                .Select(item => item.LastCandleAtUtc)
                .FirstOrDefault();
            var rejectionSummary = candidates
                .Where(item => !item.IsEligible && !string.IsNullOrWhiteSpace(item.RejectionReason))
                .GroupBy(item => item.RejectionReason!, StringComparer.Ordinal)
                .Select(group => new
                {
                    reason = group.Key,
                    count = group.Count(),
                    symbols = group.Select(item => item.Symbol).Take(3).ToArray()
                })
                .OrderByDescending(item => item.count)
                .ThenBy(item => item.reason, StringComparer.Ordinal)
                .Take(5)
                .ToArray();

            await ultraDebugLogService.WriteAsync(
                new UltraDebugLogEntry(
                    Category: "scanner",
                    EventName: freshnessPause is null ? "scanner_cycle_completed" : "scanner_cycle_freshness_pause",
                    Summary: freshnessPause is null
                        ? $"Scanner cycle persisted {cycle.ScannedSymbolCount} symbols and {cycle.EligibleCandidateCount} eligible candidates."
                        : freshnessPause.Summary,
                    CorrelationId: cycle.Id.ToString("N"),
                    Detail: new
                    {
                        category = "scanner",
                        sourceLayer = nameof(MarketScannerService),
                        timeframe = klineInterval,
                        decisionOutcome = freshnessPause is null ? "Persisted" : "FreshnessPause",
                        decisionReasonType = freshnessPause is null ? null : "FreshnessGuard",
                        decisionReasonCode = freshnessPause is null ? null : "StaleMarketData",
                        candidateCount = cycle.ScannedSymbolCount,
                        scanCycleId = cycle.Id,
                        scannedSymbolCount = cycle.ScannedSymbolCount,
                        eligibleCandidateCount = cycle.EligibleCandidateCount,
                        topCandidateCount = cycle.TopCandidateCount,
                        bestCandidateSymbol = cycle.BestCandidateSymbol,
                        bestCandidateScore = cycle.BestCandidateScore,
                        selectedSymbol = cycle.BestCandidateSymbol,
                        environment = signalEvaluationMode.ToString(),
                        plane = ExchangeDataPlane.Futures.ToString(),
                        lastCandleAtUtc = latestCandleAtUtc,
                        dataAgeMs = latestCandleAtUtc.HasValue
                            ? (long?)Math.Max(
                                0,
                                (long)Math.Round(
                                    (cycle.CompletedAtUtc - latestCandleAtUtc.Value).TotalMilliseconds,
                                    MidpointRounding.AwayFromZero))
                            : null,
                        latencyBreakdown = new
                        {
                            totalMs = cycleDurationMilliseconds,
                            scannerMs = cycleDurationMilliseconds,
                            strategyMs = (int?)null,
                            handoffMs = (int?)null,
                            executionMs = (int?)null,
                            exchangeMs = (int?)null,
                            persistMs = (int?)null
                        },
                        cycleDurationMilliseconds,
                        cycleSummary = cycle.Summary,
                        rejectionSummary
                    }),
                cancellationToken);
        }

        return cycle;
    }

    private async Task ArchiveLegacyDirtyCandidatesAsync(CancellationToken cancellationToken)
    {
        var dirtyCandidates = await dbContext.MarketScannerCandidates
            .IgnoreQueryFilters()
            .Where(entity =>
                !entity.IsDeleted &&
                (entity.MarketScore > 100m ||
                 (entity.QuoteVolume24h.HasValue &&
                  entity.QuoteVolume24h.Value > 100m &&
                  entity.MarketScore == entity.QuoteVolume24h.Value)))
            .OrderBy(entity => entity.CreatedDate)
            .ThenBy(entity => entity.Symbol)
            .ToListAsync(cancellationToken);

        if (dirtyCandidates.Count == 0)
        {
            return;
        }

        var affectedCycleIds = dirtyCandidates
            .Select(entity => entity.ScanCycleId)
            .Distinct()
            .ToArray();

        foreach (var dirtyCandidate in dirtyCandidates)
        {
            dirtyCandidate.IsDeleted = true;
            dirtyCandidate.IsTopCandidate = false;
            dirtyCandidate.Rank = null;
            dirtyCandidate.RejectionReason = MarketScannerCandidateIntegrityGuard.LegacyArchivedDirtyMarketScoreReason;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var affectedCycleId in affectedCycleIds)
        {
            await RebuildArchivedCycleAsync(affectedCycleId, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Archived {ArchivedCandidateCount} legacy market scanner candidate rows with dirty MarketScore values across {AffectedCycleCount} cycles.",
            dirtyCandidates.Count,
            affectedCycleIds.Length);
    }

    private async Task RebuildArchivedCycleAsync(Guid scanCycleId, CancellationToken cancellationToken)
    {
        var cycle = await dbContext.MarketScannerCycles
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(entity => entity.Id == scanCycleId && !entity.IsDeleted, cancellationToken);

        if (cycle is null)
        {
            return;
        }

        var activeCandidates = await dbContext.MarketScannerCandidates
            .IgnoreQueryFilters()
            .Where(entity => entity.ScanCycleId == scanCycleId && !entity.IsDeleted)
            .OrderBy(entity => entity.Rank ?? int.MaxValue)
            .ThenBy(entity => entity.Symbol)
            .ToListAsync(cancellationToken);

        cycle.ScannedSymbolCount = activeCandidates.Count;
        cycle.EligibleCandidateCount = activeCandidates.Count(item => item.IsEligible);
        cycle.TopCandidateCount = activeCandidates.Count(item => item.IsTopCandidate);

        var bestCandidate = activeCandidates
            .Where(item => item.IsTopCandidate)
            .OrderBy(item => item.Rank ?? int.MaxValue)
            .ThenBy(item => item.Symbol, StringComparer.Ordinal)
            .FirstOrDefault();

        cycle.BestCandidateSymbol = bestCandidate?.Symbol;
        cycle.BestCandidateScore = bestCandidate?.Score is decimal bestScore
            ? NormalizePersistableDecimal38Scale18(bestScore)
            : null;
        cycle.Summary = BuildCycleSummary(cycle, activeCandidates);
    }

    private async Task<IReadOnlyCollection<UniverseSymbolCandidate>> ResolveUniverseAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var universe = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var trackedSymbols = await sharedSymbolRegistry.ListSymbolsAsync(cancellationToken);
        AddSymbols(universe, trackedSymbols.Select(item => item.Symbol), "registry");
        AddSymbols(universe, configuredSeedSymbols, "config");

        var enabledBotSymbols = await dbContext.TradingBots
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => entity.IsEnabled && !entity.IsDeleted && entity.Symbol != null)
            .Select(entity => entity.Symbol!)
            .ToListAsync(cancellationToken);
        AddSymbols(universe, enabledBotSymbols, "enabled-bot");

        var recentWindowStartUtc = nowUtc.AddHours(-scannerOptionsValue.VolumeLookbackHours);
        var recentWindowCandles = await LoadHistoricalMarketWindowCandlesAsync(
            recentWindowStartUtc,
            nowUtc,
            activeOnly: true,
            cancellationToken);
        var recentCandleSymbols = recentWindowCandles
            .Select(entity => entity.Symbol)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        AddSymbols(universe, recentCandleSymbols, "historical-candles");

        var allowedPilotSymbols = botExecutionOptionsValue.PilotActivationEnabled
            ? botExecutionOptionsValue.ResolveNormalizedAllowedSymbols()
            : Array.Empty<string>();

        if (allowedPilotSymbols.Length > 0)
        {
            var allowedSymbolSet = new HashSet<string>(allowedPilotSymbols, StringComparer.Ordinal);
            universe = universe
                .Where(item => allowedSymbolSet.Contains(item.Key))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        }

        return universe
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Take(scannerOptionsValue.MaxUniverseSymbols)
            .Select(item => new UniverseSymbolCandidate(item.Key, string.Join('+', item.Value)))
            .ToArray();
    }


    private async Task<MarketScannerCandidate> EvaluateCandidateAsync(
        Guid scanCycleId,
        UniverseSymbolCandidate universeCandidate,
        CancellationToken cancellationToken)
    {
        var observedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var metadata = await sharedSymbolRegistry.GetSymbolAsync(universeCandidate.Symbol, cancellationToken);
        var latestPriceRead = await marketDataService.ReadLatestPriceAsync(universeCandidate.Symbol, cancellationToken);
        var latestPriceSnapshot = latestPriceRead.Entry?.Payload;
        var latestKlineRead = await marketDataService.ReadLatestKlineAsync(universeCandidate.Symbol, klineInterval, cancellationToken);
        var latestSharedKline = latestKlineRead.Entry?.Payload is { IsClosed: true } payload
            ? payload
            : null;
        var windowStartUtc = observedAtUtc.AddHours(-scannerOptionsValue.VolumeLookbackHours);
        var marketWindow = await ResolveMarketWindowAsync(
            universeCandidate.Symbol,
            latestSharedKline,
            windowStartUtc,
            observedAtUtc,
            cancellationToken);
        var latestCandle = marketWindow.LatestCandle;
        var quoteAsset = ResolveQuoteAsset(universeCandidate.Symbol, metadata);
        var latestPrice = ResolveLatestPrice(latestPriceSnapshot, latestCandle);
        var quoteVolume = marketWindow.QuoteVolume24h;
        var marketScore = ResolveMarketScore(quoteVolume);
        var freshness = EvaluateFreshness(
            latestPriceRead.Status,
            latestKlineRead,
            latestCandle,
            observedAtUtc);
        var rejectionReason = ResolveRejectionReason(
            metadata,
            quoteAsset,
            latestPriceRead.Status,
            latestKlineRead.Status,
            latestPrice,
            quoteVolume,
            latestCandle,
            freshness,
            marketWindow);
        var strategyScoring = rejectionReason is null
            ? await ResolveStrategyScoringAsync(
                universeCandidate.Symbol,
                marketWindow.Candles,
                observedAtUtc,
                scannerOptionsValue.HandoffEnabled
                    ? await ResolveStrategyBindingAsync(universeCandidate.Symbol, cancellationToken)
                    : null,
                cancellationToken)
            : MarketScannerStrategyScoreSummary.MarketRejected(
                rejectionReason,
                BuildMarketRejectionSummary(rejectionReason, marketScore, quoteVolume, freshness, marketWindow));
        var candidateIntelligence = rejectionReason is null
            ? await ResolveCandidateIntelligenceAsync(
                universeCandidate.Symbol,
                marketWindow.Candles,
                observedAtUtc,
                latestPrice,
                cancellationToken)
            : ScannerCandidateIntelligenceSummary.Empty;
        var aiRankingOutcomeCoverage = rejectionReason is null
            ? await ResolveAiRankingOutcomeCoverageAsync(strategyScoring.StrategyBinding, cancellationToken)
            : AiRankingOutcomeCoverageSummary.Unavailable();
        rejectionReason ??= strategyScoring.RejectionReason;
        var rankingSummary = ResolveRankingSummary(
            rejectionReason,
            marketScore,
            strategyScoring.StrategyScore,
            candidateIntelligence,
            aiRankingOutcomeCoverage);
        rejectionReason = rankingSummary.RejectionReason;
        var isEligible = rejectionReason is null;

        return new MarketScannerCandidate
        {
            Id = Guid.NewGuid(),
            ScanCycleId = scanCycleId,
            Symbol = universeCandidate.Symbol,
            UniverseSource = universeCandidate.UniverseSource,
            ObservedAtUtc = observedAtUtc,
            LastCandleAtUtc = latestCandle?.CloseTimeUtc,
            LastPrice = latestPrice,
            QuoteVolume24h = quoteVolume,
            MarketScore = marketScore,
            StrategyScore = strategyScoring.StrategyScore,
            ScoringSummary = BuildCandidateScoringSummary(
                strategyScoring.ScoringSummary,
                marketWindow,
                candidateIntelligence,
                rankingSummary),
            IsEligible = isEligible,
            RejectionReason = rejectionReason,
            Score = isEligible ? rankingSummary.EffectiveScore : 0m,
            Rank = null,
            IsTopCandidate = false
        };
    }

    private void ApplyRanking(List<MarketScannerCandidate> candidates)
    {
        var eligibleCandidates = candidates
            .Where(item => item.IsEligible)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Symbol, StringComparer.Ordinal)
            .ToArray();

        for (var index = 0; index < eligibleCandidates.Length; index++)
        {
            eligibleCandidates[index].Rank = index + 1;
            eligibleCandidates[index].IsTopCandidate = index < scannerOptionsValue.TopCandidateCount;
        }
    }

    private async Task AuditPoisonedHistoricalCandlesAsync(
        IReadOnlyCollection<UniverseSymbolCandidate> universe,
        DateTime observedAtUtc,
        CancellationToken cancellationToken)
    {
        if (universe.Count == 0)
        {
            return;
        }

        var normalizedObservedAtUtc = NormalizeUtc(observedAtUtc);
        var auditWindowStartUtc = normalizedObservedAtUtc.AddHours(-scannerOptionsValue.VolumeLookbackHours);
        var universeSymbols = universe
            .Select(item => item.Symbol)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        IReadOnlyCollection<HistoricalMarketCandle> candles = (await LoadHistoricalMarketWindowCandlesAsync(
                auditWindowStartUtc,
                normalizedObservedAtUtc,
                activeOnly: true,
                cancellationToken))
            .Where(entity => universeSymbols.Contains(entity.Symbol))
            .OrderBy(entity => entity.Symbol)
            .ThenBy(entity => entity.CloseTimeUtc)
            .ToArray();

        if (candles.Count == 0)
        {
            return;
        }

        var findings = new List<PoisonedHistoricalCandleAuditFinding>();
        foreach (var candle in candles)
        {
            if (TryCreatePoisonedHistoricalCandleAuditFinding(candle, out var finding))
            {
                findings.Add(finding);
            }
        }

        if (findings.Count == 0)
        {
            return;
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var findingIds = findings
            .Select(item => item.Candle.Id)
            .Distinct()
            .ToArray();
        var trackedCandles = await dbContext.HistoricalMarketCandles
            .IgnoreQueryFilters()
            .Where(entity => findingIds.Contains(entity.Id))
            .ToListAsync(cancellationToken);

        foreach (var trackedCandle in trackedCandles)
        {
            trackedCandle.IsDeleted = true;
            trackedCandle.UpdatedDate = nowUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var detail = BuildPoisonedCandleAuditDetail(findings, auditWindowStartUtc, normalizedObservedAtUtc);
        logger.LogWarning(
            "Market scanner poisoned candle audit removed {PurgedCount} candles. Detail={Detail}",
            findings.Count,
            detail);

        throw new MarketScannerPoisonedCandleAuditException(
            findings.Count,
            auditWindowStartUtc,
            normalizedObservedAtUtc,
            detail);
    }

    private bool TryCreatePoisonedHistoricalCandleAuditFinding(
        HistoricalMarketCandle candle,
        out PoisonedHistoricalCandleAuditFinding finding)
    {
        if (candle.ClosePrice <= 0m)
        {
            finding = CreatePoisonedHistoricalCandleAuditFinding(candle, "ClosePrice", candle.ClosePrice.ToString(CultureInfo.InvariantCulture), "ClosePrice must be greater than zero.");
            return true;
        }

        if (candle.Volume < 0m)
        {
            finding = CreatePoisonedHistoricalCandleAuditFinding(candle, "Volume", candle.Volume.ToString(CultureInfo.InvariantCulture), "Volume cannot be negative.");
            return true;
        }

        if (!FitsDecimal38Scale18(candle.ClosePrice))
        {
            finding = CreatePoisonedHistoricalCandleAuditFinding(candle, "ClosePrice", candle.ClosePrice.ToString(CultureInfo.InvariantCulture), "ClosePrice does not fit decimal(38,18).");
            return true;
        }

        if (!FitsDecimal38Scale18(candle.Volume))
        {
            finding = CreatePoisonedHistoricalCandleAuditFinding(candle, "Volume", candle.Volume.ToString(CultureInfo.InvariantCulture), "Volume does not fit decimal(38,18).");
            return true;
        }

        finding = default;
        return false;
    }

    private static bool TryComputeCandleNotional(decimal closePrice, decimal volume, out decimal notional, out string text)
    {
        try
        {
            notional = closePrice * volume;
            text = notional.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        catch (OverflowException)
        {
            notional = 0m;
            text = "overflow";
            return false;
        }
    }

    private static PoisonedHistoricalCandleAuditFinding CreatePoisonedHistoricalCandleAuditFinding(
        HistoricalMarketCandle candle,
        string fieldName,
        string offendingValue,
        string reason)
    {
        return new PoisonedHistoricalCandleAuditFinding(
            candle,
            fieldName,
            offendingValue,
            reason,
            candle.ClosePrice.ToString(CultureInfo.InvariantCulture),
            candle.Volume.ToString(CultureInfo.InvariantCulture));
    }

    private static string BuildPoisonedCandleAuditDetail(
        IReadOnlyCollection<PoisonedHistoricalCandleAuditFinding> findings,
        DateTime auditWindowStartUtc,
        DateTime auditWindowEndUtc)
    {
        var examples = findings
            .Take(3)
            .Select(finding =>
                $"CandleId={finding.Candle.Id}|Symbol={finding.Candle.Symbol}|Field={finding.FieldName}|BadValue={finding.OffendingValue}|ClosePrice={finding.ClosePrice}|Volume={finding.Volume}|CloseTimeUtc={NormalizeUtc(finding.Candle.CloseTimeUtc):O}|Reason={finding.Reason}")
            .ToArray();

        return $"ErrorCode=ScannerPoisonedCandleAudit; PurgedCount={findings.Count}; AuditWindowStartUtc={NormalizeUtc(auditWindowStartUtc):O}; AuditWindowEndUtc={NormalizeUtc(auditWindowEndUtc):O}; Examples=[{string.Join(", ", examples)}]";
    }

    private readonly record struct PoisonedHistoricalCandleAuditFinding(
        HistoricalMarketCandle Candle,
        string FieldName,
        string OffendingValue,
        string Reason,
        string ClosePrice,
        string Volume);

    private static void NormalizeScannerPersistenceNumerics(
        MarketScannerCycle cycle,
        IReadOnlyCollection<MarketScannerCandidate> persistedCandidates)
    {
        if (cycle.BestCandidateScore is decimal bestCandidateScore)
        {
            cycle.BestCandidateScore = NormalizePersistableDecimal38Scale18(bestCandidateScore);
        }

        foreach (var candidate in persistedCandidates)
        {
            if (candidate.LastPrice is decimal lastPrice)
            {
                candidate.LastPrice = NormalizePersistableDecimal38Scale18(lastPrice);
            }

            if (candidate.QuoteVolume24h is decimal quoteVolume)
            {
                candidate.QuoteVolume24h = NormalizePersistableDecimal38Scale18(quoteVolume);
            }

            candidate.MarketScore = NormalizePersistableDecimal38Scale18(candidate.MarketScore);
            candidate.Score = NormalizePersistableDecimal38Scale18(candidate.Score);
        }
    }

    private void ValidateNumericEnvelope(
        MarketScannerCycle cycle,
        IReadOnlyCollection<MarketScannerCandidate> persistedCandidates)
    {
        if (cycle.BestCandidateScore is decimal bestCandidateScore)
        {
            ValidateNumericEnvelope(
                cycle.Id,
                cycle.BestCandidateSymbol ?? "n/a",
                nameof(MarketScannerCycle.BestCandidateScore),
                bestCandidateScore);
        }

        foreach (var candidate in persistedCandidates
                     .OrderBy(item => item.Rank ?? int.MaxValue)
                     .ThenBy(item => item.Symbol, StringComparer.Ordinal))
        {
            if (candidate.LastPrice is decimal lastPrice)
            {
                ValidateNumericEnvelope(candidate.ScanCycleId, candidate.Symbol, nameof(MarketScannerCandidate.LastPrice), lastPrice);
            }

            if (candidate.QuoteVolume24h is decimal quoteVolume)
            {
                ValidateNumericEnvelope(candidate.ScanCycleId, candidate.Symbol, nameof(MarketScannerCandidate.QuoteVolume24h), quoteVolume);
            }

            ValidateNumericEnvelope(candidate.ScanCycleId, candidate.Symbol, nameof(MarketScannerCandidate.MarketScore), candidate.MarketScore);
            ValidateNumericEnvelope(candidate.ScanCycleId, candidate.Symbol, nameof(MarketScannerCandidate.Score), candidate.Score);
        }
    }

    private void ValidateNumericEnvelope(Guid scanCycleId, string symbol, string fieldName, decimal value)
    {
        if (FitsDecimal38Scale18(value))
        {
            return;
        }

        var normalizedSymbol = string.IsNullOrWhiteSpace(symbol)
            ? "n/a"
            : symbol.Trim().ToUpperInvariant();
        var detail = BuildNumericOverflowDetail(scanCycleId, normalizedSymbol, fieldName, value);

        logger.LogWarning(
            "Market scanner numeric envelope rejected persistence. ScanCycleId={ScanCycleId} Symbol={Symbol} Field={Field} Value={Value}",
            scanCycleId,
            normalizedSymbol,
            fieldName,
            value.ToString(CultureInfo.InvariantCulture));

        throw new MarketScannerNumericOverflowException(scanCycleId, normalizedSymbol, fieldName, value, detail);
    }

    private static bool FitsDecimal38Scale18(decimal value)
    {
        return GetDecimalScale(value) <= 18 &&
               GetIntegerDigitCount(value) <= 20;
    }

    private static int GetDecimalScale(decimal value)
    {
        return (decimal.GetBits(value)[3] >> 16) & 0x7F;
    }

    private static int GetIntegerDigitCount(decimal value)
    {
        var integerPart = decimal.Truncate(Math.Abs(value));

        if (integerPart == 0m)
        {
            return 1;
        }

        var digits = 0;
        while (integerPart >= 1m)
        {
            integerPart = decimal.Truncate(integerPart / 10m);
            digits++;
        }

        return digits;
    }

    private static string BuildNumericOverflowDetail(Guid scanCycleId, string symbol, string fieldName, decimal value)
    {
        return $"ErrorCode=ScannerNumericOverflow; ScanCycleId={scanCycleId}; Symbol={symbol}; Field={fieldName}; Value={value.ToString(CultureInfo.InvariantCulture)}; Precision=38; Scale=18; Guard=MarketScannerService.ValidateNumericEnvelope";
    }

    private async Task SaveWorkerHeartbeatAsync(
        MarketScannerCycle cycle,
        IReadOnlyCollection<MarketScannerCandidate> candidates,
        FreshnessPauseSummary? freshnessPause,
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsWorkerHeartbeatUniqueConstraintViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
            var entity = await dbContext.WorkerHeartbeats
                .SingleAsync(item => item.WorkerKey == WorkerKey, cancellationToken);
            ApplyWorkerHeartbeat(entity, cycle, candidates, freshnessPause, timeProvider.GetUtcNow().UtcDateTime);
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogWarning(
                "MarketScannerDuplicateHeartbeatSkipped WorkerKey={WorkerKey} ScanCycleId={ScanCycleId}",
                WorkerKey,
                cycle.Id);
        }
    }

    private async Task UpsertWorkerHeartbeatAsync(
        MarketScannerCycle cycle,
        IReadOnlyCollection<MarketScannerCandidate> candidates,
        FreshnessPauseSummary? freshnessPause,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
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

        ApplyWorkerHeartbeat(entity, cycle, candidates, freshnessPause, nowUtc);
    }

    private void ApplyWorkerHeartbeat(
        WorkerHeartbeat entity,
        MarketScannerCycle cycle,
        IReadOnlyCollection<MarketScannerCandidate> candidates,
        FreshnessPauseSummary? freshnessPause,
        DateTime nowUtc)
    {
        var noUniverse = cycle.ScannedSymbolCount == 0;
        var noEligible = cycle.ScannedSymbolCount > 0 && cycle.EligibleCandidateCount == 0;
        var rejectionSummary = noEligible
            ? BuildRejectionBreakdown(candidates, maxEntries: 4, maxSymbolsPerReason: 2)
            : null;
        var dominantRejectionReason = noEligible
            ? ResolveDominantRejectionReason(candidates)
            : null;

        entity.WorkerName = WorkerName;
        entity.HealthState = noUniverse || noEligible
            ? MonitoringHealthState.Warning
            : MonitoringHealthState.Healthy;
        entity.FreshnessTier = freshnessPause is null
            ? MonitoringFreshnessTier.Hot
            : MonitoringFreshnessTier.Stale;
        entity.CircuitBreakerState = freshnessPause is not null
            ? CircuitBreakerStateCode.Cooldown
            : noUniverse || noEligible
                ? CircuitBreakerStateCode.HalfOpen
                : CircuitBreakerStateCode.Closed;
        entity.LastHeartbeatAtUtc = nowUtc;
        entity.LastUpdatedAtUtc = nowUtc;
        entity.ConsecutiveFailureCount = 0;
        entity.LastErrorCode = freshnessPause is not null
            ? "ScannerFreshnessPaused"
            : noUniverse
                ? "NoUniverseSymbols"
                : noEligible
                    ? (dominantRejectionReason ?? "NoEligibleCandidates")
                    : null;
        entity.LastErrorMessage = freshnessPause is not null
            ? freshnessPause.Summary
            : noUniverse
                ? "Market scanner found no universe symbols."
                : noEligible
                    ? string.IsNullOrWhiteSpace(rejectionSummary)
                        ? "Market scanner did not produce eligible candidates."
                        : $"Market scanner did not produce eligible candidates. Reasons={rejectionSummary}."
                    : null;
        entity.SnapshotAgeSeconds = 0;
        entity.Detail = freshnessPause is not null
            ? Truncate(freshnessPause.Detail, 2048)
            : Truncate(
                BuildCycleDetail(
                    cycle,
                    candidates,
                    scannerOptionsValue.HandoffEnabled,
                    scannerOptionsValue.ExecutionHost,
                    klineInterval),
                2048);
    }

    private static bool IsWorkerHeartbeatUniqueConstraintViolation(DbUpdateException exception)
    {
        return TryGetSqlServerErrorNumber(exception.InnerException, out var sqlNumber) &&
               (sqlNumber is 2601 or 2627);
    }

    private static bool TryGetSqlServerErrorNumber(Exception? exception, out int sqlNumber)
    {
        sqlNumber = default;
        if (exception is null)
        {
            return false;
        }

        var numberProperty = exception.GetType().GetProperty("Number");
        if (numberProperty?.PropertyType != typeof(int))
        {
            return false;
        }

        if (numberProperty.GetValue(exception) is not int value)
        {
            return false;
        }

        sqlNumber = value;
        return exception.GetType().Name.Contains("SqlException", StringComparison.Ordinal);
    }

    private static void AddSymbols(IDictionary<string, SortedSet<string>> universe, IEnumerable<string> symbols, string source)
    {
        foreach (var symbol in symbols)
        {
            try
            {
                var normalizedSymbol = MarketDataSymbolNormalizer.Normalize(symbol);

                if (!universe.TryGetValue(normalizedSymbol, out var sources))
                {
                    sources = new SortedSet<string>(StringComparer.Ordinal) { source };
                    universe[normalizedSymbol] = sources;
                    continue;
                }

                sources.Add(source);
            }
            catch (ArgumentException)
            {
            }
        }
    }

    private string? ResolveQuoteAsset(string symbol, SymbolMetadataSnapshot? metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata?.QuoteAsset))
        {
            return metadata.QuoteAsset.Trim().ToUpperInvariant();
        }

        return allowedQuoteAssets.FirstOrDefault(item => symbol.EndsWith(item, StringComparison.Ordinal));
    }

    private static decimal? ResolveLatestPrice(MarketPriceSnapshot? latestPriceSnapshot, HistoricalMarketCandle? latestCandle)
    {
        return latestPriceSnapshot?.Price is > 0m
            ? latestPriceSnapshot.Price
            : latestCandle?.ClosePrice is > 0m
                ? latestCandle.ClosePrice
                : null;
    }

    private static HistoricalMarketCandle? ResolveLatestClosedCandle(
        MarketCandleSnapshot? latestSharedKline,
        HistoricalMarketCandle? latestHistoricalCandle)
    {
        if (latestSharedKline is null)
        {
            return latestHistoricalCandle;
        }

        if (latestHistoricalCandle is null ||
            NormalizeUtc(latestSharedKline.CloseTimeUtc) >= NormalizeUtc(latestHistoricalCandle.CloseTimeUtc))
        {
            return new HistoricalMarketCandle
            {
                Symbol = latestSharedKline.Symbol,
                Interval = latestSharedKline.Interval,
                OpenTimeUtc = NormalizeUtc(latestSharedKline.OpenTimeUtc),
                CloseTimeUtc = NormalizeUtc(latestSharedKline.CloseTimeUtc),
                OpenPrice = latestSharedKline.OpenPrice,
                HighPrice = latestSharedKline.HighPrice,
                LowPrice = latestSharedKline.LowPrice,
                ClosePrice = latestSharedKline.ClosePrice,
                Volume = latestSharedKline.Volume,
                ReceivedAtUtc = NormalizeUtc(latestSharedKline.ReceivedAtUtc),
                Source = latestSharedKline.Source
            };
        }

        return latestHistoricalCandle;
    }


    private async Task<ScannerMarketWindowSnapshot> ResolveMarketWindowAsync(
        string symbol,
        MarketCandleSnapshot? latestSharedKline,
        DateTime windowStartUtc,
        DateTime observedAtUtc,
        CancellationToken cancellationToken)
    {
        var normalizedSymbol = MarketDataSymbolNormalizer.Normalize(symbol);
        var normalizedWindowStartUtc = NormalizeUtc(windowStartUtc);
        var normalizedObservedAtUtc = NormalizeUtc(observedAtUtc);
        IReadOnlyCollection<HistoricalMarketCandle> candles = (await LoadHistoricalMarketWindowCandlesAsync(
                normalizedWindowStartUtc,
                normalizedObservedAtUtc,
                activeOnly: true,
                cancellationToken))
            .Where(entity => entity.Symbol == normalizedSymbol)
            .OrderByDescending(entity => entity.CloseTimeUtc)
            .ToArray();
        var reactivationApplied = false;

        if (candles.Count == 0)
        {
            var reactivatedCandles = await ReactivateRecoverableHistoricalWindowCandlesAsync(
                normalizedSymbol,
                latestSharedKline,
                normalizedWindowStartUtc,
                normalizedObservedAtUtc,
                cancellationToken);
            if (reactivatedCandles.Count > 0)
            {
                candles = reactivatedCandles
                    .OrderByDescending(entity => entity.CloseTimeUtc)
                    .ToArray();
                reactivationApplied = true;
            }
        }

        var latestHistoricalCandle = candles.FirstOrDefault();
        var hasInactiveHistoricalWindowRows = candles.Count == 0 && latestSharedKline is not null
            ? await BuildHistoricalMarketWindowQuery(normalizedWindowStartUtc, normalizedObservedAtUtc, activeOnly: false)
                .Where(entity => entity.Symbol == normalizedSymbol)
                .AnyAsync(
                    entity => entity.IsDeleted || entity.Source == null || string.IsNullOrWhiteSpace(entity.Source),
                    cancellationToken)
            : false;
        var hasDeletedHistoricalWindow = latestSharedKline is not null && (candles.Count == 0 || hasInactiveHistoricalWindowRows);
        var parityLag = ResolveHistoricalParityLagSeconds(latestSharedKline, latestHistoricalCandle);
        var requiresHistoricalRepair = candles.Count == 0 || parityLag.HasValue;
        var repairApplied = reactivationApplied;
        string? repairSource = reactivationApplied ? "HistoricalCandlesDb.Reactivated" : null;

        if (requiresHistoricalRepair && historicalKlineClient is not null)
        {
            var recovery = await TryRecoverHistoricalWindowAsync(
                symbol,
                candles,
                latestSharedKline,
                normalizedWindowStartUtc,
                normalizedObservedAtUtc,
                cancellationToken);
            candles = recovery.Candles;
            latestHistoricalCandle = candles.FirstOrDefault();
            parityLag = ResolveHistoricalParityLagSeconds(latestSharedKline, latestHistoricalCandle);
            var recoveryApplied = recovery.RecoveryApplied && !parityLag.HasValue && candles.Count > 0;
            repairApplied = repairApplied || recoveryApplied;
            repairSource = recoveryApplied ? recovery.RecoverySource : repairSource;
        }

        candles = candles
            .Where(IsActiveHistoricalCandle)
            .OrderByDescending(entity => entity.CloseTimeUtc)
            .ToArray();
        latestHistoricalCandle = candles.FirstOrDefault();

        var latestCandle = ResolveLatestClosedCandle(latestSharedKline, latestHistoricalCandle);
        var quoteVolume = ResolvePersistableQuoteVolume24h(candles);

        return new ScannerMarketWindowSnapshot(
            candles,
            latestCandle,
            latestHistoricalCandle?.CloseTimeUtc,
            quoteVolume,
            hasDeletedHistoricalWindow,
            parityLag.HasValue,
            parityLag,
            repairApplied,
            repairSource);
    }

    private async Task<HistoricalWindowRecoveryResult> TryRecoverHistoricalWindowAsync(
        string symbol,
        IReadOnlyCollection<HistoricalMarketCandle> existingCandles,
        MarketCandleSnapshot? latestSharedKline,
        DateTime windowStartUtc,
        DateTime observedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var fetchedSnapshots = await LoadHistoricalRecoverySnapshotsAsync(
                symbol,
                windowStartUtc,
                observedAtUtc,
                cancellationToken);
            var mergedCandles = MergeHistoricalCandles(existingCandles, fetchedSnapshots, latestSharedKline, windowStartUtc, observedAtUtc);
            var insertedCount = await PersistRecoveredHistoricalCandlesAsync(symbol, existingCandles.Count, fetchedSnapshots, latestSharedKline, cancellationToken);
            var recoveryApplied = fetchedSnapshots.Count > 0 || insertedCount > 0;
            return new HistoricalWindowRecoveryResult(
                mergedCandles,
                recoveryApplied,
                recoveryApplied ? "Binance.Rest.KlineRecovery" : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Market scanner historical window recovery failed for {Symbol}.",
                symbol);

            var existingMergedCandles = MergeHistoricalCandles(existingCandles, Array.Empty<MarketCandleSnapshot>(), latestSharedKline, windowStartUtc, observedAtUtc);
            return new HistoricalWindowRecoveryResult(existingMergedCandles, false, null);
        }
    }

    private async Task<IReadOnlyCollection<MarketCandleSnapshot>> LoadHistoricalRecoverySnapshotsAsync(
        string symbol,
        DateTime windowStartUtc,
        DateTime observedAtUtc,
        CancellationToken cancellationToken)
    {
        if (historicalKlineClient is null)
        {
            return Array.Empty<MarketCandleSnapshot>();
        }

        var intervalStep = ParseIntervalStep(klineInterval);
        var startOpenTimeUtc = AlignToIntervalStart(windowStartUtc, intervalStep);
        var endOpenTimeUtc = AlignPreviousClosedOpenTime(observedAtUtc, intervalStep);

        if (endOpenTimeUtc < startOpenTimeUtc)
        {
            return Array.Empty<MarketCandleSnapshot>();
        }

        var snapshots = new List<MarketCandleSnapshot>();
        var nextOpenTimeUtc = startOpenTimeUtc;
        while (nextOpenTimeUtc <= endOpenTimeUtc)
        {
            var remainingCandles = CountCandlesInclusive(nextOpenTimeUtc, endOpenTimeUtc, intervalStep);
            var batchSize = Math.Min(1500, remainingCandles);
            var batchEndOpenTimeUtc = AddIntervalSteps(nextOpenTimeUtc, intervalStep, batchSize - 1);
            if (batchEndOpenTimeUtc > endOpenTimeUtc)
            {
                batchEndOpenTimeUtc = endOpenTimeUtc;
            }

            var batchSnapshots = await historicalKlineClient.GetClosedCandlesAsync(
                symbol,
                klineInterval,
                nextOpenTimeUtc,
                batchEndOpenTimeUtc,
                batchSize,
                cancellationToken);
            if (batchSnapshots.Count == 0)
            {
                break;
            }

            snapshots.AddRange(batchSnapshots.Where(item => item.IsClosed));
            var lastSnapshot = batchSnapshots
                .Where(item => item.IsClosed)
                .OrderBy(item => item.OpenTimeUtc)
                .LastOrDefault();
            if (lastSnapshot is null)
            {
                break;
            }

            nextOpenTimeUtc = AddIntervalSteps(lastSnapshot.OpenTimeUtc, intervalStep, 1);
        }

        return snapshots
            .Where(item => item.IsClosed)
            .OrderByDescending(item => item.CloseTimeUtc)
            .ToArray();
    }

    private IQueryable<HistoricalMarketCandle> BuildHistoricalMarketWindowQuery(
        DateTime windowStartUtc,
        DateTime observedAtUtc,
        bool activeOnly = true)
    {
        var normalizedWindowStartUtc = NormalizeUtc(windowStartUtc);
        var normalizedObservedAtUtc = NormalizeUtc(observedAtUtc);
        var normalizedInterval = klineInterval.Trim();
        var query = dbContext.HistoricalMarketCandles
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.Interval != null &&
                entity.Interval.Trim() == normalizedInterval &&
                entity.OpenTimeUtc < entity.CloseTimeUtc &&
                entity.OpenTimeUtc < normalizedObservedAtUtc &&
                entity.CloseTimeUtc >= normalizedWindowStartUtc);

        if (!activeOnly)
        {
            return query;
        }

        return query.Where(entity =>
            !entity.IsDeleted &&
            entity.Source != null &&
            entity.Source.Trim() != string.Empty);
    }

    private IQueryable<HistoricalMarketCandle> BuildHistoricalCandlesByOpenTimesQuery(
        string symbol,
        IReadOnlyCollection<DateTime> openTimes)
    {
        var normalizedSymbol = MarketDataSymbolNormalizer.Normalize(symbol);
        var normalizedInterval = klineInterval.Trim();
        return dbContext.HistoricalMarketCandles
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.Symbol == normalizedSymbol &&
                entity.Interval != null &&
                entity.Interval.Trim() == normalizedInterval &&
                openTimes.Contains(entity.OpenTimeUtc));
    }

    private async Task<IReadOnlyCollection<HistoricalMarketCandle>> ReactivateRecoverableHistoricalWindowCandlesAsync(
        string symbol,
        MarketCandleSnapshot? latestSharedKline,
        DateTime windowStartUtc,
        DateTime observedAtUtc,
        CancellationToken cancellationToken)
    {
        var normalizedSymbol = MarketDataSymbolNormalizer.Normalize(symbol);
        var normalizedWindowStartUtc = NormalizeUtc(windowStartUtc);
        var normalizedObservedAtUtc = NormalizeUtc(observedAtUtc);
        var normalizedInterval = klineInterval.Trim();
        var windowCandles = await dbContext.HistoricalMarketCandles
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.IsDeleted &&
                entity.Symbol == normalizedSymbol &&
                entity.Interval != null &&
                entity.Interval.Trim() == normalizedInterval &&
                entity.OpenTimeUtc < entity.CloseTimeUtc &&
                entity.OpenTimeUtc < normalizedObservedAtUtc &&
                entity.CloseTimeUtc >= normalizedWindowStartUtc)
            .ToListAsync(cancellationToken);
        var recoverableCandles = windowCandles
            .Where(IsRecoverableHistoricalCandle)
            .OrderByDescending(entity => entity.CloseTimeUtc)
            .ToArray();

        if (recoverableCandles.Length == 0)
        {
            return Array.Empty<HistoricalMarketCandle>();
        }

        var normalizedSharedSnapshot = latestSharedKline is { IsClosed: true } sharedSnapshot
            ? NormalizeSnapshot(sharedSnapshot)
            : null;
        if (normalizedSharedSnapshot is not null && !IsPersistableHistoricalSnapshot(normalizedSharedSnapshot))
        {
            normalizedSharedSnapshot = null;
        }

        foreach (var candle in recoverableCandles)
        {
            if (normalizedSharedSnapshot is not null &&
                string.Equals(candle.Symbol, normalizedSharedSnapshot.Symbol, StringComparison.Ordinal) &&
                string.Equals(candle.Interval.Trim(), normalizedSharedSnapshot.Interval.Trim(), StringComparison.Ordinal) &&
                NormalizeUtc(candle.OpenTimeUtc) == normalizedSharedSnapshot.OpenTimeUtc)
            {
                ApplyHistoricalMarketCandleSnapshot(candle, normalizedSharedSnapshot);
                continue;
            }

            candle.IsDeleted = false;
        }

        logger.LogInformation(
            "Market scanner reactivated {ReactivatedCandleCount} recoverable historical candles for {Symbol} {Interval}.",
            recoverableCandles.Length,
            normalizedSymbol,
            normalizedInterval);

        return recoverableCandles;
    }
    private async Task<IReadOnlyCollection<HistoricalMarketCandle>> LoadHistoricalMarketWindowCandlesAsync(
        DateTime windowStartUtc,
        DateTime observedAtUtc,
        bool activeOnly,
        CancellationToken cancellationToken)
    {
        var rows = await BuildHistoricalMarketWindowQuery(windowStartUtc, observedAtUtc, activeOnly: false)
            .ToListAsync(cancellationToken);

        if (!activeOnly)
        {
            return rows;
        }

        return rows
            .Where(IsActiveHistoricalCandle)
            .ToArray();
    }

    private bool IsActiveHistoricalCandle(HistoricalMarketCandle entity)
    {
        return !entity.IsDeleted &&
               entity.Interval is not null &&
               string.Equals(entity.Interval.Trim(), klineInterval, StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(entity.Source) &&
               entity.OpenTimeUtc < entity.CloseTimeUtc;
    }

    private bool IsRecoverableHistoricalCandle(HistoricalMarketCandle entity)
    {
        return entity.IsDeleted &&
               entity.Interval is not null &&
               string.Equals(entity.Interval.Trim(), klineInterval, StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(entity.Source) &&
               entity.OpenTimeUtc < entity.CloseTimeUtc &&
               !TryCreatePoisonedHistoricalCandleAuditFinding(entity, out _);
    }

    private decimal? ResolvePersistableQuoteVolume24h(IReadOnlyCollection<HistoricalMarketCandle> candles)
    {
        if (candles.Count == 0)
        {
            return null;
        }

        var total = 0m;
        foreach (var candle in candles)
        {
            if (!TryComputeCandleNotional(candle.ClosePrice, candle.Volume, out var notional, out _))
            {
                return MaxSqlClientDecimal38Scale18Value;
            }

            var normalizedNotional = NormalizePersistableDecimal38Scale18(notional);
            if (MaxSqlClientDecimal38Scale18Value - total <= normalizedNotional)
            {
                return MaxSqlClientDecimal38Scale18Value;
            }

            total += normalizedNotional;
        }

        return NormalizePersistableDecimal38Scale18(total);
    }

    private static decimal NormalizePersistableDecimal38Scale18(decimal value)
    {
        if (value <= 0m)
        {
            return 0m;
        }

        if (value >= MaxSqlClientDecimal38Scale18Value || GetIntegerDigitCount(value) > 20)
        {
            return MaxSqlClientDecimal38Scale18Value;
        }

        var normalized = GetDecimalScale(value) > 18
            ? decimal.Round(value, 18, MidpointRounding.AwayFromZero)
            : value;

        return normalized >= MaxSqlClientDecimal38Scale18Value || GetIntegerDigitCount(normalized) > 20
            ? MaxSqlClientDecimal38Scale18Value
            : normalized;
    }

    private async Task<int> PersistRecoveredHistoricalCandlesAsync(
        string symbol,
        int existingHistoricalCandleCount,
        IReadOnlyCollection<MarketCandleSnapshot> fetchedSnapshots,
        MarketCandleSnapshot? latestSharedKline,
        CancellationToken cancellationToken)
    {
        var persistableSnapshots = new Dictionary<DateTime, MarketCandleSnapshot>(EqualityComparer<DateTime>.Default);
        foreach (var snapshot in fetchedSnapshots.Where(item => item.IsClosed).Select(NormalizeSnapshot).Where(IsPersistableHistoricalSnapshot))
        {
            persistableSnapshots[snapshot.OpenTimeUtc] = snapshot;
        }

        if (latestSharedKline is { IsClosed: true } && (existingHistoricalCandleCount > 0 || fetchedSnapshots.Count > 0))
        {
            var normalizedSharedSnapshot = NormalizeSnapshot(latestSharedKline);
            if (IsPersistableHistoricalSnapshot(normalizedSharedSnapshot))
            {
                persistableSnapshots[normalizedSharedSnapshot.OpenTimeUtc] = normalizedSharedSnapshot;
            }
        }

        if (persistableSnapshots.Count == 0)
        {
            return 0;
        }

        var openTimes = persistableSnapshots.Keys.ToArray();
        var existingCandles = await BuildHistoricalCandlesByOpenTimesQuery(symbol, openTimes)
            .ToListAsync(cancellationToken);
        var existingCandlesByOpenTime = existingCandles
            .GroupBy(entity => NormalizeUtc(entity.OpenTimeUtc))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(IsActiveHistoricalCandle)
                    .ThenBy(entity => entity.IsDeleted)
                    .ThenByDescending(entity => entity.UpdatedDate)
                    .First());
        var persistedCount = 0;

        foreach (var snapshot in persistableSnapshots.Values.OrderBy(item => item.OpenTimeUtc))
        {
            var normalizedOpenTimeUtc = NormalizeUtc(snapshot.OpenTimeUtc);
            if (existingCandlesByOpenTime.TryGetValue(normalizedOpenTimeUtc, out var existingCandle))
            {
                if (IsActiveHistoricalCandle(existingCandle))
                {
                    continue;
                }

                ApplyHistoricalMarketCandleSnapshot(existingCandle, snapshot);
                persistedCount++;
                continue;
            }

            var historicalCandle = MapHistoricalMarketCandle(snapshot);
            dbContext.HistoricalMarketCandles.Add(historicalCandle);
            existingCandlesByOpenTime[normalizedOpenTimeUtc] = historicalCandle;
            persistedCount++;
        }

        return persistedCount;
    }

    private static IReadOnlyList<HistoricalMarketCandle> MergeHistoricalCandles(
        IReadOnlyCollection<HistoricalMarketCandle> existingCandles,
        IReadOnlyCollection<MarketCandleSnapshot> fetchedSnapshots,
        MarketCandleSnapshot? latestSharedKline,
        DateTime windowStartUtc,
        DateTime observedAtUtc)
    {
        var merged = new Dictionary<DateTime, HistoricalMarketCandle>();

        foreach (var candle in existingCandles)
        {
            if (candle.IsDeleted)
            {
                continue;
            }

            merged[NormalizeUtc(candle.OpenTimeUtc)] = CloneHistoricalMarketCandle(candle);
        }

        foreach (var snapshot in fetchedSnapshots.Where(item => item.IsClosed))
        {
            var normalizedSnapshot = NormalizeSnapshot(snapshot);
            merged[normalizedSnapshot.OpenTimeUtc] = MapHistoricalMarketCandle(normalizedSnapshot);
        }

        if (latestSharedKline is { IsClosed: true } && (existingCandles.Count > 0 || fetchedSnapshots.Count > 0))
        {
            var normalizedSharedSnapshot = NormalizeSnapshot(latestSharedKline);
            merged[normalizedSharedSnapshot.OpenTimeUtc] = MapHistoricalMarketCandle(normalizedSharedSnapshot);
        }

        return merged.Values
            .Where(item =>
                NormalizeUtc(item.CloseTimeUtc) >= NormalizeUtc(windowStartUtc) &&
                NormalizeUtc(item.CloseTimeUtc) <= NormalizeUtc(observedAtUtc))
            .OrderByDescending(item => item.CloseTimeUtc)
            .ToArray();
    }

    private static HistoricalMarketCandle CloneHistoricalMarketCandle(HistoricalMarketCandle source)
    {
        return new HistoricalMarketCandle
        {
            Id = source.Id,
            Symbol = source.Symbol,
            Interval = source.Interval,
            OpenTimeUtc = NormalizeUtc(source.OpenTimeUtc),
            CloseTimeUtc = NormalizeUtc(source.CloseTimeUtc),
            OpenPrice = source.OpenPrice,
            HighPrice = source.HighPrice,
            LowPrice = source.LowPrice,
            ClosePrice = source.ClosePrice,
            Volume = source.Volume,
            ReceivedAtUtc = NormalizeUtc(source.ReceivedAtUtc),
            Source = source.Source,
            CreatedDate = source.CreatedDate,
            UpdatedDate = source.UpdatedDate,
            IsDeleted = source.IsDeleted
        };
    }

    private static HistoricalMarketCandle MapHistoricalMarketCandle(MarketCandleSnapshot snapshot)
    {
        return new HistoricalMarketCandle
        {
            Id = Guid.NewGuid(),
            Symbol = snapshot.Symbol,
            Interval = snapshot.Interval,
            OpenTimeUtc = NormalizeUtc(snapshot.OpenTimeUtc),
            CloseTimeUtc = NormalizeUtc(snapshot.CloseTimeUtc),
            OpenPrice = snapshot.OpenPrice,
            HighPrice = snapshot.HighPrice,
            LowPrice = snapshot.LowPrice,
            ClosePrice = snapshot.ClosePrice,
            Volume = snapshot.Volume,
            ReceivedAtUtc = NormalizeUtc(snapshot.ReceivedAtUtc),
            Source = snapshot.Source
        };
    }

    private static void ApplyHistoricalMarketCandleSnapshot(
        HistoricalMarketCandle entity,
        MarketCandleSnapshot snapshot)
    {
        var replacement = MapHistoricalMarketCandle(snapshot);
        entity.Symbol = replacement.Symbol;
        entity.Interval = replacement.Interval;
        entity.OpenTimeUtc = replacement.OpenTimeUtc;
        entity.CloseTimeUtc = replacement.CloseTimeUtc;
        entity.OpenPrice = replacement.OpenPrice;
        entity.HighPrice = replacement.HighPrice;
        entity.LowPrice = replacement.LowPrice;
        entity.ClosePrice = replacement.ClosePrice;
        entity.Volume = replacement.Volume;
        entity.ReceivedAtUtc = replacement.ReceivedAtUtc;
        entity.Source = replacement.Source;
        entity.IsDeleted = false;
    }

    private static MarketCandleSnapshot NormalizeSnapshot(MarketCandleSnapshot snapshot)
    {
        return new MarketCandleSnapshot(
            snapshot.Symbol,
            snapshot.Interval,
            NormalizeUtc(snapshot.OpenTimeUtc),
            NormalizeUtc(snapshot.CloseTimeUtc),
            snapshot.OpenPrice,
            snapshot.HighPrice,
            snapshot.LowPrice,
            snapshot.ClosePrice,
            snapshot.Volume,
            snapshot.IsClosed,
            NormalizeUtc(snapshot.ReceivedAtUtc),
            snapshot.Source);
    }

    private static bool IsPersistableHistoricalSnapshot(MarketCandleSnapshot snapshot)
    {
        return !string.IsNullOrWhiteSpace(snapshot.Symbol) &&
               !string.IsNullOrWhiteSpace(snapshot.Interval) &&
               !string.IsNullOrWhiteSpace(snapshot.Source) &&
               snapshot.OpenTimeUtc < snapshot.CloseTimeUtc &&
               snapshot.ClosePrice > 0m &&
               snapshot.Volume >= 0m;
    }

    private static int? ResolveHistoricalParityLagSeconds(
        MarketCandleSnapshot? latestSharedKline,
        HistoricalMarketCandle? latestHistoricalCandle)
    {
        if (latestSharedKline is null || latestHistoricalCandle is null)
        {
            return null;
        }

        var lag = NormalizeUtc(latestSharedKline.CloseTimeUtc) - NormalizeUtc(latestHistoricalCandle.CloseTimeUtc);
        if (lag <= TimeSpan.Zero)
        {
            return null;
        }

        var intervalStep = ParseIntervalStep(latestSharedKline.Interval);
        return lag >= intervalStep
            ? (int)Math.Round(lag.TotalSeconds, MidpointRounding.AwayFromZero)
            : null;
    }

    private static TimeSpan ParseIntervalStep(string interval)
    {
        var normalizedInterval = interval?.Trim() ?? throw new InvalidOperationException("Interval is required.");
        if (normalizedInterval.Length < 2)
        {
            throw new InvalidOperationException($"Unsupported candle interval '{interval}'.");
        }

        var magnitudeText = normalizedInterval[..^1];
        var unit = normalizedInterval[^1];
        if (!int.TryParse(magnitudeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var magnitude) || magnitude <= 0)
        {
            throw new InvalidOperationException($"Unsupported candle interval '{interval}'.");
        }

        return unit switch
        {
            'm' => TimeSpan.FromMinutes(magnitude),
            'h' => TimeSpan.FromHours(magnitude),
            'd' => TimeSpan.FromDays(magnitude),
            'w' => TimeSpan.FromDays(7d * magnitude),
            _ => throw new InvalidOperationException($"Unsupported candle interval '{interval}'.")
        };
    }

    private static DateTime AlignToIntervalStart(DateTime value, TimeSpan intervalStep)
    {
        var normalizedValue = NormalizeUtc(value);
        var ticks = normalizedValue.Ticks - (normalizedValue.Ticks % intervalStep.Ticks);
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private static DateTime AlignPreviousClosedOpenTime(DateTime observedAtUtc, TimeSpan intervalStep)
    {
        var alignedObservedAtUtc = AlignToIntervalStart(observedAtUtc, intervalStep);
        return alignedObservedAtUtc - intervalStep;
    }

    private static DateTime AddIntervalSteps(DateTime value, TimeSpan intervalStep, int stepCount)
    {
        return NormalizeUtc(value).AddTicks(intervalStep.Ticks * stepCount);
    }

    private static int CountCandlesInclusive(DateTime startOpenTimeUtc, DateTime endOpenTimeUtc, TimeSpan intervalStep)
    {
        var normalizedStart = NormalizeUtc(startOpenTimeUtc);
        var normalizedEnd = NormalizeUtc(endOpenTimeUtc);
        if (normalizedEnd < normalizedStart)
        {
            return 0;
        }

        var diffTicks = normalizedEnd.Ticks - normalizedStart.Ticks;
        return (int)(diffTicks / intervalStep.Ticks) + 1;
    }

    private ScannerFreshnessSnapshot EvaluateFreshness(
        SharedMarketDataCacheReadStatus latestPriceReadStatus,
        SharedMarketDataCacheReadResult<MarketCandleSnapshot> latestKlineRead,
        HistoricalMarketCandle? latestCandle,
        DateTime observedAtUtc)
    {
        var normalizedObservedAtUtc = NormalizeUtc(observedAtUtc);
        DateTime? normalizedLastCandleAtUtc = latestCandle is null
            ? null
            : NormalizeUtc(latestCandle.CloseTimeUtc);
        var dataAgeSeconds = normalizedLastCandleAtUtc.HasValue
            ? Math.Max(0, (int)Math.Round((normalizedObservedAtUtc - normalizedLastCandleAtUtc.Value).TotalSeconds, MidpointRounding.AwayFromZero))
            : (int?)null;
        var latestClosedSharedKline = latestKlineRead.Entry?.Payload is { IsClosed: true } payload
            ? payload
            : null;
        var freshnessSource = latestClosedSharedKline is not null &&
                              normalizedLastCandleAtUtc.HasValue &&
                              NormalizeUtc(latestClosedSharedKline.CloseTimeUtc) == normalizedLastCandleAtUtc.Value
            ? "SharedKlineCache"
            : latestCandle is null
                ? "Unavailable"
                : $"HistoricalCandlesDb:{latestCandle.Source}";
        var isStale = !normalizedLastCandleAtUtc.HasValue ||
                      dataAgeSeconds.GetValueOrDefault(scannerOptionsValue.MaxDataAgeSeconds + 1) > scannerOptionsValue.MaxDataAgeSeconds;
        var diagnosticCode = latestKlineRead.Status switch
        {
            SharedMarketDataCacheReadStatus.HitFresh when !isStale => "SharedKlineFresh",
            SharedMarketDataCacheReadStatus.HitFresh => "SharedKlineBehindScannerClock",
            SharedMarketDataCacheReadStatus.HitStale => "SharedKlineStale",
            SharedMarketDataCacheReadStatus.Miss when latestCandle is not null => "HistoricalCandleFallback",
            SharedMarketDataCacheReadStatus.Miss => "SharedKlineMissing",
            SharedMarketDataCacheReadStatus.ProviderUnavailable => "SharedKlineProviderUnavailable",
            SharedMarketDataCacheReadStatus.DeserializeFailed => "SharedKlineDeserializeFailed",
            SharedMarketDataCacheReadStatus.InvalidPayload => "SharedKlineInvalidPayload",
            _ => "Unknown"
        };

        return new ScannerFreshnessSnapshot(
            normalizedObservedAtUtc,
            normalizedLastCandleAtUtc,
            dataAgeSeconds,
            freshnessSource,
            diagnosticCode,
            latestPriceReadStatus,
            latestKlineRead.Status,
            latestKlineRead.ReasonCode,
            latestKlineRead.ReasonSummary,
            isStale);
    }

    private string? ResolveRejectionReason(
        SymbolMetadataSnapshot? metadata,
        string? quoteAsset,
        SharedMarketDataCacheReadStatus latestPriceReadStatus,
        SharedMarketDataCacheReadStatus latestKlineReadStatus,
        decimal? latestPrice,
        decimal? quoteVolume,
        HistoricalMarketCandle? latestCandle,
        ScannerFreshnessSnapshot freshness,
        ScannerMarketWindowSnapshot marketWindow)
    {
        if (metadata is not null && (!metadata.IsTradingEnabled || !string.Equals(metadata.TradingStatus, "TRADING", StringComparison.OrdinalIgnoreCase)))
        {
            return "SymbolTradingDisabled";
        }

        if (string.IsNullOrWhiteSpace(quoteAsset) || !allowedQuoteAssets.Contains(quoteAsset, StringComparer.Ordinal))
        {
            return "QuoteAssetNotAllowed";
        }

        if (latestPriceReadStatus == SharedMarketDataCacheReadStatus.ProviderUnavailable &&
            latestKlineReadStatus == SharedMarketDataCacheReadStatus.ProviderUnavailable &&
            latestCandle is null)
        {
            return "MarketDataProviderUnavailable";
        }

        if (latestPriceReadStatus is SharedMarketDataCacheReadStatus.DeserializeFailed or SharedMarketDataCacheReadStatus.InvalidPayload &&
            latestKlineReadStatus is SharedMarketDataCacheReadStatus.DeserializeFailed or SharedMarketDataCacheReadStatus.InvalidPayload &&
            latestCandle is null)
        {
            return "MarketDataInvalidPayload";
        }

        if (!latestPrice.HasValue)
        {
            return "MissingLastPrice";
        }

        if (marketWindow.IsDeletedHistoricalWindow && !marketWindow.HistoricalRecoveryApplied)
        {
            return "DeletedHistoricalWindow";
        }

        if (marketWindow.HasHistoricalParityLag && !marketWindow.HistoricalRecoveryApplied && latestCandle is null)
        {
            return "HistoricalParityLag";
        }

        if (!quoteVolume.HasValue)
        {
            return "QuoteVolume24hMissing";
        }

        if (latestPrice.Value < scannerOptionsValue.MinPrice || latestPrice.Value > scannerOptionsValue.MaxPrice)
        {
            return "PriceOutOfRange";
        }

        if (quoteVolume.Value < scannerOptionsValue.Min24hQuoteVolume)
        {
            return "LowQuoteVolume";
        }

        if (latestCandle is null)
        {
            return "MissingMarketData";
        }

        if (freshness.IsStale)
        {
            return "StaleMarketData";
        }

        return null;
    }

    private async Task<MarketScannerStrategyScoreSummary> ResolveStrategyScoringAsync(
        string symbol,
        IReadOnlyCollection<HistoricalMarketCandle> historicalCandles,
        DateTime nowUtc,
        MarketScannerStrategyBindingResolution? strategyBindingResolution,
        CancellationToken cancellationToken)
    {
        if (!scannerOptionsValue.HandoffEnabled)
        {
            return MarketScannerStrategyScoreSummary.Accepted(
                0,
                "StrategyScoring=Skipped; Reason=ScannerHandoffDisabled; HandoffEnabled=False");
        }

        var resolvedBinding = strategyBindingResolution ?? await ResolveStrategyBindingAsync(symbol, cancellationToken);
        if (resolvedBinding.Binding is null)
        {
            return MarketScannerStrategyScoreSummary.Rejected(
                resolvedBinding.RejectionReason ?? "NoPublishedStrategy",
                resolvedBinding.ScoringSummary
                    ?? $"StrategyScore=n/a; StrategyOutcome=NoPublishedStrategy; Symbol={symbol}; Timeframe={klineInterval}");
        }

        var strategyBinding = resolvedBinding.Binding;

        if (indicatorDataService is null || strategyEvaluatorService is null)
        {
            return MarketScannerStrategyScoreSummary.Rejected(
                "StrategyScoringUnavailable",
                $"StrategyScore=n/a; StrategyOutcome=StrategyScoringUnavailable; StrategyKey={strategyBinding.StrategyKey}; Symbol={symbol}; Timeframe={klineInterval}",
                strategyBinding);
        }

        var indicatorSnapshot = await ResolveIndicatorSnapshotAsync(symbol, historicalCandles, cancellationToken);
        if (!IsUsableIndicatorSnapshot(indicatorSnapshot, symbol))
        {
            return MarketScannerStrategyScoreSummary.Rejected(
                "MissingFreshSignalData",
                $"StrategyScore=n/a; StrategyOutcome=MissingFreshSignalData; StrategyKey={strategyBinding.StrategyKey}; Symbol={symbol}; Timeframe={klineInterval}",
                strategyBinding);
        }

        try
        {
            var report = strategyEvaluatorService.EvaluateReport(
                new StrategyEvaluationReportRequest(
                    strategyBinding.TradingStrategyId,
                    strategyBinding.TradingStrategyVersionId,
                    strategyBinding.VersionNumber,
                    strategyBinding.StrategyKey,
                    strategyBinding.DisplayName,
                    strategyBinding.DefinitionJson,
                    new StrategyEvaluationContext(signalEvaluationMode, indicatorSnapshot),
                    nowUtc));

            var directionModeBlockerReason = ResolveDirectionModeRejectionReason(
                strategyBinding.DirectionMode,
                report.RuleEvaluation.EntryDirection);
            var strategyBlockerReason = directionModeBlockerReason ?? (string.Equals(report.Outcome, "EntryMatched", StringComparison.Ordinal)
                ? null
                : ResolveStrategyOutcomeRejectionReason(report, strategyBinding.DirectionMode));
            var summary = Truncate(
                $"StrategyKey={strategyBinding.StrategyKey}; Template={report.TemplateKey ?? "custom"}; Outcome={report.Outcome}; EntryDirection={report.RuleEvaluation.EntryDirection}; BotDirectionMode={strategyBinding.DirectionMode}; StrategyBlocker={strategyBlockerReason ?? "none"}; StrategyScore={report.AggregateScore}; Passed={report.PassedRuleCount}; Failed={report.FailedRuleCount}; {report.ExplainabilitySummary}",
                512);

            if (!string.Equals(report.Outcome, "EntryMatched", StringComparison.Ordinal) || directionModeBlockerReason is not null)
            {
                return MarketScannerStrategyScoreSummary.Rejected(
                    strategyBlockerReason ?? $"Strategy{report.Outcome}",
                    summary ?? $"StrategyKey={strategyBinding.StrategyKey}; Outcome={report.Outcome}; StrategyScore={report.AggregateScore}",
                    strategyBinding);
            }

            return MarketScannerStrategyScoreSummary.Accepted(report.AggregateScore, summary, strategyBinding);
        }
        catch (StrategyDefinitionValidationException exception)
        {
            return MarketScannerStrategyScoreSummary.Rejected(
                "StrategyDefinitionInvalid",
                Truncate(
                    $"StrategyValidation={exception.StatusCode}; StrategyKey={strategyBinding.StrategyKey}; Symbol={symbol}; Timeframe={klineInterval}",
                    512),
                strategyBinding);
        }
        catch (StrategyRuleParseException)
        {
            return MarketScannerStrategyScoreSummary.Rejected(
                "StrategyParseFailed",
                $"StrategyParseFailed; StrategyKey={strategyBinding.StrategyKey}; Symbol={symbol}; Timeframe={klineInterval}",
                strategyBinding);
        }
        catch (StrategyRuleEvaluationException)
        {
            return MarketScannerStrategyScoreSummary.Rejected(
                "StrategyEvaluationFailed",
                $"StrategyEvaluationFailed; StrategyKey={strategyBinding.StrategyKey}; Symbol={symbol}; Timeframe={klineInterval}",
                strategyBinding);
        }
    }

    private async Task<MarketScannerStrategyBindingResolution> ResolveStrategyBindingAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        var candidateBots = await dbContext.TradingBots
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => entity.IsEnabled && !entity.IsDeleted && entity.Symbol != null)
            .OrderBy(entity => entity.OwnerUserId)
            .ThenBy(entity => entity.Id)
            .Select(entity => new
            {
                entity.Id,
                entity.OwnerUserId,
                entity.StrategyKey,
                entity.Symbol,
                entity.DirectionMode
            })
            .ToListAsync(cancellationToken);
        MarketScannerStrategyBindingResolution? bindingMiss = null;

        foreach (var bot in candidateBots)
        {
            if (!string.Equals(MarketDataSymbolNormalizer.Normalize(bot.Symbol!), symbol, StringComparison.Ordinal))
            {
                continue;
            }

            var strategy = await dbContext.TradingStrategies
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == bot.OwnerUserId &&
                    entity.StrategyKey == bot.StrategyKey &&
                    !entity.IsDeleted)
                .OrderByDescending(entity => entity.UpdatedDate)
                .ThenByDescending(entity => entity.CreatedDate)
                .Select(entity => new
                {
                    entity.Id,
                    entity.StrategyKey,
                    entity.DisplayName
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (strategy is null)
            {
                bindingMiss = new MarketScannerStrategyBindingResolution(
                    null,
                    "NoStrategyForEnabledBot",
                    $"StrategyScore=n/a; StrategyOutcome=NoStrategyForEnabledBot; Symbol={symbol}; Timeframe={klineInterval}; StrategyKey={bot.StrategyKey}");
                continue;
            }

            var strategyVersion = await StrategyRuntimeVersionSelection.ResolveAsync(
                dbContext,
                strategy.Id,
                cancellationToken);

            if (strategyVersion is null)
            {
                bindingMiss = new MarketScannerStrategyBindingResolution(
                    null,
                    "NoPublishedStrategy",
                    $"StrategyScore=n/a; StrategyOutcome=NoPublishedStrategy; StrategyKey={strategy.StrategyKey}; Symbol={symbol}; Timeframe={klineInterval}");
                continue;
            }

            return new MarketScannerStrategyBindingResolution(
                new MarketScannerStrategyBinding(
                    bot.OwnerUserId,
                    strategy.Id,
                    strategyVersion.Id,
                    strategyVersion.VersionNumber,
                    strategy.StrategyKey,
                    symbol,
                    string.IsNullOrWhiteSpace(strategy.DisplayName) ? strategy.StrategyKey : strategy.DisplayName,
                    strategyVersion.DefinitionJson,
                    bot.DirectionMode),
                null,
                null);
        }

        return bindingMiss ?? new MarketScannerStrategyBindingResolution(
            null,
            "NoEnabledBotForSymbol",
            $"StrategyScore=n/a; StrategyOutcome=NoEnabledBotForSymbol; Symbol={symbol}; Timeframe={klineInterval}");
    }

    private static string ResolveStrategyOutcomeRejectionReason(
        StrategyEvaluationReportSnapshot report,
        TradingBotDirectionMode directionMode)
    {
        return report.Outcome switch
        {
            "RiskVetoed" => ResolveStrategyRiskVetoRejectionReason(report.RuleEvaluation),
            "ExitMatched" => ResolveStrategyExitRejectionReason(report.RuleEvaluation),
            "NoSignalCandidate" => ResolveStrategyNoSignalRejectionReason(report.RuleEvaluation, directionMode),
            _ => $"Strategy{ToReasonSegment(report.Outcome, "OutcomeBlocked")}"
        };
    }

    private static string? ResolveDirectionModeRejectionReason(
        TradingBotDirectionMode directionMode,
        StrategyTradeDirection entryDirection)
    {
        return directionMode switch
        {
            TradingBotDirectionMode.ShortOnly when entryDirection == StrategyTradeDirection.Long => "EntryDirectionModeBlocked",
            TradingBotDirectionMode.LongOnly when entryDirection == StrategyTradeDirection.Short => "EntryDirectionModeBlocked",
            _ => null
        };
    }

    private static string ResolveStrategyRiskVetoRejectionReason(StrategyEvaluationResult result)
    {
        var failedRule = FindFirstFailedEnabledRule(result.RiskRuleResult);
        var reasonSegment = ResolveRuleReasonSegment(failedRule)
            ?? ResolveRuleReasonSegment(result.RiskRuleResult)
            ?? "RiskRuleFailed";

        return TruncateReasonCode($"StrategyRiskVetoed{reasonSegment}");
    }

    private static string ResolveStrategyExitRejectionReason(StrategyEvaluationResult result)
    {
        return result.ExitDirection switch
        {
            StrategyTradeDirection.Long => "StrategyLongExitMatched",
            StrategyTradeDirection.Short => "StrategyShortExitMatched",
            _ => "StrategyExitMatchedExitRule"
        };
    }

    private static string ResolveStrategyNoSignalRejectionReason(
        StrategyEvaluationResult result,
        TradingBotDirectionMode directionMode)
    {
        if (result.HasEntryRules && !result.EntryMatched)
        {
            var scopedEntryRuleResult = ResolveDirectionScopedEntryRuleResult(result, directionMode);
            var failedRule = FindFirstFailedEnabledRule(scopedEntryRuleResult ?? result.EntryRuleResult);
            var reasonSegment = ResolveRuleReasonSegment(failedRule)
                ?? ResolveRuleReasonSegment(scopedEntryRuleResult)
                ?? ResolveRuleReasonSegment(result.EntryRuleResult)
                ?? "EntryRulesNotMatched";

            return TruncateReasonCode($"StrategyNoSignal{reasonSegment}");
        }

        return "StrategyNoSignalCandidate";
    }

    private static StrategyRuleResultSnapshot? ResolveDirectionScopedEntryRuleResult(
        StrategyEvaluationResult result,
        TradingBotDirectionMode directionMode)
    {
        return directionMode switch
        {
            TradingBotDirectionMode.LongOnly => result.LongEntryRuleResult ?? result.EntryRuleResult,
            TradingBotDirectionMode.ShortOnly => result.ShortEntryRuleResult ?? result.EntryRuleResult,
            _ => result.EntryRuleResult
        };
    }

    private static StrategyRuleResultSnapshot? FindFirstFailedEnabledRule(StrategyRuleResultSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        if (snapshot.Enabled && !snapshot.Matched && snapshot.Children.Count == 0)
        {
            return snapshot;
        }

        foreach (var child in snapshot.Children)
        {
            var failed = FindFirstFailedEnabledRule(child);
            if (failed is not null)
            {
                return failed;
            }
        }

        return snapshot.Enabled && !snapshot.Matched ? snapshot : null;
    }

    private static string? ResolveRuleReasonSegment(StrategyRuleResultSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        return ToReasonSegment(snapshot.RuleId, null)
            ?? ToReasonSegment(snapshot.RuleType, null)
            ?? ToReasonSegment(snapshot.Group, null)
            ?? ToReasonSegment(snapshot.Path, null);
    }

    private static string? ToReasonSegment(string? value, string? fallback)
    {
        var source = string.IsNullOrWhiteSpace(value) ? fallback : value;
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var builder = new StringBuilder(source.Length);
        var capitalizeNext = true;
        foreach (var character in source.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(capitalizeNext ? char.ToUpperInvariant(character) : character);
                capitalizeNext = false;
                continue;
            }

            capitalizeNext = builder.Length > 0;
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string TruncateReasonCode(string value)
    {
        return value.Length <= 64 ? value : value[..64];
    }

    private decimal ResolveMarketScore(decimal? quoteVolume)
    {
        if (!quoteVolume.HasValue || quoteVolume.Value <= 0m)
        {
            return 0m;
        }

        var baselineVolume = Math.Max(scannerOptionsValue.Min24hQuoteVolume, 1m);
        const decimal saturationMultiple = 100m;
        var normalizedRatio = decimal.Clamp(quoteVolume.Value / baselineVolume, 0m, saturationMultiple);
        var score = Math.Log10((double)(normalizedRatio + 1m)) /
                    Math.Log10((double)(saturationMultiple + 1m)) * 100d;

        return decimal.Round(
            ClampScoreBand((decimal)score),
            4,
            MidpointRounding.AwayFromZero);
    }

    private decimal ResolveCompositeScore(decimal marketScore, int? strategyScore)
    {
        var boundedMarketScore = ClampScoreBand(marketScore);
        var boundedStrategyScore = ClampScoreBand(strategyScore ?? 0);
        var boundedStrategyWeight = decimal.Clamp(scannerOptionsValue.StrategyScoreWeight, 0m, 10m);
        var totalWeight = 1m + boundedStrategyWeight;

        if (totalWeight <= 0m)
        {
            return 0m;
        }

        var compositeScore = ((boundedMarketScore * 1m) + (boundedStrategyScore * boundedStrategyWeight)) / totalWeight;
        return decimal.Round(
            ClampScoreBand(compositeScore),
            4,
            MidpointRounding.AwayFromZero);
    }

    private ScannerCandidateRankingSummary ResolveRankingSummary(
        string? initialRejectionReason,
        decimal marketScore,
        int? strategyScore,
        ScannerCandidateIntelligenceSummary candidateIntelligence,
        AiRankingOutcomeCoverageSummary outcomeCoverage)
    {
        var advisoryScore = candidateIntelligence.ShadowScore;
        if (!string.IsNullOrWhiteSpace(initialRejectionReason))
        {
            return new ScannerCandidateRankingSummary(
                null,
                advisoryScore,
                null,
                0m,
                "NotRanked",
                0m,
                outcomeCoverage.CoveragePercent,
                null,
                "NotApplicable",
                "CandidateIneligible",
                initialRejectionReason);
        }

        var classicalScore = ResolveCompositeScore(marketScore, strategyScore);
        var combinedScore = classicalScore;
        var boundedAiWeight = decimal.Clamp(scannerOptionsValue.AiAssistedRankingWeight, 0m, 1m);
        var rankingMode = scannerOptionsValue.AiAssistedRankingEnabled
            ? "ClassicalFallback"
            : "Disabled";
        string? fallbackReason = scannerOptionsValue.AiAssistedRankingEnabled
            ? null
            : "AiRankingDisabled";
        var canUseAdvisoryRanking = scannerOptionsValue.AiAssistedRankingEnabled;
        if (canUseAdvisoryRanking)
        {
            if (!advisoryScore.HasValue)
            {
                fallbackReason = "AiAdvisoryUnavailable";
                canUseAdvisoryRanking = false;
            }
            else if (ClampScoreBand(advisoryScore.Value) <
                     ClampScoreBand(scannerOptionsValue.AiAssistedRankingMinConfidenceScore))
            {
                fallbackReason = "ConfidenceBelowThreshold";
                canUseAdvisoryRanking = false;
            }
            else if (!outcomeCoverage.IsAvailable)
            {
                fallbackReason = outcomeCoverage.FallbackReason ?? "OutcomeCoverageUnavailable";
                canUseAdvisoryRanking = false;
            }
            else if (outcomeCoverage.CoveragePercent <
                     decimal.Round(
                         ClampScoreBand(scannerOptionsValue.AiAssistedRankingMinOutcomeCoveragePercent),
                         2,
                         MidpointRounding.AwayFromZero))
            {
                fallbackReason = "OutcomeCoverageBelowThreshold";
                canUseAdvisoryRanking = false;
            }
            else if (boundedAiWeight <= 0m)
            {
                fallbackReason = "AiInfluenceWeightZero";
                canUseAdvisoryRanking = false;
            }
        }

        if (canUseAdvisoryRanking && advisoryScore.HasValue)
        {
            combinedScore = decimal.Round(
                ClampScoreBand(
                    (classicalScore * (1m - boundedAiWeight)) +
                    (ClampScoreBand(advisoryScore.Value) * boundedAiWeight)),
                4,
                MidpointRounding.AwayFromZero);
            rankingMode = "AdvisoryCombined";
        }

        var adaptiveFilteringEnabled = scannerOptionsValue.AiAssistedRankingEnabled &&
                                       scannerOptionsValue.AdaptiveFilteringEnabled;
        var adaptiveFilterState = adaptiveFilteringEnabled
            ? "Passed"
            : "Disabled";
        string? adaptiveFilterReason = adaptiveFilteringEnabled
            ? "ThresholdNotMet"
            : null;
        string? rejectionReason = null;

        if (adaptiveFilteringEnabled)
        {
            if (!advisoryScore.HasValue)
            {
                adaptiveFilterReason = "ConfidenceInsufficient";
            }
            else if (!canUseAdvisoryRanking)
            {
                adaptiveFilterReason = fallbackReason;
            }
            else if (ClampScoreBand(advisoryScore.Value) <= ClampScoreBand(scannerOptionsValue.AdaptiveFilteringMaxAdvisoryScore) &&
                     classicalScore <= ClampScoreBand(scannerOptionsValue.AdaptiveFilteringMaxClassicalScore))
            {
                adaptiveFilterState = "Suppressed";
                adaptiveFilterReason = "LowAdvisoryScoreAndWeakClassicalScore";
                rejectionReason = "AdaptiveFilterLowQualitySetup";
            }
        }

        return new ScannerCandidateRankingSummary(
            classicalScore,
            advisoryScore,
            combinedScore,
            rejectionReason is null ? combinedScore : 0m,
            rankingMode,
            boundedAiWeight,
            outcomeCoverage.CoveragePercent,
            fallbackReason,
            adaptiveFilterState,
            adaptiveFilterReason,
            rejectionReason);
    }

    private string? BuildCandidateScoringSummary(
        string? scoringSummary,
        ScannerMarketWindowSnapshot marketWindow,
        ScannerCandidateIntelligenceSummary candidateIntelligence,
        ScannerCandidateRankingSummary rankingSummary)
    {
        var summarySegments = new List<string>(8);
        if (!string.IsNullOrWhiteSpace(scoringSummary))
        {
            summarySegments.Add(scoringSummary!);
        }

        summarySegments.Add($"ScannerRankingMode={rankingSummary.RankingMode}");
        summarySegments.Add($"ScannerClassicalScore={(rankingSummary.ClassicalScore?.ToString("0.####", CultureInfo.InvariantCulture) ?? "n/a")}");
        summarySegments.Add($"ScannerCombinedScore={(rankingSummary.CombinedScore?.ToString("0.####", CultureInfo.InvariantCulture) ?? "n/a")}");
        summarySegments.Add($"ScannerAiInfluenceWeight={rankingSummary.InfluenceWeight.ToString("0.####", CultureInfo.InvariantCulture)}");
        if (rankingSummary.OutcomeCoveragePercent.HasValue)
        {
            summarySegments.Add($"ScannerOutcomeCoveragePercent={rankingSummary.OutcomeCoveragePercent.Value.ToString("0.##", CultureInfo.InvariantCulture)}");
        }
        if (!string.IsNullOrWhiteSpace(rankingSummary.FallbackReason))
        {
            summarySegments.Add($"ScannerRankingFallbackReason={rankingSummary.FallbackReason}");
        }

        if (candidateIntelligence.Labels.Count > 0)
        {
            summarySegments.Add($"ScannerLabels={string.Join(',', candidateIntelligence.Labels)}");
        }

        if (candidateIntelligence.ReasonCodes.Count > 0)
        {
            summarySegments.Add($"ScannerReasonCodes={string.Join(',', candidateIntelligence.ReasonCodes)}");
        }

        if (!string.IsNullOrWhiteSpace(candidateIntelligence.Summary))
        {
            summarySegments.Add($"ScannerReasonSummary={candidateIntelligence.Summary}");
        }

        if (candidateIntelligence.ShadowScore.HasValue)
        {
            summarySegments.Add($"ScannerShadowScore={candidateIntelligence.ShadowScore.Value}");
        }

        if (candidateIntelligence.ShadowContributions.Count > 0)
        {
            summarySegments.Add($"ScannerShadowContributions={string.Join(',', candidateIntelligence.ShadowContributions)}");
        }

        summarySegments.Add($"ScannerAdaptiveFilterState={rankingSummary.AdaptiveFilterState}");
        if (!string.IsNullOrWhiteSpace(rankingSummary.AdaptiveFilterReason))
        {
            summarySegments.Add($"ScannerAdaptiveFilterReason={rankingSummary.AdaptiveFilterReason}");
        }

        if (marketWindow.HistoricalRecoveryApplied || marketWindow.HasHistoricalParityLag)
        {
            summarySegments.Add(
                $"HistoricalLastCandleAtUtc={(marketWindow.LatestHistoricalCandleAtUtc?.ToString("O") ?? "n/a")}; HistoricalParityLagSeconds={(marketWindow.HistoricalParityLagSeconds?.ToString(CultureInfo.InvariantCulture) ?? "n/a")}; HistoricalRecoveryApplied={marketWindow.HistoricalRecoveryApplied}; HistoricalRecoverySource={(marketWindow.HistoricalRecoverySource ?? "n/a")}");
        }

        if (summarySegments.Count == 0)
        {
            return null;
        }

        return Truncate(string.Join("; ", summarySegments), CandidateScoringSummaryMaxLength);
    }

    private async Task<AiRankingOutcomeCoverageSummary> ResolveAiRankingOutcomeCoverageAsync(
        MarketScannerStrategyBinding? strategyBinding,
        CancellationToken cancellationToken)
    {
        if (!scannerOptionsValue.AiAssistedRankingEnabled)
        {
            return AiRankingOutcomeCoverageSummary.Disabled();
        }

        if (strategyBinding is null || string.IsNullOrWhiteSpace(strategyBinding.OwnerUserId))
        {
            return AiRankingOutcomeCoverageSummary.Unavailable("OutcomeCoverageUnavailable");
        }

        if (aiShadowDecisionService is null)
        {
            return AiRankingOutcomeCoverageSummary.Unavailable("OutcomeCoverageUnavailable");
        }

        if (aiRankingOutcomeCoverageCache.TryGetValue(strategyBinding.OwnerUserId, out var cachedSummary))
        {
            return cachedSummary;
        }

        try
        {
            var summary = await aiShadowDecisionService.GetOutcomeSummaryAsync(
                strategyBinding.OwnerUserId,
                take: 200,
                cancellationToken: cancellationToken);
            var coveragePercent = summary.TotalDecisionCount <= 0
                ? 0m
                : decimal.Round(
                    (summary.ScoredCount * 100m) / summary.TotalDecisionCount,
                    2,
                    MidpointRounding.AwayFromZero);
            var resolvedSummary = new AiRankingOutcomeCoverageSummary(
                true,
                coveragePercent,
                null);
            aiRankingOutcomeCoverageCache[strategyBinding.OwnerUserId] = resolvedSummary;
            return resolvedSummary;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Market scanner AI ranking outcome coverage lookup failed. StrategyKey={StrategyKey} Symbol={Symbol} Timeframe={Timeframe}.",
                strategyBinding.StrategyKey,
                strategyBinding.Symbol,
                klineInterval);
            var unavailableSummary = AiRankingOutcomeCoverageSummary.Unavailable("OutcomeCoverageUnavailable");
            aiRankingOutcomeCoverageCache[strategyBinding.OwnerUserId] = unavailableSummary;
            return unavailableSummary;
        }
    }

    private void ApplyAdaptiveFilteringSuppressionGuardrail(List<MarketScannerCandidate> candidates)
    {
        if (!scannerOptionsValue.AiAssistedRankingEnabled || !scannerOptionsValue.AdaptiveFilteringEnabled)
        {
            return;
        }

        var rankedCandidates = candidates
            .Where(candidate =>
                !string.Equals(ExtractToken(candidate.ScoringSummary, "ScannerRankingMode"), "NotRanked", StringComparison.Ordinal))
            .ToArray();
        if (rankedCandidates.Length == 0)
        {
            return;
        }

        var suppressedCandidates = rankedCandidates
            .Where(candidate => string.Equals(candidate.RejectionReason, "AdaptiveFilterLowQualitySetup", StringComparison.Ordinal))
            .OrderByDescending(ResolveSuppressionGuardrailReleaseScore)
            .ThenBy(candidate => candidate.Symbol, StringComparer.Ordinal)
            .ToArray();
        if (suppressedCandidates.Length == 0)
        {
            return;
        }

        var boundedRatio = decimal.Clamp(scannerOptionsValue.AdaptiveFilteringMaxSuppressionRatio, 0m, 1m);
        var maximumSuppressedCount = boundedRatio <= 0m
            ? 0
            : (int)Math.Ceiling(rankedCandidates.Length * (double)boundedRatio);
        if (suppressedCandidates.Length <= maximumSuppressedCount)
        {
            return;
        }

        var releaseCount = suppressedCandidates.Length - maximumSuppressedCount;
        foreach (var candidate in suppressedCandidates.Take(releaseCount))
        {
            var restoredScore = ResolveSuppressionGuardrailReleaseScore(candidate);
            candidate.IsEligible = true;
            candidate.RejectionReason = null;
            candidate.Score = restoredScore;
            candidate.ScoringSummary = UpsertSummaryToken(candidate.ScoringSummary, "ScannerAdaptiveFilterState", "ReleasedByGuardrail");
            candidate.ScoringSummary = UpsertSummaryToken(candidate.ScoringSummary, "ScannerAdaptiveFilterReason", "SuppressionRatioExceeded");
        }
    }

    private static decimal ResolveSuppressionGuardrailReleaseScore(MarketScannerCandidate candidate)
    {
        if (TryResolveDecimalToken(candidate.ScoringSummary, "ScannerCombinedScore", out var combinedScore))
        {
            return combinedScore;
        }

        if (TryResolveDecimalToken(candidate.ScoringSummary, "ScannerClassicalScore", out var classicalScore))
        {
            return classicalScore;
        }

        return candidate.Score;
    }

    private async Task<ScannerCandidateIntelligenceSummary> ResolveCandidateIntelligenceAsync(
        string symbol,
        IReadOnlyCollection<HistoricalMarketCandle> historicalCandles,
        DateTime observedAtUtc,
        decimal? latestPrice,
        CancellationToken cancellationToken)
    {
        var indicatorSnapshot = await ResolveIndicatorSnapshotAsync(symbol, historicalCandles, cancellationToken);
        if (!IsUsableIndicatorSnapshot(indicatorSnapshot, symbol))
        {
            return ScannerCandidateIntelligenceSummary.Empty;
        }

        return ResolveCandidateLabels(latestPrice, indicatorSnapshot!, observedAtUtc);
    }

    private async Task<StrategyIndicatorSnapshot?> ResolveIndicatorSnapshotAsync(
        string symbol,
        IReadOnlyCollection<HistoricalMarketCandle> historicalCandles,
        CancellationToken cancellationToken)
    {
        if (indicatorDataService is null)
        {
            return null;
        }

        await indicatorDataService.TrackSymbolAsync(symbol, cancellationToken);
        var indicatorSnapshot = await indicatorDataService.GetLatestAsync(symbol, klineInterval, cancellationToken);
        if (indicatorSnapshot is not null && indicatorSnapshot.State == IndicatorDataState.Ready)
        {
            return indicatorSnapshot;
        }

        var marketCandles = historicalCandles
            .OrderBy(entity => entity.OpenTimeUtc)
            .Select(entity => new MarketCandleSnapshot(
                entity.Symbol,
                entity.Interval,
                NormalizeUtc(entity.OpenTimeUtc),
                NormalizeUtc(entity.CloseTimeUtc),
                entity.OpenPrice,
                entity.HighPrice,
                entity.LowPrice,
                entity.ClosePrice,
                entity.Volume,
                true,
                NormalizeUtc(entity.ReceivedAtUtc),
                entity.Source))
            .ToArray();

        if (marketCandles.Length == 0)
        {
            return indicatorSnapshot;
        }

        return await indicatorDataService.PrimeAsync(symbol, klineInterval, marketCandles, cancellationToken);
    }

    private bool IsUsableIndicatorSnapshot(StrategyIndicatorSnapshot? indicatorSnapshot, string symbol)
    {
        return indicatorSnapshot is not null &&
               indicatorSnapshot.State == IndicatorDataState.Ready &&
               string.Equals(indicatorSnapshot.Symbol, symbol, StringComparison.Ordinal) &&
               string.Equals(indicatorSnapshot.Timeframe, klineInterval, StringComparison.Ordinal);
    }

    private static ScannerCandidateIntelligenceSummary ResolveCandidateLabels(
        decimal? latestPrice,
        StrategyIndicatorSnapshot indicatorSnapshot,
        DateTime observedAtUtc)
    {
        var labels = new List<string>(3);
        var reasonCodes = new List<string>(3);
        var summarySegments = new List<string>(3);
        var shadowContributions = new List<string>(3);
        if (TryResolveBollingerBandWidth(indicatorSnapshot, out var bandWidth) && bandWidth <= 5m)
        {
            labels.Add("HasCompressionBreakoutSetup");
            reasonCodes.Add("CompressionBreakoutSetupDetected");
            summarySegments.Add("Compression breakout setup detected from tight Bollinger bandwidth.");
            shadowContributions.Add($"CompressionBreakoutSetupDetected:+{CompressionBreakoutSetupShadowScore}");
        }

        if (indicatorSnapshot.Macd.IsReady &&
            indicatorSnapshot.Macd.MacdLine is decimal macdLine &&
            indicatorSnapshot.Macd.SignalLine is decimal signalLine &&
            indicatorSnapshot.Macd.Histogram is decimal histogram &&
            indicatorSnapshot.Bollinger.MiddleBand is decimal middleBand &&
            latestPrice is decimal currentPrice)
        {
            if (currentPrice >= middleBand &&
                macdLine > signalLine &&
                histogram > 0m &&
                indicatorSnapshot.CloseTimeUtc <= observedAtUtc)
            {
                labels.Add("HasTrendBreakoutUp");
                reasonCodes.Add("TrendBreakoutConfirmed");
                summarySegments.Add("Bullish trend breakout confirmed above the Bollinger mid-band with positive MACD alignment.");
                shadowContributions.Add($"TrendBreakoutConfirmed:+{TrendBreakoutShadowScore}");
            }
            else if (currentPrice <= middleBand &&
                     macdLine < signalLine &&
                     histogram < 0m &&
                     indicatorSnapshot.CloseTimeUtc <= observedAtUtc)
            {
                labels.Add("HasTrendBreakoutDown");
                reasonCodes.Add("TrendBreakdownConfirmed");
                summarySegments.Add("Bearish trend breakdown confirmed below the Bollinger mid-band with negative MACD alignment.");
                shadowContributions.Add($"TrendBreakdownConfirmed:+{TrendBreakoutShadowScore}");
            }
        }

        var distinctShadowContributions = shadowContributions
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var shadowScore = distinctShadowContributions.Length == 0
            ? (int?)null
            : Math.Min(
                100,
                distinctShadowContributions.Sum(item => ResolveShadowContributionScore(item)));

        return new ScannerCandidateIntelligenceSummary(
            labels
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray(),
            reasonCodes
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray(),
            summarySegments.Count == 0
                ? null
                : Truncate(string.Join(" | ", summarySegments), 240),
            shadowScore,
            distinctShadowContributions);
    }

    private static bool TryResolveBollingerBandWidth(
        StrategyIndicatorSnapshot indicatorSnapshot,
        out decimal bandWidth)
    {
        bandWidth = 0m;
        if (indicatorSnapshot.Bollinger.UpperBand is not decimal upperBand ||
            indicatorSnapshot.Bollinger.LowerBand is not decimal lowerBand ||
            indicatorSnapshot.Bollinger.MiddleBand is not decimal middleBand ||
            middleBand == 0m)
        {
            return false;
        }

        bandWidth = decimal.Round(
            (upperBand - lowerBand) / middleBand * 100m,
            6,
            MidpointRounding.AwayFromZero);
        return true;
    }

    private static int ResolveShadowContributionScore(string contribution)
    {
        var separatorIndex = contribution.LastIndexOf(':');
        if (separatorIndex <= 0 || separatorIndex >= contribution.Length - 1)
        {
            return 0;
        }

        var pointsText = contribution[(separatorIndex + 1)..].Trim();
        if (!int.TryParse(pointsText.TrimStart('+'), out var score))
        {
            return 0;
        }

        return score;
    }

    private string BuildMarketRejectionSummary(
        string rejectionReason,
        decimal marketScore,
        decimal? quoteVolume,
        ScannerFreshnessSnapshot freshness,
        ScannerMarketWindowSnapshot marketWindow)
    {
        return Truncate(
            $"MarketRejected={rejectionReason}; QuoteVolume24h={(quoteVolume?.ToString(CultureInfo.InvariantCulture) ?? "n/a")}; MarketScore={marketScore.ToString("0.####", CultureInfo.InvariantCulture)}; FreshnessSource={freshness.FreshnessSource}; Diagnostic={freshness.DiagnosticCode}; LastCandleAtUtc={(freshness.LastCandleAtUtc?.ToString("O") ?? "n/a")}; ObservedAtUtc={freshness.ObservedAtUtc:O}; DataAgeSeconds={(freshness.DataAgeSeconds?.ToString(CultureInfo.InvariantCulture) ?? "n/a")}; ThresholdSeconds={scannerOptionsValue.MaxDataAgeSeconds.ToString(CultureInfo.InvariantCulture)}; TickerStatus={freshness.TickerReadStatus}; KlineStatus={freshness.KlineReadStatus}; KlineReason={freshness.KlineReasonCode}; KlineReasonSummary={freshness.KlineReasonSummary ?? "n/a"}; HistoricalLastCandleAtUtc={(marketWindow.LatestHistoricalCandleAtUtc?.ToString("O") ?? "n/a")}; HistoricalParityLagSeconds={(marketWindow.HistoricalParityLagSeconds?.ToString(CultureInfo.InvariantCulture) ?? "n/a")}; HistoricalRecoveryApplied={marketWindow.HistoricalRecoveryApplied}; HistoricalRecoverySource={(marketWindow.HistoricalRecoverySource ?? "n/a")}",
            512)
            ?? $"MarketRejected={rejectionReason}; FreshnessSource={freshness.FreshnessSource}";
    }

    private static decimal ClampScoreBand(decimal value)
    {
        return decimal.Clamp(value, 0m, 100m);
    }

    private FreshnessPauseSummary? ResolveFreshnessPause(IReadOnlyCollection<MarketScannerCandidate> candidates)
    {
        if (candidates.Count == 0 ||
            candidates.Any(item => item.IsEligible) ||
            candidates.Any(item => !string.Equals(item.RejectionReason, "StaleMarketData", StringComparison.Ordinal)))
        {
            return null;
        }

        var orderedCandidates = candidates
            .OrderBy(item => item.Symbol, StringComparer.Ordinal)
            .ToArray();
        var sample = orderedCandidates[0];
        var staleCandidates = orderedCandidates
            .Select(candidate => $"{candidate.Symbol}:{BuildFreshnessPauseAge(candidate)}")
            .ToArray();
        var summary = $"Market scanner paused because fresh candle data is unavailable across {orderedCandidates.Length} scanned symbols. FreshnessSource={ResolveFreshnessPauseSource(sample.ScoringSummary)}; Sample={string.Join(", ", staleCandidates.Take(3))}.";
        var detail = $"ScannerPause=FreshnessRecovery; Reason=AllSymbolsStaleMarketData; StaleSymbols={orderedCandidates.Length}; Sample={string.Join(" | ", staleCandidates)}; SampleSummary={sample.ScoringSummary ?? "n/a"}";
        return new FreshnessPauseSummary(summary, detail);
    }

    private static string BuildFreshnessPauseAge(MarketScannerCandidate candidate)
    {
        var observedAtUtc = NormalizeUtc(candidate.ObservedAtUtc);
        var ageSeconds = candidate.LastCandleAtUtc.HasValue
            ? Math.Max(0, (int)Math.Round((observedAtUtc - NormalizeUtc(candidate.LastCandleAtUtc.Value)).TotalSeconds, MidpointRounding.AwayFromZero))
            : -1;
        return ageSeconds >= 0
            ? $"{ageSeconds}s"
            : "n/a";
    }

    private static string ResolveFreshnessPauseSource(string? scoringSummary)
    {
        return ExtractToken(scoringSummary, "FreshnessSource") ?? "n/a";
    }

    private static string? ExtractToken(string? summary, string key)
    {
        if (string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var token = key + "=";
        var startIndex = summary.IndexOf(token, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            return null;
        }

        startIndex += token.Length;
        var endIndex = summary.IndexOf(';', startIndex);
        var value = endIndex >= 0
            ? summary[startIndex..endIndex]
            : summary[startIndex..];
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static bool TryResolveDecimalToken(string? summary, string key, out decimal value)
    {
        value = 0m;
        var rawValue = ExtractToken(summary, key);
        return !string.IsNullOrWhiteSpace(rawValue) &&
               !string.Equals(rawValue, "n/a", StringComparison.OrdinalIgnoreCase) &&
               decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static string? UpsertSummaryToken(string? summary, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return Truncate(summary, CandidateScoringSummaryMaxLength);
        }

        var segments = string.IsNullOrWhiteSpace(summary)
            ? new List<string>()
            : summary
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();
        var tokenPrefix = key + "=";
        var replaced = false;

        for (var index = segments.Count - 1; index >= 0; index--)
        {
            if (!segments[index].StartsWith(tokenPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                segments.RemoveAt(index);
                continue;
            }

            segments[index] = tokenPrefix + value;
            replaced = true;
        }

        if (!replaced && !string.IsNullOrWhiteSpace(value))
        {
            segments.Add(tokenPrefix + value);
        }

        return Truncate(string.Join("; ", segments), CandidateScoringSummaryMaxLength);
    }

    private static int ResolveTopCandidateChangedByAiCount(IReadOnlyCollection<MarketScannerCandidate> candidates)
    {
        var eligibleCandidates = candidates
            .Where(candidate => candidate.IsEligible)
            .ToArray();
        if (eligibleCandidates.Length == 0)
        {
            return 0;
        }

        if (!eligibleCandidates.Any(candidate =>
                string.Equals(ExtractToken(candidate.ScoringSummary, "ScannerRankingMode"), "AdvisoryCombined", StringComparison.Ordinal)))
        {
            return 0;
        }

        var aiTopCandidate = eligibleCandidates
            .OrderBy(candidate => candidate.Rank ?? int.MaxValue)
            .ThenBy(candidate => candidate.Symbol, StringComparer.Ordinal)
            .FirstOrDefault();
        var classicalTopCandidate = eligibleCandidates
            .OrderByDescending(candidate =>
                TryResolveDecimalToken(candidate.ScoringSummary, "ScannerClassicalScore", out var classicalScore)
                    ? classicalScore
                    : candidate.Score)
            .ThenBy(candidate => candidate.Symbol, StringComparer.Ordinal)
            .FirstOrDefault();

        return aiTopCandidate is not null &&
               classicalTopCandidate is not null &&
               !string.Equals(aiTopCandidate.Symbol, classicalTopCandidate.Symbol, StringComparison.Ordinal)
            ? 1
            : 0;
    }

    private static string ResolveUniverseSummary(IReadOnlyCollection<UniverseSymbolCandidate> universe)
    {
        if (universe.Count == 0)
        {
            return "none";
        }

        return string.Join(
            '+',
            universe
                .SelectMany(item => item.UniverseSource.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal));
    }

    private static string BuildCycleSummary(MarketScannerCycle cycle, IReadOnlyCollection<MarketScannerCandidate> candidates)
    {
        var bestCandidate = candidates
            .Where(item => item.IsTopCandidate)
            .OrderBy(item => item.Rank ?? int.MaxValue)
            .ThenBy(item => item.Symbol, StringComparer.Ordinal)
            .FirstOrDefault();

        if (bestCandidate is not null)
        {
            return $"Market scanner evaluated {cycle.ScannedSymbolCount} symbols and ranked {bestCandidate.Symbol} #{bestCandidate.Rank} with score {bestCandidate.Score.ToString("0.####", CultureInfo.InvariantCulture)}.";
        }

        var rejectionSummary = BuildRejectionBreakdown(candidates, maxEntries: 4, maxSymbolsPerReason: 2);
        return string.IsNullOrWhiteSpace(rejectionSummary)
            ? $"Market scanner evaluated {cycle.ScannedSymbolCount} symbols and found no eligible candidates."
            : $"Market scanner evaluated {cycle.ScannedSymbolCount} symbols and found no eligible candidates. Reasons={rejectionSummary}.";
    }

    private static string BuildCycleDetail(
        MarketScannerCycle cycle,
        IReadOnlyCollection<MarketScannerCandidate> candidates,
        bool handoffEnabled,
        string? executionHost,
        string timeframe)
    {
        var rejectedReason = BuildRejectionBreakdown(candidates, maxEntries: 6, maxSymbolsPerReason: 3) ?? "none";
        var normalizedExecutionHost = string.IsNullOrWhiteSpace(executionHost)
            ? "n/a"
            : executionHost.Trim();
        var aiRankingFallbackCount = candidates.Count(candidate =>
            string.Equals(ExtractToken(candidate.ScoringSummary, "ScannerRankingMode"), "ClassicalFallback", StringComparison.Ordinal));
        var adaptiveSuppressionCount = candidates.Count(candidate =>
            string.Equals(ExtractToken(candidate.ScoringSummary, "ScannerAdaptiveFilterState"), "Suppressed", StringComparison.Ordinal));
        var topCandidateChangedByAiCount = ResolveTopCandidateChangedByAiCount(candidates);

        return $"ScanCycleId={cycle.Id}; Timeframe={timeframe}; HandoffEnabled={handoffEnabled}; ExecutionHost={normalizedExecutionHost}; UniverseSource={cycle.UniverseSource}; Scanned={cycle.ScannedSymbolCount}; Eligible={cycle.EligibleCandidateCount}; Top={cycle.TopCandidateCount}; BestCandidate={cycle.BestCandidateSymbol ?? "n/a"}; BestScore={cycle.BestCandidateScore?.ToString("0.####", CultureInfo.InvariantCulture) ?? "n/a"}; AiRankingFallbackCount={aiRankingFallbackCount}; AiRankingSuppressionCount={adaptiveSuppressionCount}; AiRankingTopCandidateChangedCount={topCandidateChangedByAiCount}; TopRejectReason={rejectedReason}; CompletedAtUtc={cycle.CompletedAtUtc:O}";
    }

    private static string? ResolveDominantRejectionReason(IReadOnlyCollection<MarketScannerCandidate> candidates)
    {
        return candidates
            .Where(item => !item.IsEligible && !string.IsNullOrWhiteSpace(item.RejectionReason))
            .GroupBy(item => item.RejectionReason!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key)
            .FirstOrDefault();
    }

    private static string? BuildRejectionBreakdown(
        IReadOnlyCollection<MarketScannerCandidate> candidates,
        int maxEntries,
        int maxSymbolsPerReason)
    {
        var groups = candidates
            .Where(item => !item.IsEligible && !string.IsNullOrWhiteSpace(item.RejectionReason))
            .GroupBy(item => item.RejectionReason!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(Math.Max(1, maxEntries))
            .Select(group =>
            {
                var symbols = group
                    .Select(item => item.Symbol)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .Take(Math.Max(1, maxSymbolsPerReason))
                    .ToArray();

                return symbols.Length == 0
                    ? $"{group.Key}:{group.Count()}"
                    : $"{group.Key}:{group.Count()} [{string.Join(", ", symbols)}]";
            })
            .ToArray();

        return groups.Length == 0
            ? null
            : string.Join(" | ", groups);
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

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }


    private sealed record ScannerFreshnessSnapshot(
        DateTime ObservedAtUtc,
        DateTime? LastCandleAtUtc,
        int? DataAgeSeconds,
        string FreshnessSource,
        string DiagnosticCode,
        SharedMarketDataCacheReadStatus TickerReadStatus,
        SharedMarketDataCacheReadStatus KlineReadStatus,
        string KlineReasonCode,
        string? KlineReasonSummary,
        bool IsStale);

    private sealed record FreshnessPauseSummary(string Summary, string Detail);

    private sealed record ScannerMarketWindowSnapshot(
        IReadOnlyCollection<HistoricalMarketCandle> Candles,
        HistoricalMarketCandle? LatestCandle,
        DateTime? LatestHistoricalCandleAtUtc,
        decimal? QuoteVolume24h,
        bool IsDeletedHistoricalWindow,
        bool HasHistoricalParityLag,
        int? HistoricalParityLagSeconds,
        bool HistoricalRecoveryApplied,
        string? HistoricalRecoverySource);

    private sealed record HistoricalWindowRecoveryResult(
        IReadOnlyCollection<HistoricalMarketCandle> Candles,
        bool RecoveryApplied,
        string? RecoverySource);

    private readonly record struct UniverseSymbolCandidate(string Symbol, string UniverseSource);

    private sealed record MarketScannerStrategyBinding(
        string OwnerUserId,
        Guid TradingStrategyId,
        Guid TradingStrategyVersionId,
        int VersionNumber,
        string StrategyKey,
        string Symbol,
        string DisplayName,
        string DefinitionJson,
        TradingBotDirectionMode DirectionMode);

    private sealed record MarketScannerStrategyBindingResolution(
        MarketScannerStrategyBinding? Binding,
        string? RejectionReason,
        string? ScoringSummary);

    private sealed record MarketScannerStrategyScoreSummary(
        int? StrategyScore,
        string? RejectionReason,
        string? ScoringSummary,
        MarketScannerStrategyBinding? StrategyBinding)
    {
        public static MarketScannerStrategyScoreSummary Accepted(
            int strategyScore,
            string? scoringSummary,
            MarketScannerStrategyBinding? strategyBinding = null) =>
            new(strategyScore, null, scoringSummary, strategyBinding);

        public static MarketScannerStrategyScoreSummary Rejected(
            string rejectionReason,
            string? scoringSummary,
            MarketScannerStrategyBinding? strategyBinding = null) =>
            new(null, rejectionReason, scoringSummary, strategyBinding);

        public static MarketScannerStrategyScoreSummary MarketRejected(string rejectionReason, string? scoringSummary) =>
            new(null, rejectionReason, scoringSummary, null);
    }

    private sealed record ScannerCandidateIntelligenceSummary(
        IReadOnlyCollection<string> Labels,
        IReadOnlyCollection<string> ReasonCodes,
        string? Summary,
        int? ShadowScore,
        IReadOnlyCollection<string> ShadowContributions)
    {
        public static ScannerCandidateIntelligenceSummary Empty { get; } =
            new(Array.Empty<string>(), Array.Empty<string>(), null, null, Array.Empty<string>());
    }

    private sealed record ScannerCandidateRankingSummary(
        decimal? ClassicalScore,
        int? AdvisoryScore,
        decimal? CombinedScore,
        decimal EffectiveScore,
        string RankingMode,
        decimal InfluenceWeight,
        decimal? OutcomeCoveragePercent,
        string? FallbackReason,
        string AdaptiveFilterState,
        string? AdaptiveFilterReason,
        string? RejectionReason);

    private sealed record AiRankingOutcomeCoverageSummary(
        bool IsAvailable,
        decimal? CoveragePercent,
        string? FallbackReason)
    {
        public static AiRankingOutcomeCoverageSummary Disabled() => new(false, null, "AiRankingDisabled");

        public static AiRankingOutcomeCoverageSummary Unavailable(string? fallbackReason = null) =>
            new(false, null, fallbackReason ?? "OutcomeCoverageUnavailable");
    }
}
