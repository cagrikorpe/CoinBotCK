using System.Globalization;
using System.Text.Json;
using CoinBot.Application.Abstractions.MarketData;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.MarketData;

public sealed class BinanceHistoricalKlineClient(
    HttpClient httpClient,
    TimeProvider timeProvider,
    ILogger<BinanceHistoricalKlineClient> logger) : IBinanceHistoricalKlineClient
{
    public async Task<IReadOnlyCollection<MarketCandleSnapshot>> GetClosedCandlesAsync(
        string symbol,
        string interval,
        DateTime startOpenTimeUtc,
        DateTime endOpenTimeUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = MarketDataSymbolNormalizer.Normalize(symbol);
        var normalizedInterval = NormalizeRequired(interval, nameof(interval));
        var normalizedStartOpenTimeUtc = NormalizeTimestamp(startOpenTimeUtc);
        var normalizedEndOpenTimeUtc = NormalizeTimestamp(endOpenTimeUtc);
        var requestEndTimeUtc = CalculateRequestEndTimeUtc(normalizedEndOpenTimeUtc, normalizedInterval);

        if (requestEndTimeUtc < normalizedStartOpenTimeUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endOpenTimeUtc),
                "The end open time must be greater than or equal to the start open time.");
        }

        var requestUri =
            $"api/v3/klines?symbol={Uri.EscapeDataString(normalizedSymbol)}&interval={Uri.EscapeDataString(normalizedInterval)}&startTime={ToUnixMilliseconds(normalizedStartOpenTimeUtc)}&endTime={ToUnixMilliseconds(requestEndTimeUtc)}&limit={limit}";

        using var response = await httpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        var receivedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var snapshots = new List<MarketCandleSnapshot>();

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<MarketCandleSnapshot>();
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
            var snapshot = TryMapSnapshot(item, normalizedSymbol, normalizedInterval, receivedAtUtc);

            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }
        }

        logger.LogInformation(
            "Binance historical kline backfill returned {CandleCount} closed candles for {Symbol} {Interval}.",
            snapshots.Count,
            normalizedSymbol,
            normalizedInterval);

        return snapshots
            .OrderBy(snapshot => snapshot.OpenTimeUtc)
            .ToArray();
    }

    private static MarketCandleSnapshot? TryMapSnapshot(
        JsonElement item,
        string symbol,
        string interval,
        DateTime receivedAtUtc)
    {
        if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 7)
        {
            return null;
        }

        if (!TryReadUnixMilliseconds(item[0], out var openTimeUtc) ||
            !TryReadUnixMilliseconds(item[6], out var closeTimeUtc) ||
            !TryReadDecimal(item[1], out var openPrice) ||
            !TryReadDecimal(item[2], out var highPrice) ||
            !TryReadDecimal(item[3], out var lowPrice) ||
            !TryReadDecimal(item[4], out var closePrice) ||
            !TryReadDecimal(item[5], out var volume))
        {
            return null;
        }

        return new MarketCandleSnapshot(
            symbol,
            interval,
            openTimeUtc,
            closeTimeUtc,
            openPrice,
            highPrice,
            lowPrice,
            closePrice,
            volume,
            IsClosed: true,
            receivedAtUtc,
            Source: "Binance.Rest.Kline");
    }

    private static bool TryReadDecimal(JsonElement element, out decimal value)
    {
        value = default;
        var rawValue = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null
        };

        return !string.IsNullOrWhiteSpace(rawValue) &&
               decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadUnixMilliseconds(JsonElement element, out DateTime value)
    {
        value = DateTime.MinValue;

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var milliseconds))
        {
            return false;
        }

        value = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
        return true;
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

    private static long ToUnixMilliseconds(DateTime value)
    {
        return new DateTimeOffset(NormalizeTimestamp(value)).ToUnixTimeMilliseconds();
    }

    private static DateTime CalculateRequestEndTimeUtc(DateTime endOpenTimeUtc, string interval)
    {
        var normalizedInterval = interval.Trim();
        var magnitudeText = normalizedInterval[..^1];
        var unit = normalizedInterval[^1];

        if (!int.TryParse(magnitudeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var magnitude) ||
            magnitude <= 0)
        {
            throw new InvalidOperationException($"Unsupported candle interval '{interval}'.");
        }

        var normalizedEndOpenTimeUtc = NormalizeTimestamp(endOpenTimeUtc);
        var exclusiveEndTimeUtc = unit switch
        {
            'm' => normalizedEndOpenTimeUtc.AddMinutes(magnitude),
            'h' => normalizedEndOpenTimeUtc.AddHours(magnitude),
            'd' => normalizedEndOpenTimeUtc.AddDays(magnitude),
            'w' => normalizedEndOpenTimeUtc.AddDays(magnitude * 7d),
            'M' => normalizedEndOpenTimeUtc.AddMonths(magnitude),
            _ => throw new InvalidOperationException($"Unsupported candle interval '{interval}'.")
        };

        return exclusiveEndTimeUtc.AddMilliseconds(-1);
    }
}
