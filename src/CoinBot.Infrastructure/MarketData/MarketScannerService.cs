using System.Globalization;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Persistence;
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
    IOptions<BotExecutionPilotOptions>? botExecutionPilotOptions = null)
{
    internal const string WorkerKey = "market-scanner";
    internal const string WorkerName = "Market Scanner";

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
        botExecutionPilotOptions?.Value.SignalEvaluationMode ?? ExecutionEnvironment.Live;

    public async Task<MarketScannerCycle> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var startedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var universe = await ResolveUniverseAsync(startedAtUtc, cancellationToken);
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
            candidates.Add(await EvaluateCandidateAsync(cycle.Id, candidate, startedAtUtc, cancellationToken));
        }

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
        cycle.Summary = BuildCycleSummary(cycle, candidates);

        if (candidates.Count > 0)
        {
            dbContext.MarketScannerCandidates.AddRange(candidates);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (handoffService is not null)
        {
            await handoffService.RunOnceAsync(cycle.Id, cancellationToken);
        }

        await UpsertWorkerHeartbeatAsync(cycle, candidates, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Market scanner cycle completed. ScanCycleId={ScanCycleId} Scanned={ScannedSymbolCount} Eligible={EligibleCandidateCount} TopCandidates={TopCandidateCount} BestCandidate={BestCandidateSymbol}.",
            cycle.Id,
            cycle.ScannedSymbolCount,
            cycle.EligibleCandidateCount,
            cycle.TopCandidateCount,
            cycle.BestCandidateSymbol ?? "n/a");

        return cycle;
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
        var recentCandleSymbols = await dbContext.HistoricalMarketCandles
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.Interval == klineInterval &&
                entity.CloseTimeUtc >= recentWindowStartUtc)
            .Select(entity => entity.Symbol)
            .Distinct()
            .ToListAsync(cancellationToken);
        AddSymbols(universe, recentCandleSymbols, "historical-candles");

        return universe
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Take(scannerOptionsValue.MaxUniverseSymbols)
            .Select(item => new UniverseSymbolCandidate(item.Key, string.Join('+', item.Value)))
            .ToArray();
    }


    private async Task<MarketScannerCandidate> EvaluateCandidateAsync(
        Guid scanCycleId,
        UniverseSymbolCandidate universeCandidate,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var metadata = await sharedSymbolRegistry.GetSymbolAsync(universeCandidate.Symbol, cancellationToken);
        var latestPriceRead = await marketDataService.ReadLatestPriceAsync(universeCandidate.Symbol, cancellationToken);
        var latestPriceSnapshot = latestPriceRead.Status == SharedMarketDataCacheReadStatus.HitFresh
            ? latestPriceRead.Entry?.Payload
            : null;
        var windowStartUtc = nowUtc.AddHours(-scannerOptionsValue.VolumeLookbackHours);
        var candles = await dbContext.HistoricalMarketCandles
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.Symbol == universeCandidate.Symbol &&
                entity.Interval == klineInterval &&
                entity.CloseTimeUtc >= windowStartUtc &&
                entity.CloseTimeUtc <= nowUtc)
            .OrderByDescending(entity => entity.CloseTimeUtc)
            .ToListAsync(cancellationToken);

        var latestCandle = candles.FirstOrDefault();
        var quoteAsset = ResolveQuoteAsset(universeCandidate.Symbol, metadata);
        var latestPrice = ResolveLatestPrice(latestPriceSnapshot, latestCandle);
        var quoteVolume = candles.Count == 0
            ? (decimal?)null
            : candles.Sum(item => item.ClosePrice * item.Volume);
        var marketScore = quoteVolume ?? 0m;
        var rejectionReason = ResolveRejectionReason(
            metadata,
            quoteAsset,
            latestPriceRead.Status,
            latestPrice,
            quoteVolume,
            latestCandle,
            nowUtc);
        var strategyScoring = rejectionReason is null
            ? await ResolveStrategyScoringAsync(
                universeCandidate.Symbol,
                candles,
                nowUtc,
                cancellationToken)
            : MarketScannerStrategyScoreSummary.MarketRejected(
                rejectionReason,
                $"MarketRejected={rejectionReason}; MarketScore={marketScore.ToString("0.####", CultureInfo.InvariantCulture)}");
        rejectionReason ??= strategyScoring.RejectionReason;
        var isEligible = rejectionReason is null;

        return new MarketScannerCandidate
        {
            Id = Guid.NewGuid(),
            ScanCycleId = scanCycleId,
            Symbol = universeCandidate.Symbol,
            UniverseSource = universeCandidate.UniverseSource,
            ObservedAtUtc = nowUtc,
            LastCandleAtUtc = latestCandle?.CloseTimeUtc,
            LastPrice = latestPrice,
            QuoteVolume24h = quoteVolume,
            MarketScore = marketScore,
            StrategyScore = strategyScoring.StrategyScore,
            ScoringSummary = strategyScoring.ScoringSummary,
            IsEligible = isEligible,
            RejectionReason = rejectionReason,
            Score = isEligible ? ResolveCompositeScore(marketScore, strategyScoring.StrategyScore) : 0m,
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

    private async Task UpsertWorkerHeartbeatAsync(
        MarketScannerCycle cycle,
        IReadOnlyCollection<MarketScannerCandidate> candidates,
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

        var noUniverse = cycle.ScannedSymbolCount == 0;
        var noEligible = cycle.ScannedSymbolCount > 0 && cycle.EligibleCandidateCount == 0;

        entity.WorkerName = WorkerName;
        entity.HealthState = noUniverse || noEligible
            ? MonitoringHealthState.Warning
            : MonitoringHealthState.Healthy;
        entity.FreshnessTier = MonitoringFreshnessTier.Hot;
        entity.CircuitBreakerState = noUniverse || noEligible
            ? CircuitBreakerStateCode.HalfOpen
            : CircuitBreakerStateCode.Closed;
        entity.LastHeartbeatAtUtc = nowUtc;
        entity.LastUpdatedAtUtc = nowUtc;
        entity.ConsecutiveFailureCount = 0;
        entity.LastErrorCode = noUniverse
            ? "NoUniverseSymbols"
            : noEligible
                ? "NoEligibleCandidates"
                : null;
        entity.LastErrorMessage = noUniverse
            ? "Market scanner found no universe symbols."
            : noEligible
                ? "Market scanner did not produce eligible candidates."
                : null;
        entity.SnapshotAgeSeconds = 0;
        entity.Detail = Truncate(BuildCycleDetail(cycle, candidates), 2048);
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

    private string? ResolveRejectionReason(
        SymbolMetadataSnapshot? metadata,
        string? quoteAsset,
        SharedMarketDataCacheReadStatus latestPriceReadStatus,
        decimal? latestPrice,
        decimal? quoteVolume,
        HistoricalMarketCandle? latestCandle,
        DateTime nowUtc)
    {
        if (metadata is not null && (!metadata.IsTradingEnabled || !string.Equals(metadata.TradingStatus, "TRADING", StringComparison.OrdinalIgnoreCase)))
        {
            return "SymbolTradingDisabled";
        }

        if (string.IsNullOrWhiteSpace(quoteAsset) || !allowedQuoteAssets.Contains(quoteAsset, StringComparer.Ordinal))
        {
            return "QuoteAssetNotAllowed";
        }

        if (latestPriceReadStatus == SharedMarketDataCacheReadStatus.ProviderUnavailable)
        {
            return "MarketDataProviderUnavailable";
        }

        if (latestPriceReadStatus is SharedMarketDataCacheReadStatus.DeserializeFailed or
            SharedMarketDataCacheReadStatus.InvalidPayload)
        {
            return "MarketDataInvalidPayload";
        }

        if (latestPriceReadStatus == SharedMarketDataCacheReadStatus.HitStale)
        {
            return "StaleMarketData";
        }

        if (!latestPrice.HasValue)
        {
            return "MissingLastPrice";
        }

        if (!quoteVolume.HasValue)
        {
            return "MissingMarketData";
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

        var dataAgeSeconds = (nowUtc - NormalizeUtc(latestCandle.CloseTimeUtc)).TotalSeconds;
        if (dataAgeSeconds > scannerOptionsValue.MaxDataAgeSeconds)
        {
            return "StaleMarketData";
        }

        return null;
    }

    private async Task<MarketScannerStrategyScoreSummary> ResolveStrategyScoringAsync(
        string symbol,
        IReadOnlyCollection<HistoricalMarketCandle> historicalCandles,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (!scannerOptionsValue.HandoffEnabled)
        {
            return MarketScannerStrategyScoreSummary.Accepted(
                0,
                "StrategyScoring=Skipped; Reason=ScannerHandoffDisabled");
        }

        var strategyBinding = await ResolveStrategyBindingAsync(symbol, cancellationToken);
        if (strategyBinding is null)
        {
            return MarketScannerStrategyScoreSummary.Rejected(
                "NoPublishedStrategy",
                $"StrategyScore=n/a; StrategyOutcome=NoPublishedStrategy; Symbol={symbol}; Timeframe={klineInterval}");
        }

        if (indicatorDataService is null || strategyEvaluatorService is null)
        {
            return MarketScannerStrategyScoreSummary.Rejected(
                "StrategyScoringUnavailable",
                $"StrategyScore=n/a; StrategyOutcome=StrategyScoringUnavailable; StrategyKey={strategyBinding.StrategyKey}; Symbol={symbol}; Timeframe={klineInterval}");
        }

        await indicatorDataService.TrackSymbolAsync(symbol, cancellationToken);
        var indicatorSnapshot = await indicatorDataService.GetLatestAsync(symbol, klineInterval, cancellationToken);
        if (indicatorSnapshot is null || indicatorSnapshot.State != IndicatorDataState.Ready)
        {
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

            if (marketCandles.Length > 0)
            {
                indicatorSnapshot = await indicatorDataService.PrimeAsync(symbol, klineInterval, marketCandles, cancellationToken);
            }
        }

        if (indicatorSnapshot is null ||
            indicatorSnapshot.State != IndicatorDataState.Ready ||
            !string.Equals(indicatorSnapshot.Symbol, symbol, StringComparison.Ordinal) ||
            !string.Equals(indicatorSnapshot.Timeframe, klineInterval, StringComparison.Ordinal))
        {
            return MarketScannerStrategyScoreSummary.Rejected(
                "MissingFreshSignalData",
                $"StrategyScore=n/a; StrategyOutcome=MissingFreshSignalData; StrategyKey={strategyBinding.StrategyKey}; Symbol={symbol}; Timeframe={klineInterval}");
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

            var summary = Truncate(
                $"StrategyKey={strategyBinding.StrategyKey}; Template={report.TemplateKey ?? "custom"}; Outcome={report.Outcome}; StrategyScore={report.AggregateScore}; Passed={report.PassedRuleCount}; Failed={report.FailedRuleCount}; {report.ExplainabilitySummary}",
                512);

            if (!string.Equals(report.Outcome, "EntryMatched", StringComparison.Ordinal))
            {
                return MarketScannerStrategyScoreSummary.Rejected(
                    $"Strategy{report.Outcome}",
                    summary ?? $"StrategyKey={strategyBinding.StrategyKey}; Outcome={report.Outcome}; StrategyScore={report.AggregateScore}");
            }

            return MarketScannerStrategyScoreSummary.Accepted(report.AggregateScore, summary);
        }
        catch (StrategyDefinitionValidationException exception)
        {
            return MarketScannerStrategyScoreSummary.Rejected(
                "StrategyDefinitionInvalid",
                Truncate(
                    $"StrategyValidation={exception.StatusCode}; StrategyKey={strategyBinding.StrategyKey}; Symbol={symbol}; Timeframe={klineInterval}",
                    512));
        }
        catch (StrategyRuleParseException)
        {
            return MarketScannerStrategyScoreSummary.Rejected(
                "StrategyParseFailed",
                $"StrategyParseFailed; StrategyKey={strategyBinding.StrategyKey}; Symbol={symbol}; Timeframe={klineInterval}");
        }
        catch (StrategyRuleEvaluationException)
        {
            return MarketScannerStrategyScoreSummary.Rejected(
                "StrategyEvaluationFailed",
                $"StrategyEvaluationFailed; StrategyKey={strategyBinding.StrategyKey}; Symbol={symbol}; Timeframe={klineInterval}");
        }
    }

    private async Task<MarketScannerStrategyBinding?> ResolveStrategyBindingAsync(
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
                entity.Symbol
            })
            .ToListAsync(cancellationToken);

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
                continue;
            }

            var strategyVersion = await dbContext.TradingStrategyVersions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.TradingStrategyId == strategy.Id &&
                    entity.Status == StrategyVersionStatus.Published &&
                    !entity.IsDeleted)
                .OrderByDescending(entity => entity.VersionNumber)
                .Select(entity => new
                {
                    entity.Id,
                    entity.VersionNumber,
                    entity.DefinitionJson
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (strategyVersion is null)
            {
                continue;
            }

            return new MarketScannerStrategyBinding(
                strategy.Id,
                strategyVersion.Id,
                strategyVersion.VersionNumber,
                strategy.StrategyKey,
                string.IsNullOrWhiteSpace(strategy.DisplayName) ? strategy.StrategyKey : strategy.DisplayName,
                strategyVersion.DefinitionJson);
        }

        return null;
    }

    private decimal ResolveCompositeScore(decimal marketScore, int? strategyScore)
    {
        return decimal.Round(
            marketScore + ((strategyScore ?? 0) * Math.Max(scannerOptionsValue.StrategyScoreWeight, 0m)),
            4,
            MidpointRounding.AwayFromZero);
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

        return bestCandidate is null
            ? $"Market scanner evaluated {cycle.ScannedSymbolCount} symbols and found no eligible candidates."
            : $"Market scanner evaluated {cycle.ScannedSymbolCount} symbols and ranked {bestCandidate.Symbol} #{bestCandidate.Rank} with score {bestCandidate.Score.ToString("0.####", CultureInfo.InvariantCulture)}.";
    }

    private static string BuildCycleDetail(MarketScannerCycle cycle, IReadOnlyCollection<MarketScannerCandidate> candidates)
    {
        var rejectedReason = candidates
            .Where(item => !item.IsEligible && !string.IsNullOrWhiteSpace(item.RejectionReason))
            .GroupBy(item => item.RejectionReason!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}:{group.Count()}")
            .FirstOrDefault() ?? "none";

        return $"ScanCycleId={cycle.Id}; UniverseSource={cycle.UniverseSource}; Scanned={cycle.ScannedSymbolCount}; Eligible={cycle.EligibleCandidateCount}; Top={cycle.TopCandidateCount}; BestCandidate={cycle.BestCandidateSymbol ?? "n/a"}; BestScore={cycle.BestCandidateScore?.ToString("0.####", CultureInfo.InvariantCulture) ?? "n/a"}; TopRejectReason={rejectedReason}; CompletedAtUtc={cycle.CompletedAtUtc:O}";
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

    private readonly record struct UniverseSymbolCandidate(string Symbol, string UniverseSource);

    private sealed record MarketScannerStrategyBinding(
        Guid TradingStrategyId,
        Guid TradingStrategyVersionId,
        int VersionNumber,
        string StrategyKey,
        string DisplayName,
        string DefinitionJson);

    private sealed record MarketScannerStrategyScoreSummary(
        int? StrategyScore,
        string? RejectionReason,
        string? ScoringSummary)
    {
        public static MarketScannerStrategyScoreSummary Accepted(int strategyScore, string? scoringSummary) =>
            new(strategyScore, null, scoringSummary);

        public static MarketScannerStrategyScoreSummary Rejected(string rejectionReason, string? scoringSummary) =>
            new(null, rejectionReason, scoringSummary);

        public static MarketScannerStrategyScoreSummary MarketRejected(string rejectionReason, string? scoringSummary) =>
            new(null, rejectionReason, scoringSummary);
    }
}



