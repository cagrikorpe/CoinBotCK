using System.Collections.Concurrent;
using System.Globalization;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.MarketData;

public sealed class CandleContinuityValidator(ILogger<CandleContinuityValidator> logger)
{
    private readonly ConcurrentDictionary<string, ClosedCandleCursor> cursors = new(StringComparer.Ordinal);

    public CandleContinuityValidationResult Validate(MarketCandleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var normalizedSnapshot = Normalize(snapshot);
        var cursorKey = CreateCursorKey(normalizedSnapshot.Symbol, normalizedSnapshot.Interval);

        if (!cursors.TryGetValue(cursorKey, out var previousCursor))
        {
            var initialCursor = ClosedCandleCursor.FromSnapshot(normalizedSnapshot);
            cursors[cursorKey] = initialCursor;

            return new CandleContinuityValidationResult(
                IsAccepted: true,
                GuardStateCode: DegradedModeStateCode.Normal,
                GuardReasonCode: DegradedModeReasonCode.None,
                EffectiveDataTimestampUtc: normalizedSnapshot.CloseTimeUtc,
                ExpectedOpenTimeUtc: null);
        }

        if (normalizedSnapshot.OpenTimeUtc == previousCursor.OpenTimeUtc)
        {
            logger.LogWarning(
                "Candle continuity validator detected duplicate closed candle for {Symbol} {Interval}.",
                normalizedSnapshot.Symbol,
                normalizedSnapshot.Interval);

            return CreateRejectedResult(
                previousCursor,
                DegradedModeReasonCode.CandleDataDuplicateDetected,
                expectedOpenTimeUtc: CalculateExpectedNextOpenTimeUtc(previousCursor.OpenTimeUtc, normalizedSnapshot.Interval));
        }

        if (normalizedSnapshot.OpenTimeUtc < previousCursor.OpenTimeUtc)
        {
            logger.LogWarning(
                "Candle continuity validator detected out-of-order candle for {Symbol} {Interval}. Expected a candle after {PreviousOpenTimeUtc:o} but received {CurrentOpenTimeUtc:o}.",
                normalizedSnapshot.Symbol,
                normalizedSnapshot.Interval,
                previousCursor.OpenTimeUtc,
                normalizedSnapshot.OpenTimeUtc);

            return CreateRejectedResult(
                previousCursor,
                DegradedModeReasonCode.CandleDataOutOfOrderDetected,
                expectedOpenTimeUtc: CalculateExpectedNextOpenTimeUtc(previousCursor.OpenTimeUtc, normalizedSnapshot.Interval));
        }

        var expectedNextOpenTimeUtc = CalculateExpectedNextOpenTimeUtc(previousCursor.OpenTimeUtc, normalizedSnapshot.Interval);

        if (normalizedSnapshot.OpenTimeUtc > expectedNextOpenTimeUtc)
        {
            logger.LogWarning(
                "Candle continuity validator detected candle gap for {Symbol} {Interval}. Expected {ExpectedOpenTimeUtc:o} but received {CurrentOpenTimeUtc:o}.",
                normalizedSnapshot.Symbol,
                normalizedSnapshot.Interval,
                expectedNextOpenTimeUtc,
                normalizedSnapshot.OpenTimeUtc);

            return CreateRejectedResult(
                previousCursor,
                DegradedModeReasonCode.CandleDataGapDetected,
                expectedOpenTimeUtc: expectedNextOpenTimeUtc);
        }

        if (normalizedSnapshot.OpenTimeUtc < expectedNextOpenTimeUtc)
        {
            logger.LogWarning(
                "Candle continuity validator detected overlapping candle order for {Symbol} {Interval}. Expected {ExpectedOpenTimeUtc:o} but received {CurrentOpenTimeUtc:o}.",
                normalizedSnapshot.Symbol,
                normalizedSnapshot.Interval,
                expectedNextOpenTimeUtc,
                normalizedSnapshot.OpenTimeUtc);

            return CreateRejectedResult(
                previousCursor,
                DegradedModeReasonCode.CandleDataOutOfOrderDetected,
                expectedOpenTimeUtc: expectedNextOpenTimeUtc);
        }

        cursors[cursorKey] = ClosedCandleCursor.FromSnapshot(normalizedSnapshot);

        return new CandleContinuityValidationResult(
            IsAccepted: true,
            GuardStateCode: DegradedModeStateCode.Normal,
            GuardReasonCode: DegradedModeReasonCode.None,
            EffectiveDataTimestampUtc: normalizedSnapshot.CloseTimeUtc,
            ExpectedOpenTimeUtc: expectedNextOpenTimeUtc);
    }

    private static CandleContinuityValidationResult CreateRejectedResult(
        ClosedCandleCursor previousCursor,
        DegradedModeReasonCode guardReasonCode,
        DateTime? expectedOpenTimeUtc)
    {
        return new CandleContinuityValidationResult(
            IsAccepted: false,
            GuardStateCode: DegradedModeStateCode.Stopped,
            GuardReasonCode: guardReasonCode,
            EffectiveDataTimestampUtc: previousCursor.CloseTimeUtc,
            ExpectedOpenTimeUtc: expectedOpenTimeUtc);
    }

    private static MarketCandleSnapshot Normalize(MarketCandleSnapshot snapshot)
    {
        return snapshot with
        {
            Symbol = MarketDataSymbolNormalizer.Normalize(snapshot.Symbol),
            Interval = snapshot.Interval.Trim(),
            OpenTimeUtc = NormalizeTimestamp(snapshot.OpenTimeUtc),
            CloseTimeUtc = NormalizeTimestamp(snapshot.CloseTimeUtc),
            ReceivedAtUtc = NormalizeTimestamp(snapshot.ReceivedAtUtc),
            Source = snapshot.Source.Trim()
        };
    }

    private static string CreateCursorKey(string symbol, string interval)
    {
        return $"{symbol}:{interval}";
    }

    private static DateTime CalculateExpectedNextOpenTimeUtc(DateTime previousOpenTimeUtc, string interval)
    {
        var normalizedInterval = interval.Trim();

        if (normalizedInterval.Length < 2)
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
            'm' => previousOpenTimeUtc.AddMinutes(magnitude),
            'h' => previousOpenTimeUtc.AddHours(magnitude),
            'd' => previousOpenTimeUtc.AddDays(magnitude),
            'w' => previousOpenTimeUtc.AddDays(magnitude * 7d),
            'M' => previousOpenTimeUtc.AddMonths(magnitude),
            _ => throw new InvalidOperationException($"Unsupported candle interval '{interval}'.")
        };
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

    private sealed record ClosedCandleCursor(
        string Symbol,
        string Interval,
        DateTime OpenTimeUtc,
        DateTime CloseTimeUtc)
    {
        public static ClosedCandleCursor FromSnapshot(MarketCandleSnapshot snapshot)
        {
            return new ClosedCandleCursor(
                snapshot.Symbol,
                snapshot.Interval,
                snapshot.OpenTimeUtc,
                snapshot.CloseTimeUtc);
        }
    }
}
