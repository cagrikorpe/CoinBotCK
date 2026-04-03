using CoinBot.Application.Abstractions.MarketData;

namespace CoinBot.Infrastructure.MarketData;

internal static class SharedMarketDataProjectionPolicy
{
    public static SharedMarketDataProjectionResult NormalizeTicker(
        MarketPriceSnapshot snapshot,
        out MarketPriceSnapshot normalizedSnapshot)
    {
        normalizedSnapshot = snapshot;

        try
        {
            normalizedSnapshot = snapshot with
            {
                Symbol = MarketDataSymbolNormalizer.Normalize(snapshot.Symbol),
                ObservedAtUtc = NormalizeTimestamp(snapshot.ObservedAtUtc),
                ReceivedAtUtc = NormalizeTimestamp(snapshot.ReceivedAtUtc),
                Source = (snapshot.Source ?? string.Empty).Trim()
            };
        }
        catch (ArgumentException exception)
        {
            return SharedMarketDataProjectionResult.InvalidPayload(
                SharedMarketDataProjectionReasonCode.InvalidTickerPayload,
                SanitizeMessage(exception.Message));
        }

        if (string.IsNullOrWhiteSpace(normalizedSnapshot.Source))
        {
            return SharedMarketDataProjectionResult.InvalidPayload(
                SharedMarketDataProjectionReasonCode.InvalidTickerPayload,
                "Ticker source is required.");
        }

        if (normalizedSnapshot.Price <= 0m)
        {
            return SharedMarketDataProjectionResult.IgnoredDegraded(
                SharedMarketDataProjectionReasonCode.TickerDegraded,
                "Ticker price must be positive.");
        }

        if (normalizedSnapshot.ReceivedAtUtc < normalizedSnapshot.ObservedAtUtc)
        {
            return SharedMarketDataProjectionResult.InvalidPayload(
                SharedMarketDataProjectionReasonCode.InvalidTickerPayload,
                "Ticker ReceivedAtUtc cannot be earlier than ObservedAtUtc.");
        }

        return SharedMarketDataProjectionResult.Accepted();
    }

    public static SharedMarketDataProjectionResult NormalizeKline(
        MarketCandleSnapshot snapshot,
        CandleDataQualityGuardResult? guardResult,
        out MarketCandleSnapshot normalizedSnapshot)
    {
        normalizedSnapshot = snapshot;

        try
        {
            normalizedSnapshot = snapshot with
            {
                Symbol = MarketDataSymbolNormalizer.Normalize(snapshot.Symbol),
                Interval = NormalizeRequiredTimeframe(snapshot.Interval),
                OpenTimeUtc = NormalizeTimestamp(snapshot.OpenTimeUtc),
                CloseTimeUtc = NormalizeTimestamp(snapshot.CloseTimeUtc),
                ReceivedAtUtc = NormalizeTimestamp(snapshot.ReceivedAtUtc),
                Source = (snapshot.Source ?? string.Empty).Trim()
            };
        }
        catch (ArgumentException exception)
        {
            return SharedMarketDataProjectionResult.InvalidPayload(
                SharedMarketDataProjectionReasonCode.InvalidKlinePayload,
                SanitizeMessage(exception.Message));
        }

        if (string.IsNullOrWhiteSpace(normalizedSnapshot.Source))
        {
            return SharedMarketDataProjectionResult.InvalidPayload(
                SharedMarketDataProjectionReasonCode.InvalidKlinePayload,
                "Kline source is required.");
        }

        if (!normalizedSnapshot.IsClosed)
        {
            return SharedMarketDataProjectionResult.IgnoredDegraded(
                SharedMarketDataProjectionReasonCode.KlineDegraded,
                "Open kline snapshots are not projected to the latest closed-candle cache.");
        }

        if (guardResult is not null &&
            !guardResult.IsAccepted)
        {
            return SharedMarketDataProjectionResult.IgnoredDegraded(
                SharedMarketDataProjectionReasonCode.KlineDegraded,
                $"Kline rejected by data-quality guard: {guardResult.GuardReasonCode}.");
        }

        if (normalizedSnapshot.OpenPrice <= 0m ||
            normalizedSnapshot.HighPrice <= 0m ||
            normalizedSnapshot.LowPrice <= 0m ||
            normalizedSnapshot.ClosePrice <= 0m ||
            normalizedSnapshot.Volume < 0m ||
            normalizedSnapshot.HighPrice < normalizedSnapshot.LowPrice ||
            normalizedSnapshot.HighPrice < normalizedSnapshot.OpenPrice ||
            normalizedSnapshot.HighPrice < normalizedSnapshot.ClosePrice ||
            normalizedSnapshot.LowPrice > normalizedSnapshot.OpenPrice ||
            normalizedSnapshot.LowPrice > normalizedSnapshot.ClosePrice ||
            normalizedSnapshot.CloseTimeUtc < normalizedSnapshot.OpenTimeUtc ||
            normalizedSnapshot.ReceivedAtUtc < normalizedSnapshot.CloseTimeUtc)
        {
            return SharedMarketDataProjectionResult.InvalidPayload(
                SharedMarketDataProjectionReasonCode.InvalidKlinePayload,
                "Kline OHLCV or timestamp payload is invalid.");
        }

        return SharedMarketDataProjectionResult.Accepted();
    }

