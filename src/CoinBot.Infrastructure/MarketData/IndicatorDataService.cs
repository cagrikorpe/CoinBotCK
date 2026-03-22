using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.MarketData;

public sealed class IndicatorDataService(
    IMarketDataService marketDataService,
    IndicatorStreamHub streamHub,
    IOptions<IndicatorEngineOptions> options,
    ILogger<IndicatorDataService> logger) : IIndicatorDataService
{
    private const string IndicatorSourceName = "CoinBot.IndicatorEngine";
    private readonly IndicatorEngineOptions optionsValue = options.Value;
    private readonly ConcurrentDictionary<string, IndicatorSeriesState> seriesStates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StrategyIndicatorSnapshot> latestSnapshots = new(StringComparer.Ordinal);

    public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default)
    {
        return marketDataService.TrackSymbolAsync(symbol, cancellationToken);
    }

    public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
    {
        return marketDataService.TrackSymbolsAsync(symbols, cancellationToken);
    }

    public ValueTask<StrategyIndicatorSnapshot?> GetLatestAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = CreateKey(
            MarketDataSymbolNormalizer.Normalize(symbol),
            NormalizeTimeframe(timeframe));

        latestSnapshots.TryGetValue(key, out StrategyIndicatorSnapshot? snapshot);
        return ValueTask.FromResult(snapshot);
    }

    public async IAsyncEnumerable<StrategyIndicatorSnapshot> WatchAsync(
        IEnumerable<IndicatorSubscription> subscriptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var normalizedSubscriptions = NormalizeSubscriptions(subscriptions);

        if (normalizedSubscriptions.Count > 0)
        {
            await TrackSymbolsAsync(
                normalizedSubscriptions
                    .Select(subscription => subscription.Symbol)
                    .Distinct(StringComparer.Ordinal),
                cancellationToken);
        }

        await foreach (var snapshot in streamHub.SubscribeAsync(normalizedSubscriptions, cancellationToken))
        {
            yield return snapshot;
        }
    }

    internal ValueTask RecordAcceptedCandleAsync(
        MarketCandleSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSnapshot = Normalize(snapshot);
        var key = CreateKey(normalizedSnapshot.Symbol, normalizedSnapshot.Interval);
        var seriesState = seriesStates.GetOrAdd(key, _ => new IndicatorSeriesState(optionsValue));
        var indicatorSnapshot = seriesState.Advance(normalizedSnapshot);

        latestSnapshots[key] = indicatorSnapshot;
        streamHub.Publish(indicatorSnapshot);

        logger.LogDebug(
            "Central indicator engine updated {Symbol} {Timeframe} with state {State}.",
            indicatorSnapshot.Symbol,
            indicatorSnapshot.Timeframe,
            indicatorSnapshot.State);

        return ValueTask.CompletedTask;
    }

    internal ValueTask RecordRejectedCandleAsync(
        MarketCandleSnapshot snapshot,
        CandleDataQualityGuardResult guardResult,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(guardResult);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSnapshot = Normalize(snapshot);
        var key = CreateKey(normalizedSnapshot.Symbol, normalizedSnapshot.Interval);
        var indicatorSnapshot = seriesStates.TryGetValue(key, out var seriesState)
            ? seriesState.MarkMissingData(normalizedSnapshot, guardResult.GuardReasonCode)
            : IndicatorSeriesState.CreateInitialMissingDataSnapshot(normalizedSnapshot, guardResult.GuardReasonCode, optionsValue);

        latestSnapshots[key] = indicatorSnapshot;
        streamHub.Publish(indicatorSnapshot);

        logger.LogDebug(
            "Central indicator engine moved {Symbol} {Timeframe} to {State} because of {ReasonCode}.",
            indicatorSnapshot.Symbol,
            indicatorSnapshot.Timeframe,
            indicatorSnapshot.State,
            indicatorSnapshot.DataQualityReasonCode);

        return ValueTask.CompletedTask;
    }

    private static IReadOnlyCollection<IndicatorSubscription> NormalizeSubscriptions(IEnumerable<IndicatorSubscription> subscriptions)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);

        return subscriptions
            .Select(NormalizeSubscription)
            .Distinct()
            .ToArray();
    }

    private static IndicatorSubscription NormalizeSubscription(IndicatorSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        return subscription with
        {
            Symbol = MarketDataSymbolNormalizer.Normalize(subscription.Symbol),
            Timeframe = NormalizeTimeframe(subscription.Timeframe)
        };
    }

    private static MarketCandleSnapshot Normalize(MarketCandleSnapshot snapshot)
    {
        return snapshot with
        {
            Symbol = MarketDataSymbolNormalizer.Normalize(snapshot.Symbol),
            Interval = NormalizeTimeframe(snapshot.Interval),
            OpenTimeUtc = NormalizeTimestamp(snapshot.OpenTimeUtc),
            CloseTimeUtc = NormalizeTimestamp(snapshot.CloseTimeUtc),
            ReceivedAtUtc = NormalizeTimestamp(snapshot.ReceivedAtUtc),
            Source = snapshot.Source.Trim()
        };
    }

    private static string NormalizeTimeframe(string timeframe)
    {
        var normalizedTimeframe = timeframe?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedTimeframe))
        {
            throw new ArgumentException("The timeframe is required.", nameof(timeframe));
        }

        return normalizedTimeframe;
    }

    private static string CreateKey(string symbol, string timeframe)
    {
        return $"{symbol}:{timeframe}";
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

    private static decimal? ToDecimal(double? value)
    {
        return value is null ? null : (decimal)value.Value;
    }

    private sealed class IndicatorSeriesState
    {
        private readonly object syncRoot = new();
        private readonly RelativeStrengthIndexCalculator rsiCalculator;
        private readonly MovingAverageConvergenceDivergenceCalculator macdCalculator;
        private readonly BollingerBandsCalculator bollingerCalculator;
        private readonly int requiredSampleCount;
        private int sampleCount;

        public IndicatorSeriesState(IndicatorEngineOptions options)
        {
            requiredSampleCount = options.GetRequiredSampleCount();
            rsiCalculator = new RelativeStrengthIndexCalculator(options.RsiPeriod);
            macdCalculator = new MovingAverageConvergenceDivergenceCalculator(
                options.MacdFastPeriod,
                options.MacdSlowPeriod,
                options.MacdSignalPeriod);
            bollingerCalculator = new BollingerBandsCalculator(
                options.BollingerPeriod,
                options.BollingerStandardDeviationMultiplier);
        }

        public StrategyIndicatorSnapshot Advance(MarketCandleSnapshot snapshot)
        {
            lock (syncRoot)
            {
                sampleCount++;

                var rsi = rsiCalculator.Advance(snapshot.ClosePrice);
                var macd = macdCalculator.Advance(snapshot.ClosePrice);
                var bollinger = bollingerCalculator.Advance(snapshot.ClosePrice);
                var state = rsi.IsReady && macd.IsReady && bollinger.IsReady
                    ? IndicatorDataState.Ready
                    : IndicatorDataState.WarmingUp;

                return CreateSnapshot(
                    snapshot,
                    state,
                    DegradedModeReasonCode.None,
                    rsi,
                    macd,
                    bollinger);
            }
        }

        public StrategyIndicatorSnapshot MarkMissingData(
            MarketCandleSnapshot snapshot,
            DegradedModeReasonCode reasonCode)
        {
            lock (syncRoot)
            {
                return CreateSnapshot(
                    snapshot,
                    IndicatorDataState.MissingData,
                    reasonCode,
                    rsiCalculator.CreateUnavailableSnapshot(),
                    macdCalculator.CreateUnavailableSnapshot(),
                    bollingerCalculator.CreateUnavailableSnapshot());
            }
        }

        public static StrategyIndicatorSnapshot CreateInitialMissingDataSnapshot(
            MarketCandleSnapshot snapshot,
            DegradedModeReasonCode reasonCode,
            IndicatorEngineOptions options)
        {
            var rsi = new RelativeStrengthIndexCalculator(options.RsiPeriod).CreateUnavailableSnapshot();
            var macd = new MovingAverageConvergenceDivergenceCalculator(
                options.MacdFastPeriod,
                options.MacdSlowPeriod,
                options.MacdSignalPeriod).CreateUnavailableSnapshot();
            var bollinger = new BollingerBandsCalculator(
                options.BollingerPeriod,
                options.BollingerStandardDeviationMultiplier).CreateUnavailableSnapshot();

            return new StrategyIndicatorSnapshot(
                snapshot.Symbol,
                snapshot.Interval,
                snapshot.OpenTimeUtc,
                snapshot.CloseTimeUtc,
                snapshot.ReceivedAtUtc,
                SampleCount: 0,
                RequiredSampleCount: options.GetRequiredSampleCount(),
                State: IndicatorDataState.MissingData,
                DataQualityReasonCode: reasonCode,
                Rsi: rsi,
                Macd: macd,
                Bollinger: bollinger,
                Source: IndicatorSourceName);
        }

        private StrategyIndicatorSnapshot CreateSnapshot(
            MarketCandleSnapshot snapshot,
            IndicatorDataState state,
            DegradedModeReasonCode reasonCode,
            RelativeStrengthIndexSnapshot rsi,
            MovingAverageConvergenceDivergenceSnapshot macd,
            BollingerBandsSnapshot bollinger)
        {
            return new StrategyIndicatorSnapshot(
                snapshot.Symbol,
                snapshot.Interval,
                snapshot.OpenTimeUtc,
                snapshot.CloseTimeUtc,
                snapshot.ReceivedAtUtc,
                sampleCount,
                requiredSampleCount,
                state,
                reasonCode,
                rsi,
                macd,
                bollinger,
                IndicatorSourceName);
        }
    }

    private sealed class RelativeStrengthIndexCalculator(int period)
    {
        private double? previousClose;
        private int changeCount;
        private double accumulatedGain;
        private double accumulatedLoss;
        private double averageGain;
        private double averageLoss;
        private bool isReady;

        public RelativeStrengthIndexSnapshot Advance(decimal closePrice)
        {
            var closeValue = (double)closePrice;

            if (previousClose is null)
            {
                previousClose = closeValue;
                return CreateUnavailableSnapshot();
            }

            var change = closeValue - previousClose.Value;
            var gain = Math.Max(change, 0d);
            var loss = Math.Max(-change, 0d);

            previousClose = closeValue;

            if (!isReady)
            {
                accumulatedGain += gain;
                accumulatedLoss += loss;
                changeCount++;

                if (changeCount < period)
                {
                    return CreateUnavailableSnapshot();
                }

                averageGain = accumulatedGain / period;
                averageLoss = accumulatedLoss / period;
                isReady = true;
            }
            else
            {
                averageGain = ((averageGain * (period - 1)) + gain) / period;
                averageLoss = ((averageLoss * (period - 1)) + loss) / period;
            }

            return new RelativeStrengthIndexSnapshot(
                period,
                IsReady: true,
                Value: ToDecimal(ComputeRsi(averageGain, averageLoss)));
        }

        public RelativeStrengthIndexSnapshot CreateUnavailableSnapshot()
        {
            return new RelativeStrengthIndexSnapshot(
                period,
                IsReady: false,
                Value: null);
        }

        private static double ComputeRsi(double averageGain, double averageLoss)
        {
            if (averageGain == 0d && averageLoss == 0d)
            {
                return 50d;
            }

            if (averageLoss == 0d)
            {
                return 100d;
            }

            if (averageGain == 0d)
            {
                return 0d;
            }

            var relativeStrength = averageGain / averageLoss;
            return 100d - (100d / (1d + relativeStrength));
        }
    }

    private sealed class MovingAverageConvergenceDivergenceCalculator(
        int fastPeriod,
        int slowPeriod,
        int signalPeriod)
    {
        private readonly ExponentialMovingAverageCalculator fastEma = new(fastPeriod);
        private readonly ExponentialMovingAverageCalculator slowEma = new(slowPeriod);
        private readonly ExponentialMovingAverageCalculator signalEma = new(signalPeriod);

        public MovingAverageConvergenceDivergenceSnapshot Advance(decimal closePrice)
        {
            var closeValue = (double)closePrice;
            var fastValue = fastEma.Advance(closeValue);
            var slowValue = slowEma.Advance(closeValue);

            if (fastValue is null || slowValue is null)
            {
                return CreateUnavailableSnapshot();
            }

            var macdLine = fastValue.Value - slowValue.Value;
            var signalLine = signalEma.Advance(macdLine);

            if (signalLine is null)
            {
                return CreateUnavailableSnapshot();
            }

            var histogram = macdLine - signalLine.Value;

            return new MovingAverageConvergenceDivergenceSnapshot(
                fastPeriod,
                slowPeriod,
                signalPeriod,
                IsReady: true,
                MacdLine: ToDecimal(macdLine),
                SignalLine: ToDecimal(signalLine.Value),
                Histogram: ToDecimal(histogram));
        }

        public MovingAverageConvergenceDivergenceSnapshot CreateUnavailableSnapshot()
        {
            return new MovingAverageConvergenceDivergenceSnapshot(
                fastPeriod,
                slowPeriod,
                signalPeriod,
                IsReady: false,
                MacdLine: null,
                SignalLine: null,
                Histogram: null);
        }
    }

    private sealed class BollingerBandsCalculator(int period, decimal standardDeviationMultiplier)
    {
        private readonly Queue<double> window = new();
        private readonly double multiplier = (double)standardDeviationMultiplier;
        private double sum;
        private double sumSquares;

        public BollingerBandsSnapshot Advance(decimal closePrice)
        {
            var closeValue = (double)closePrice;
            window.Enqueue(closeValue);
            sum += closeValue;
            sumSquares += closeValue * closeValue;

            if (window.Count > period)
            {
                var removedValue = window.Dequeue();
                sum -= removedValue;
                sumSquares -= removedValue * removedValue;
            }

            if (window.Count < period)
            {
                return CreateUnavailableSnapshot();
            }

            var middleBand = sum / period;
            var variance = Math.Max(0d, (sumSquares / period) - (middleBand * middleBand));
            var standardDeviation = Math.Sqrt(variance);

            return new BollingerBandsSnapshot(
                period,
                standardDeviationMultiplier,
                IsReady: true,
                MiddleBand: ToDecimal(middleBand),
                UpperBand: ToDecimal(middleBand + (multiplier * standardDeviation)),
                LowerBand: ToDecimal(middleBand - (multiplier * standardDeviation)),
                StandardDeviation: ToDecimal(standardDeviation));
        }

        public BollingerBandsSnapshot CreateUnavailableSnapshot()
        {
            return new BollingerBandsSnapshot(
                period,
                standardDeviationMultiplier,
                IsReady: false,
                MiddleBand: null,
                UpperBand: null,
                LowerBand: null,
                StandardDeviation: null);
        }
    }

    private sealed class ExponentialMovingAverageCalculator(int period)
    {
        private readonly double smoothingFactor = 2d / (period + 1d);
        private int sampleCount;
        private double accumulatedSum;
        private double? currentValue;

        public double? Advance(double sample)
        {
            if (currentValue is null)
            {
                sampleCount++;
                accumulatedSum += sample;

                if (sampleCount < period)
                {
                    return null;
                }

                currentValue = accumulatedSum / period;
                return currentValue;
            }

            currentValue = ((sample - currentValue.Value) * smoothingFactor) + currentValue.Value;
            return currentValue;
        }
    }
}
