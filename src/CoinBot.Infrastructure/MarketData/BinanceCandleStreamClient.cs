using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CoinBot.Application.Abstractions.MarketData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.MarketData;

public sealed class BinanceCandleStreamClient(
    IOptions<BinanceMarketDataOptions> options,
    TimeProvider timeProvider,
    ILogger<BinanceCandleStreamClient> logger) : IBinanceCandleStreamClient
{
    private readonly BinanceMarketDataOptions optionsValue = options.Value;

    public async IAsyncEnumerable<MarketCandleSnapshot> StreamAsync(
        IReadOnlyCollection<string> symbols,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        var normalizedSymbols = MarketDataSymbolNormalizer.NormalizeMany(symbols);

        if (normalizedSymbols.Count == 0)
        {
            yield break;
        }

        using var socket = new ClientWebSocket();
        var streamUri = CreateStreamUri(normalizedSymbols);

        logger.LogInformation(
            "Connecting Binance combined kline stream for {SymbolCount} symbols at interval {Interval}.",
            normalizedSymbols.Count,
            optionsValue.KlineInterval);

        await socket.ConnectAsync(streamUri, cancellationToken);

        var receiveBuffer = new byte[4096];

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var payload = await ReceiveMessageAsync(socket, receiveBuffer, cancellationToken);

            if (payload is null)
            {
                yield break;
            }

            var snapshot = TryParseSnapshot(payload);

            if (snapshot is not null)
            {
                yield return snapshot;
            }
        }
    }

    private Uri CreateStreamUri(IReadOnlyCollection<string> symbols)
    {
        var normalizedInterval = optionsValue.KlineInterval.Trim();
        var streamNames = string.Join(
            '/',
            symbols.Select(symbol => $"{symbol.ToLowerInvariant()}@kline_{normalizedInterval}"));
        var baseUrl = optionsValue.WebSocketBaseUrl.TrimEnd('/');

        return new Uri($"{baseUrl}/stream?streams={streamNames}", UriKind.Absolute);
    }

    private static async Task<string?> ReceiveMessageAsync(
        ClientWebSocket socket,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var writer = new ArrayBufferWriter<byte>();

        while (true)
        {
            var memory = writer.GetMemory(buffer.Length);
            var result = await socket.ReceiveAsync(memory, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Normal closure",
                        cancellationToken);
                }

                return null;
            }

            writer.Advance(result.Count);

            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    private MarketCandleSnapshot? TryParseSnapshot(string payload)
    {
        using var document = JsonDocument.Parse(payload);

        if (!document.RootElement.TryGetProperty("data", out var dataElement) ||
            !dataElement.TryGetProperty("s", out var symbolElement) ||
            !dataElement.TryGetProperty("k", out var klineElement))
        {
            return null;
        }

        var symbol = symbolElement.GetString();
        var interval = klineElement.TryGetProperty("i", out var intervalElement)
            ? intervalElement.GetString()
            : null;
        var openPrice = TryReadDecimal(klineElement, "o");
        var highPrice = TryReadDecimal(klineElement, "h");
        var lowPrice = TryReadDecimal(klineElement, "l");
        var closePrice = TryReadDecimal(klineElement, "c");
        var volume = TryReadDecimal(klineElement, "v");

        if (string.IsNullOrWhiteSpace(symbol) ||
            string.IsNullOrWhiteSpace(interval) ||
            openPrice is null ||
            highPrice is null ||
            lowPrice is null ||
            closePrice is null ||
            volume is null ||
            !TryReadUnixMilliseconds(klineElement, "t", out var openTimeUtc) ||
            !TryReadUnixMilliseconds(klineElement, "T", out var closeTimeUtc) ||
            !klineElement.TryGetProperty("x", out var isClosedElement) ||
            (isClosedElement.ValueKind != JsonValueKind.True && isClosedElement.ValueKind != JsonValueKind.False))
        {
            logger.LogDebug("Binance kline payload could not be mapped to a market candle snapshot.");
            return null;
        }

        return new MarketCandleSnapshot(
            MarketDataSymbolNormalizer.Normalize(symbol),
            interval.Trim(),
            openTimeUtc,
            closeTimeUtc,
            openPrice.Value,
            highPrice.Value,
            lowPrice.Value,
            closePrice.Value,
            volume.Value,
            isClosedElement.GetBoolean(),
            timeProvider.GetUtcNow().UtcDateTime,
            Source: "Binance.WebSocket.Kline");
    }

    private static decimal? TryReadDecimal(JsonElement parentElement, string propertyName)
    {
        if (!parentElement.TryGetProperty(propertyName, out var propertyElement))
        {
            return null;
        }

        var value = propertyElement.GetString();

        if (string.IsNullOrWhiteSpace(value) ||
            !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedValue))
        {
            return null;
        }

        return parsedValue;
    }

    private static bool TryReadUnixMilliseconds(JsonElement parentElement, string propertyName, out DateTime timestampUtc)
    {
        timestampUtc = DateTime.MinValue;

        if (!parentElement.TryGetProperty(propertyName, out var propertyElement) ||
            propertyElement.ValueKind != JsonValueKind.Number ||
            !propertyElement.TryGetInt64(out var milliseconds))
        {
            return false;
        }

        timestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
        return true;
    }
}