    public static SharedMarketDataProjectionResult NormalizeDepth(
        MarketDepthSnapshot snapshot,
        out MarketDepthSnapshot normalizedSnapshot)
    {
        normalizedSnapshot = snapshot;

        try
        {
            normalizedSnapshot = snapshot with
            {
                Symbol = MarketDataSymbolNormalizer.Normalize(snapshot.Symbol),
                EventTimeUtc = NormalizeTimestamp(snapshot.EventTimeUtc),
                ReceivedAtUtc = NormalizeTimestamp(snapshot.ReceivedAtUtc),
                Source = (snapshot.Source ?? string.Empty).Trim(),
                Bids = snapshot.Bids
                    .OrderByDescending(level => level.Price)
                    .ThenByDescending(level => level.Quantity)
                    .ToArray(),
                Asks = snapshot.Asks
                    .OrderBy(level => level.Price)
                    .ThenByDescending(level => level.Quantity)
                    .ToArray()
            };
        }
        catch (ArgumentException exception)
        {
            return SharedMarketDataProjectionResult.InvalidPayload(
                SharedMarketDataProjectionReasonCode.InvalidDepthPayload,
                SanitizeMessage(exception.Message));
        }

        if (string.IsNullOrWhiteSpace(normalizedSnapshot.Source))
        {
            return SharedMarketDataProjectionResult.InvalidPayload(
                SharedMarketDataProjectionReasonCode.InvalidDepthPayload,
                "Depth source is required.");
        }

        if (normalizedSnapshot.LastUpdateId < 0L ||
            normalizedSnapshot.ReceivedAtUtc < normalizedSnapshot.EventTimeUtc)
        {
            return SharedMarketDataProjectionResult.InvalidPayload(
                SharedMarketDataProjectionReasonCode.InvalidDepthPayload,
                "Depth sequence or timestamp payload is invalid.");
        }

        if (normalizedSnapshot.Bids.Count == 0 ||
            normalizedSnapshot.Asks.Count == 0)
        {
            return SharedMarketDataProjectionResult.IgnoredDegraded(
                SharedMarketDataProjectionReasonCode.DepthDegraded,
                "Depth snapshot requires at least one bid and one ask level.");
        }

        if (normalizedSnapshot.Bids.Any(level => level.Price <= 0m || level.Quantity <= 0m) ||
            normalizedSnapshot.Asks.Any(level => level.Price <= 0m || level.Quantity <= 0m))
        {
            return SharedMarketDataProjectionResult.InvalidPayload(
                SharedMarketDataProjectionReasonCode.InvalidDepthPayload,
                "Depth bid/ask prices and quantities must be positive.");
        }

        var bestBid = normalizedSnapshot.Bids.First();
        var bestAsk = normalizedSnapshot.Asks.First();
        if (bestBid.Price >= bestAsk.Price)
        {
            return SharedMarketDataProjectionResult.IgnoredDegraded(
                SharedMarketDataProjectionReasonCode.DepthDegraded,
                "Depth snapshot is crossed or locked.");
        }

        return SharedMarketDataProjectionResult.Accepted();
    }

