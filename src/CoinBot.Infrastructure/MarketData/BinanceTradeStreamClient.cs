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

public sealed class BinanceTradeStreamClient(
    IOptions<BinanceMarketDataOptions> options,
    TimeProvider timeProvider,
    ILogger<BinanceTradeStreamClient> logger) : IBinanceTradeStreamClient
{
    private readonly BinanceMarketDataOptions optionsValue = options.Value;

    public async IAsyncEnumerable<MarketPriceSnapshot> StreamAsync(
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
            "Connecting Binance combined trade stream for {SymbolCount} symbols.",
            normalizedSymbols.Count);

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
        var streamNames = string.Join(
            '/',
            symbols.Select(symbol => $"{symbol.ToLowerInvariant()}@trade"));
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

    private MarketPriceSnapshot? TryParseSnapshot(string payload)
    {
        using var document = JsonDocument.Parse(payload);

        if (!document.RootElement.TryGetProperty("data", out var dataElement))
        {
            return null;
        }

        if (!dataElement.TryGetProperty("s", out var symbolElement) ||
            !dataElement.TryGetProperty("p", out var priceElement))
        {
            return null;
        }

        var symbol = symbolElement.GetString();
        var price = priceElement.GetString();

        if (string.IsNullOrWhiteSpace(symbol) ||
            string.IsNullOrWhiteSpace(price) ||
            !decimal.TryParse(price, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedPrice))
        {
            logger.LogDebug("Binance trade payload could not be mapped to a market price snapshot.");
            return null;
        }

        var observedAtUtc = ResolveObservedAtUtc(dataElement, timeProvider.GetUtcNow().UtcDateTime);

        return new MarketPriceSnapshot(
            MarketDataSymbolNormalizer.Normalize(symbol),
            parsedPrice,
            observedAtUtc,
            timeProvider.GetUtcNow().UtcDateTime,
            Source: "Binance.WebSocket.Trade");
    }

    private static DateTime ResolveObservedAtUtc(JsonElement dataElement, DateTime fallbackUtc)
    {
        if (dataElement.TryGetProperty("T", out var tradeTimeElement) &&
            tradeTimeElement.ValueKind == JsonValueKind.Number &&
            tradeTimeElement.TryGetInt64(out var tradeTimeMilliseconds))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(tradeTimeMilliseconds).UtcDateTime;
        }

        if (dataElement.TryGetProperty("E", out var eventTimeElement) &&
            eventTimeElement.ValueKind == JsonValueKind.Number &&
            eventTimeElement.TryGetInt64(out var eventTimeMilliseconds))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(eventTimeMilliseconds).UtcDateTime;
        }

        return fallbackUtc;
    }
}
