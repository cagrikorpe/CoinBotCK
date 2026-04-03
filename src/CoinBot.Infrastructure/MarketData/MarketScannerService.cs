using System.Globalization;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
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
    MarketScannerHandoffService? handoffService = null)
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
        var latestPriceSnapshot = await marketDataService.GetLatestPriceAsync(universeCandidate.Symbol, cancellationToken);
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
        var rejectionReason = ResolveRejectionReason(
            metadata,
            quoteAsset,
            latestPrice,
            quoteVolume,
            latestCandle,
            nowUtc);
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
            IsEligible = isEligible,
            RejectionReason = rejectionReason,
            Score = isEligible ? quoteVolume!.Value : 0m,
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
}