    public static SharedMarketDataProjectionResult EvaluateTickerOrdering(
        MarketPriceSnapshot incoming,
        MarketPriceSnapshot current)
    {
        if (incoming.ObservedAtUtc < current.ObservedAtUtc ||
            (incoming.ObservedAtUtc == current.ObservedAtUtc &&
             incoming.ReceivedAtUtc < current.ReceivedAtUtc))
        {
            return SharedMarketDataProjectionResult.IgnoredOutOfOrder(
                SharedMarketDataProjectionReasonCode.TickerOutOfOrder,
                $"Ticker update {incoming.ObservedAtUtc:O}/{incoming.ReceivedAtUtc:O} is older than cached {current.ObservedAtUtc:O}/{current.ReceivedAtUtc:O}.");
        }

        return SharedMarketDataProjectionResult.Accepted();
    }

    public static SharedMarketDataProjectionResult EvaluateKlineOrdering(
        MarketCandleSnapshot incoming,
        MarketCandleSnapshot current)
    {
        if (incoming.CloseTimeUtc < current.CloseTimeUtc ||
            (incoming.CloseTimeUtc == current.CloseTimeUtc &&
             incoming.OpenTimeUtc < current.OpenTimeUtc) ||
            (incoming.CloseTimeUtc == current.CloseTimeUtc &&
             incoming.OpenTimeUtc == current.OpenTimeUtc &&
             incoming.ReceivedAtUtc < current.ReceivedAtUtc))
        {
            return SharedMarketDataProjectionResult.IgnoredOutOfOrder(
                SharedMarketDataProjectionReasonCode.KlineOutOfOrder,
                $"Kline update {incoming.OpenTimeUtc:O}/{incoming.CloseTimeUtc:O}/{incoming.ReceivedAtUtc:O} is older than cached {current.OpenTimeUtc:O}/{current.CloseTimeUtc:O}/{current.ReceivedAtUtc:O}.");
        }

        return SharedMarketDataProjectionResult.Accepted();
    }

    public static SharedMarketDataProjectionResult EvaluateDepthOrdering(
        MarketDepthSnapshot incoming,
        MarketDepthSnapshot current)
    {
        if (incoming.LastUpdateId.HasValue &&
            current.LastUpdateId.HasValue &&
            incoming.LastUpdateId.Value < current.LastUpdateId.Value)
        {
            return SharedMarketDataProjectionResult.IgnoredOutOfOrder(
                SharedMarketDataProjectionReasonCode.DepthOutOfOrder,
                $"Depth sequence {incoming.LastUpdateId.Value} is lower than cached {current.LastUpdateId.Value}.");
        }

        if (incoming.EventTimeUtc < current.EventTimeUtc ||
            (incoming.EventTimeUtc == current.EventTimeUtc &&
             incoming.ReceivedAtUtc < current.ReceivedAtUtc))
        {
            return SharedMarketDataProjectionResult.IgnoredOutOfOrder(
                SharedMarketDataProjectionReasonCode.DepthOutOfOrder,
                $"Depth update {incoming.EventTimeUtc:O}/{incoming.ReceivedAtUtc:O} is older than cached {current.EventTimeUtc:O}/{current.ReceivedAtUtc:O}.");
        }

        return SharedMarketDataProjectionResult.Accepted();
    }

    private static string NormalizeRequiredTimeframe(string timeframe)
    {
        var normalizedTimeframe = timeframe?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(normalizedTimeframe))
        {
            throw new ArgumentException("The timeframe is required.", nameof(timeframe));
        }

        return normalizedTimeframe;
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

    private static string SanitizeMessage(string? value)
    {
        var sanitized = string.IsNullOrWhiteSpace(value)
            ? "Shared market-data projection failed."
            : value.Trim();

        return sanitized.Length <= 256
            ? sanitized
            : sanitized[..256];
    }
}
