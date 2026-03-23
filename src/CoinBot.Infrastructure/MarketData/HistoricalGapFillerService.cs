using System.Globalization;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.MarketData;

public sealed class HistoricalGapFillerService(
    ApplicationDbContext dbContext,
    IBinanceHistoricalKlineClient historicalKlineClient,
    IIndicatorDataService indicatorDataService,
    IOptions<HistoricalGapFillerOptions> options,
    IOptions<BinanceMarketDataOptions> binanceOptions,
    TimeProvider timeProvider,
    ILogger<HistoricalGapFillerService> logger)
{
    private readonly HistoricalGapFillerOptions optionsValue = options.Value;
    private readonly BinanceMarketDataOptions binanceOptionsValue = binanceOptions.Value;

    public Task<IReadOnlyCollection<HistoricalGapRange>> DetectGapsAsync(CancellationToken cancellationToken = default)
    {
        var configuredSymbols = GetConfiguredSymbols();
        return DetectGapsAsync(configuredSymbols, cancellationToken);
    }

    public async Task<HistoricalGapFillRunSummary> BackfillAsync(CancellationToken cancellationToken = default)
    {
        return await BackfillAsync(GetConfiguredSymbols(), cancellationToken);
    }

    public async Task<HistoricalGapFillRunSummary> BackfillAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken cancellationToken = default)
    {
        var configuredSymbols = NormalizeSymbols(symbols);

        if (configuredSymbols.Count == 0)
        {
            logger.LogInformation("Historical gap filler skipped because no symbols are configured.");

            return new HistoricalGapFillRunSummary(
                ScannedSymbolCount: 0,
                DetectedGapCount: 0,
                InsertedCandleCount: 0,
                SkippedDuplicateCount: 0,
                ContinuityVerifiedSymbolCount: 0);
        }

        var gaps = await DetectGapsAsync(configuredSymbols, cancellationToken);
        var insertedCandleCount = 0;
        var skippedDuplicateCount = 0;

        foreach (var gap in gaps)
        {
            var gapResult = await BackfillGapAsync(gap, cancellationToken);
            insertedCandleCount += gapResult.InsertedCandleCount;
            skippedDuplicateCount += gapResult.SkippedDuplicateCount;
        }

        var intervalDescriptor = CandleIntervalDescriptor.Parse(binanceOptionsValue.KlineInterval);
        var (rangeStartOpenTimeUtc, rangeEndOpenTimeUtc) = BuildWindow(intervalDescriptor);
        var verifiedSymbols = 0;

        foreach (var symbol in configuredSymbols)
        {
            await EnsureContinuityAsync(
                symbol,
                intervalDescriptor,
                rangeStartOpenTimeUtc,
                rangeEndOpenTimeUtc,
                cancellationToken);

            verifiedSymbols++;
        }

        return new HistoricalGapFillRunSummary(
            ScannedSymbolCount: configuredSymbols.Count,
            DetectedGapCount: gaps.Count,
            InsertedCandleCount: insertedCandleCount,
            SkippedDuplicateCount: skippedDuplicateCount,
            ContinuityVerifiedSymbolCount: verifiedSymbols);
    }

    public ValueTask<IReadOnlyCollection<string>> GetConfiguredSymbolsSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(GetConfiguredSymbols());
    }

    public async Task<IReadOnlyCollection<MarketCandleSnapshot>> LoadRecentCandlesAsync(
        string symbol,
        string interval,
        int maxCandles,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (maxCandles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCandles), "The candle count must be greater than zero.");
        }

        var normalizedSymbol = MarketDataSymbolNormalizer.Normalize(symbol);
        var normalizedInterval = NormalizeRequired(interval, nameof(interval));
        var entities = await dbContext.HistoricalMarketCandles
            .AsNoTracking()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.Symbol == normalizedSymbol &&
                entity.Interval == normalizedInterval)
            .OrderByDescending(entity => entity.OpenTimeUtc)
            .Take(maxCandles)
            .ToListAsync(cancellationToken);

        entities.Reverse();

        return entities
            .Select(MapMarketCandleSnapshot)
            .ToArray();
    }

    public async Task<HistoricalIndicatorWarmupSummary> WarmIndicatorsAsync(CancellationToken cancellationToken = default)
    {
        return await WarmIndicatorsAsync(GetConfiguredSymbols(), cancellationToken);
    }

    public async Task<HistoricalIndicatorWarmupSummary> WarmIndicatorsAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbols = NormalizeSymbols(symbols);
        var normalizedInterval = NormalizeRequired(binanceOptionsValue.KlineInterval, nameof(binanceOptionsValue.KlineInterval));
        var primedSymbolCount = 0;
        var loadedCandleCount = 0;

        foreach (var symbol in normalizedSymbols)
        {
            var candles = await LoadRecentCandlesAsync(
                symbol,
                normalizedInterval,
                optionsValue.LookbackCandles,
                cancellationToken);

            if (candles.Count == 0)
            {
                continue;
            }

            var snapshot = await indicatorDataService.PrimeAsync(
                symbol,
                normalizedInterval,
                candles,
                cancellationToken);

            if (snapshot is null)
            {
                continue;
            }

            primedSymbolCount++;
            loadedCandleCount += candles.Count;
        }

        return new HistoricalIndicatorWarmupSummary(
            normalizedInterval,
            normalizedSymbols.Count,
            primedSymbolCount,
            loadedCandleCount);
    }

    private async Task<IReadOnlyCollection<HistoricalGapRange>> DetectGapsAsync(
        IReadOnlyCollection<string> configuredSymbols,
        CancellationToken cancellationToken)
    {
        if (configuredSymbols.Count == 0)
        {
            return Array.Empty<HistoricalGapRange>();
        }

        var intervalDescriptor = CandleIntervalDescriptor.Parse(binanceOptionsValue.KlineInterval);
        var (rangeStartOpenTimeUtc, rangeEndOpenTimeUtc) = BuildWindow(intervalDescriptor);
        var gaps = new List<HistoricalGapRange>();

        foreach (var symbol in configuredSymbols)
        {
            var openTimes = await dbContext.HistoricalMarketCandles
                .AsNoTracking()
                .Where(entity =>
                    !entity.IsDeleted &&
                    entity.Symbol == symbol &&
                    entity.Interval == intervalDescriptor.Interval &&
                    entity.OpenTimeUtc >= rangeStartOpenTimeUtc &&
                    entity.OpenTimeUtc <= rangeEndOpenTimeUtc)
                .OrderBy(entity => entity.OpenTimeUtc)
                .Select(entity => entity.OpenTimeUtc)
                .ToListAsync(cancellationToken);

            gaps.AddRange(DetectGapsForSymbol(
                symbol,
                intervalDescriptor,
                rangeStartOpenTimeUtc,
                rangeEndOpenTimeUtc,
                openTimes));
        }

        return gaps
            .OrderBy(gap => gap.Symbol, StringComparer.Ordinal)
            .ThenBy(gap => gap.StartOpenTimeUtc)
            .ToArray();
    }

    private async Task<GapBackfillResult> BackfillGapAsync(
        HistoricalGapRange gap,
        CancellationToken cancellationToken)
    {
        var intervalDescriptor = CandleIntervalDescriptor.Parse(gap.Interval);
        var insertedCandleCount = 0;
        var skippedDuplicateCount = 0;
        var nextOpenTimeUtc = gap.StartOpenTimeUtc;

        logger.LogInformation(
            "Historical gap filler detected {MissingCandleCount} missing candles for {Symbol} {Interval} between {StartOpenTimeUtc:o} and {EndOpenTimeUtc:o}.",
            gap.MissingCandleCount,
            gap.Symbol,
            gap.Interval,
            gap.StartOpenTimeUtc,
            gap.EndOpenTimeUtc);

        while (nextOpenTimeUtc <= gap.EndOpenTimeUtc)
        {
            var remainingCandles = intervalDescriptor.CountCandles(nextOpenTimeUtc, gap.EndOpenTimeUtc);
            var batchSize = Math.Min(optionsValue.MaxCandlesPerRequest, remainingCandles);
            var batchEndOpenTimeUtc = intervalDescriptor.AddSteps(nextOpenTimeUtc, batchSize - 1);

            if (batchEndOpenTimeUtc > gap.EndOpenTimeUtc)
            {
                batchEndOpenTimeUtc = gap.EndOpenTimeUtc;
            }

            var snapshots = await GetClosedCandlesWithRetryAsync(
                gap.Symbol,
                gap.Interval,
                nextOpenTimeUtc,
                batchEndOpenTimeUtc,
                batchSize,
                cancellationToken);

            if (snapshots.Count == 0)
            {
                logger.LogWarning(
                    "Historical gap filler received no candles for {Symbol} {Interval} between {StartOpenTimeUtc:o} and {EndOpenTimeUtc:o}.",
                    gap.Symbol,
                    gap.Interval,
                    nextOpenTimeUtc,
                    batchEndOpenTimeUtc);

                break;
            }

            var deduplicationResult = DeduplicateSnapshots(
                snapshots,
                nextOpenTimeUtc,
                batchEndOpenTimeUtc);
            skippedDuplicateCount += deduplicationResult.DuplicateCount;

            if (deduplicationResult.Snapshots.Count == 0)
            {
                logger.LogWarning(
                    "Historical gap filler could not map any usable candles for {Symbol} {Interval} between {StartOpenTimeUtc:o} and {EndOpenTimeUtc:o}.",
                    gap.Symbol,
                    gap.Interval,
                    nextOpenTimeUtc,
                    batchEndOpenTimeUtc);

                break;
            }

            var firstSnapshot = deduplicationResult.Snapshots[0];
            var lastSnapshot = deduplicationResult.Snapshots[deduplicationResult.Snapshots.Count - 1];

            var existingOpenTimes = await dbContext.HistoricalMarketCandles
                .Where(entity =>
                    !entity.IsDeleted &&
                    entity.Symbol == gap.Symbol &&
                    entity.Interval == gap.Interval &&
                    entity.OpenTimeUtc >= firstSnapshot.OpenTimeUtc &&
                    entity.OpenTimeUtc <= lastSnapshot.OpenTimeUtc)
                .Select(entity => entity.OpenTimeUtc)
                .ToListAsync(cancellationToken);
            var existingOpenTimeSet = existingOpenTimes.ToHashSet();

            foreach (var snapshot in deduplicationResult.Snapshots)
            {
                if (!existingOpenTimeSet.Add(snapshot.OpenTimeUtc))
                {
                    skippedDuplicateCount++;
                    continue;
                }

                dbContext.HistoricalMarketCandles.Add(MapHistoricalMarketCandle(snapshot));
                insertedCandleCount++;
            }

            if (dbContext.ChangeTracker.Entries<HistoricalMarketCandle>().Any(entry => entry.State == EntityState.Added))
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            nextOpenTimeUtc = intervalDescriptor.AddSteps(lastSnapshot.OpenTimeUtc, 1);
        }

        return new GapBackfillResult(insertedCandleCount, skippedDuplicateCount);
    }

    private async Task<IReadOnlyCollection<MarketCandleSnapshot>> GetClosedCandlesWithRetryAsync(
        string symbol,
        string interval,
        DateTime startOpenTimeUtc,
        DateTime endOpenTimeUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        var maxAttempts = optionsValue.MaxRetryAttempts + 1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await historicalKlineClient.GetClosedCandlesAsync(
                    symbol,
                    interval,
                    startOpenTimeUtc,
                    endOpenTimeUtc,
                    limit,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < maxAttempts)
            {
                logger.LogWarning(
                    "Historical gap filler retrying canceled Binance REST request for {Symbol} {Interval}. Attempt {Attempt} of {MaxAttempts}.",
                    symbol,
                    interval,
                    attempt,
                    maxAttempts);
            }
            catch (Exception exception) when (attempt < maxAttempts)
            {
                logger.LogWarning(
                    exception,
                    "Historical gap filler Binance REST request failed for {Symbol} {Interval}. Attempt {Attempt} of {MaxAttempts}.",
                    symbol,
                    interval,
                    attempt,
                    maxAttempts);
            }

            await Task.Delay(TimeSpan.FromSeconds(optionsValue.RetryDelaySeconds), cancellationToken);
        }

        return await historicalKlineClient.GetClosedCandlesAsync(
            symbol,
            interval,
            startOpenTimeUtc,
            endOpenTimeUtc,
            limit,
            cancellationToken);
    }

    private async Task EnsureContinuityAsync(
        string symbol,
        CandleIntervalDescriptor intervalDescriptor,
        DateTime rangeStartOpenTimeUtc,
        DateTime rangeEndOpenTimeUtc,
        CancellationToken cancellationToken)
    {
        var openTimes = await dbContext.HistoricalMarketCandles
            .AsNoTracking()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.Symbol == symbol &&
                entity.Interval == intervalDescriptor.Interval &&
                entity.OpenTimeUtc >= rangeStartOpenTimeUtc &&
                entity.OpenTimeUtc <= rangeEndOpenTimeUtc)
            .OrderBy(entity => entity.OpenTimeUtc)
            .Select(entity => entity.OpenTimeUtc)
            .ToListAsync(cancellationToken);
        var remainingGaps = DetectGapsForSymbol(
                symbol,
                intervalDescriptor,
                rangeStartOpenTimeUtc,
                rangeEndOpenTimeUtc,
                openTimes)
            .ToArray();

        if (remainingGaps.Length == 0)
        {
            logger.LogInformation(
                "Historical gap filler verified continuity for {Symbol} {Interval} between {StartOpenTimeUtc:o} and {EndOpenTimeUtc:o}.",
                symbol,
                intervalDescriptor.Interval,
                rangeStartOpenTimeUtc,
                rangeEndOpenTimeUtc);

            return;
        }

        logger.LogWarning(
            "Historical gap filler could not restore continuity for {Symbol} {Interval}. Remaining gap count: {GapCount}.",
            symbol,
            intervalDescriptor.Interval,
            remainingGaps.Length);

        throw new InvalidOperationException(
            $"Historical gap filler could not restore continuity for {symbol} {intervalDescriptor.Interval}.");
    }

    private IReadOnlyCollection<string> GetConfiguredSymbols()
    {
        var symbols = optionsValue.Symbols.Length > 0
            ? optionsValue.Symbols
            : binanceOptionsValue.SeedSymbols;

        return symbols.Length == 0
            ? Array.Empty<string>()
            : MarketDataSymbolNormalizer.NormalizeMany(symbols);
    }

    private (DateTime RangeStartOpenTimeUtc, DateTime RangeEndOpenTimeUtc) BuildWindow(CandleIntervalDescriptor intervalDescriptor)
    {
        var latestClosedOpenTimeUtc = intervalDescriptor.GetLatestClosedOpenTimeUtc(timeProvider.GetUtcNow().UtcDateTime);

        if (latestClosedOpenTimeUtc == DateTime.MinValue)
        {
            throw new InvalidOperationException("Historical gap filler requires a valid latest closed candle boundary.");
        }

        var rangeStartOpenTimeUtc = intervalDescriptor.AddSteps(
            latestClosedOpenTimeUtc,
            -(optionsValue.LookbackCandles - 1));

        return (rangeStartOpenTimeUtc, latestClosedOpenTimeUtc);
    }

    private static IReadOnlyCollection<HistoricalGapRange> DetectGapsForSymbol(
        string symbol,
        CandleIntervalDescriptor intervalDescriptor,
        DateTime rangeStartOpenTimeUtc,
        DateTime rangeEndOpenTimeUtc,
        IReadOnlyList<DateTime> openTimes)
    {
        var gaps = new List<HistoricalGapRange>();

        if (openTimes.Count == 0)
        {
            gaps.Add(new HistoricalGapRange(
                symbol,
                intervalDescriptor.Interval,
                rangeStartOpenTimeUtc,
                rangeEndOpenTimeUtc,
                intervalDescriptor.CountCandles(rangeStartOpenTimeUtc, rangeEndOpenTimeUtc)));

            return gaps;
        }

        var expectedOpenTimeUtc = rangeStartOpenTimeUtc;

        foreach (var openTimeUtc in openTimes)
        {
            if (openTimeUtc > expectedOpenTimeUtc)
            {
                var gapEndOpenTimeUtc = intervalDescriptor.AddSteps(openTimeUtc, -1);
                gaps.Add(new HistoricalGapRange(
                    symbol,
                    intervalDescriptor.Interval,
                    expectedOpenTimeUtc,
                    gapEndOpenTimeUtc,
                    intervalDescriptor.CountCandles(expectedOpenTimeUtc, gapEndOpenTimeUtc)));
            }

            expectedOpenTimeUtc = intervalDescriptor.AddSteps(openTimeUtc, 1);
        }

        if (expectedOpenTimeUtc <= rangeEndOpenTimeUtc)
        {
            gaps.Add(new HistoricalGapRange(
                symbol,
                intervalDescriptor.Interval,
                expectedOpenTimeUtc,
                rangeEndOpenTimeUtc,
                intervalDescriptor.CountCandles(expectedOpenTimeUtc, rangeEndOpenTimeUtc)));
        }

        return gaps;
    }

    private static DeduplicationResult DeduplicateSnapshots(
        IReadOnlyCollection<MarketCandleSnapshot> snapshots,
        DateTime startOpenTimeUtc,
        DateTime endOpenTimeUtc)
    {
        var uniqueSnapshots = new SortedDictionary<DateTime, MarketCandleSnapshot>();
        var duplicateCount = 0;

        foreach (var snapshot in snapshots.OrderBy(item => item.OpenTimeUtc))
        {
            if (!snapshot.IsClosed ||
                snapshot.OpenTimeUtc < startOpenTimeUtc ||
                snapshot.OpenTimeUtc > endOpenTimeUtc)
            {
                continue;
            }

            if (!uniqueSnapshots.TryAdd(snapshot.OpenTimeUtc, snapshot))
            {
                duplicateCount++;
            }
        }

        return new DeduplicationResult(uniqueSnapshots.Values.ToArray(), duplicateCount);
    }

    private static HistoricalMarketCandle MapHistoricalMarketCandle(MarketCandleSnapshot snapshot)
    {
        return new HistoricalMarketCandle
        {
            Symbol = MarketDataSymbolNormalizer.Normalize(snapshot.Symbol),
            Interval = snapshot.Interval.Trim(),
            OpenTimeUtc = NormalizeTimestamp(snapshot.OpenTimeUtc),
            CloseTimeUtc = NormalizeTimestamp(snapshot.CloseTimeUtc),
            OpenPrice = snapshot.OpenPrice,
            HighPrice = snapshot.HighPrice,
            LowPrice = snapshot.LowPrice,
            ClosePrice = snapshot.ClosePrice,
            Volume = snapshot.Volume,
            ReceivedAtUtc = NormalizeTimestamp(snapshot.ReceivedAtUtc),
            Source = snapshot.Source.Trim()
        };
    }

    private static MarketCandleSnapshot MapMarketCandleSnapshot(HistoricalMarketCandle entity)
    {
        return new MarketCandleSnapshot(
            entity.Symbol,
            entity.Interval,
            NormalizeTimestamp(entity.OpenTimeUtc),
            NormalizeTimestamp(entity.CloseTimeUtc),
            entity.OpenPrice,
            entity.HighPrice,
            entity.LowPrice,
            entity.ClosePrice,
            entity.Volume,
            IsClosed: true,
            NormalizeTimestamp(entity.ReceivedAtUtc),
            entity.Source);
    }

    private static IReadOnlyCollection<string> NormalizeSymbols(IReadOnlyCollection<string> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        return symbols.Count == 0
            ? Array.Empty<string>()
            : MarketDataSymbolNormalizer.NormalizeMany(symbols);
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

    private static DateTime NormalizeTimestamp(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private sealed record GapBackfillResult(
        int InsertedCandleCount,
        int SkippedDuplicateCount);

    private sealed record DeduplicationResult(
        IReadOnlyList<MarketCandleSnapshot> Snapshots,
        int DuplicateCount);

    private sealed record CandleIntervalDescriptor(
        string Interval,
        int Magnitude,
        char Unit)
    {
        public static CandleIntervalDescriptor Parse(string interval)
        {
            var normalizedInterval = interval?.Trim();

            if (string.IsNullOrWhiteSpace(normalizedInterval) || normalizedInterval.Length < 2)
            {
                throw new InvalidOperationException($"Unsupported candle interval '{interval}'.");
            }

            var magnitudeText = normalizedInterval[..^1];
            var unit = normalizedInterval[^1];

            if (!int.TryParse(magnitudeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var magnitude) ||
                magnitude <= 0)
            {
                throw new InvalidOperationException($"Unsupported candle interval '{interval}'.");
            }

            return unit switch
            {
                'm' or 'h' or 'd' or 'w' or 'M' => new CandleIntervalDescriptor(normalizedInterval, magnitude, unit),
                _ => throw new InvalidOperationException($"Unsupported candle interval '{interval}'.")
            };
        }

        public DateTime AddSteps(DateTime value, int stepCount)
        {
            if (stepCount == 0)
            {
                return NormalizeTimestamp(value);
            }

            var normalizedValue = NormalizeTimestamp(value);

            return Unit switch
            {
                'm' => normalizedValue.AddMinutes((double)Magnitude * stepCount),
                'h' => normalizedValue.AddHours((double)Magnitude * stepCount),
                'd' => normalizedValue.AddDays((double)Magnitude * stepCount),
                'w' => normalizedValue.AddDays(Magnitude * 7d * stepCount),
                'M' => normalizedValue.AddMonths(Magnitude * stepCount),
                _ => throw new InvalidOperationException($"Unsupported candle interval '{Interval}'.")
            };
        }

        public int CountCandles(DateTime startOpenTimeUtc, DateTime endOpenTimeUtc)
        {
            var normalizedStartOpenTimeUtc = NormalizeTimestamp(startOpenTimeUtc);
            var normalizedEndOpenTimeUtc = NormalizeTimestamp(endOpenTimeUtc);

            if (normalizedEndOpenTimeUtc < normalizedStartOpenTimeUtc)
            {
                return 0;
            }

            var count = 0;

            for (var cursor = normalizedStartOpenTimeUtc; cursor <= normalizedEndOpenTimeUtc; cursor = AddSteps(cursor, 1))
            {
                count++;
            }

            return count;
        }

        public DateTime GetLatestClosedOpenTimeUtc(DateTime value)
        {
            var normalizedValue = NormalizeTimestamp(value);
            var currentBoundaryUtc = GetCurrentBoundaryOpenTimeUtc(normalizedValue);
            var latestClosedOpenTimeUtc = AddSteps(currentBoundaryUtc, -1);

            return latestClosedOpenTimeUtc >= DateTime.MinValue
                ? latestClosedOpenTimeUtc
                : DateTime.MinValue;
        }

        private DateTime GetCurrentBoundaryOpenTimeUtc(DateTime value)
        {
            return Unit switch
            {
                'm' => new DateTime(
                    value.Year,
                    value.Month,
                    value.Day,
                    value.Hour,
                    value.Minute - (value.Minute % Magnitude),
                    0,
                    DateTimeKind.Utc),
                'h' => new DateTime(
                    value.Year,
                    value.Month,
                    value.Day,
                    value.Hour - (value.Hour % Magnitude),
                    0,
                    0,
                    DateTimeKind.Utc),
                'd' => new DateTime(
                    value.Year,
                    value.Month,
                    value.Day,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc),
                'w' => AlignWeekBoundary(value),
                'M' => new DateTime(
                    value.Year,
                    value.Month,
                    1,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc),
                _ => throw new InvalidOperationException($"Unsupported candle interval '{Interval}'.")
            };
        }

        private DateTime AlignWeekBoundary(DateTime value)
        {
            var utcDate = value.Date;
            var offset = ((int)utcDate.DayOfWeek + 6) % 7;
            var monday = utcDate.AddDays(-offset);
            return new DateTime(monday.Year, monday.Month, monday.Day, 0, 0, 0, DateTimeKind.Utc);
        }
    }
}
