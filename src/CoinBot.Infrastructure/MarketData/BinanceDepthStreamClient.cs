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

public sealed class BinanceDepthStreamClient(
    IOptions<BinanceMarketDataOptions> options,
    TimeProvider timeProvider,
    ILogger<BinanceDepthStreamClient> logger) : IBinanceDepthStreamClient
{
    private const string DepthStreamSuffix = "depth5@100ms";
    private readonly BinanceMarketDataOptions optionsValue = options.Value;

    public async IAsyncEnumerable<MarketDepthSnapshot> StreamAsync(
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
            "Connecting Binance combined depth stream for {SymbolCount} symbols.",
            normalizedSymbols.Count);

        await socket.ConnectAsync(streamUri, cancellationToken);

        var receiveBuffer = new byte[8192];
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

    internal MarketDepthSnapshot? TryParseSnapshot(string payload)
    {
        using var document = JsonDocument.Parse(payload);

        if (!document.RootElement.TryGetProperty("data", out var dataElement))
        {
            return null;
        }

        var symbol = ResolveSymbol(document.RootElement, dataElement);
        var bids = TryReadLevels(dataElement, "b", "bids");
        var asks = TryReadLevels(dataElement, "a", "asks");
        var lastUpdateId = TryReadUpdateId(dataElement);

        if (string.IsNullOrWhiteSpace(symbol) ||
            bids is null ||
            asks is null)
        {
            logger.LogDebug("Binance depth payload could not be mapped to a market depth snapshot.");
            return null;
        }

        var receivedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var eventTimeUtc = ResolveEventTimeUtc(dataElement, receivedAtUtc);

        return new MarketDepthSnapshot(
            MarketDataSymbolNormalizer.Normalize(symbol),
            bids,
            asks,
            lastUpdateId,
            eventTimeUtc,
            receivedAtUtc,
            Source: "Binance.WebSocket.Depth");
    }

    private Uri CreateStreamUri(IReadOnlyCollection<string> symbols)
    {
        var streamNames = string.Join(
            '/',
            symbols.Select(symbol => $"{symbol.ToLowerInvariant()}@{DepthStreamSuffix}"));
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

    private static string? ResolveSymbol(JsonElement rootElement, JsonElement dataElement)
    {
        if (dataElement.TryGetProperty("s", out var symbolElement))
        {
            return symbolElement.GetString();
        }

        if (!rootElement.TryGetProperty("stream", out var streamElement))
        {
            return null;
        }

        var streamName = streamElement.GetString();
        var separatorIndex = streamName?.IndexOf('@', StringComparison.Ordinal) ?? -1;
        return separatorIndex > 0
            ? streamName![..separatorIndex]
            : null;
    }

    private static long? TryReadUpdateId(JsonElement dataElement)
    {
        if (dataElement.TryGetProperty("u", out var finalUpdateIdElement) &&
            finalUpdateIdElement.ValueKind == JsonValueKind.Number &&
            finalUpdateIdElement.TryGetInt64(out var finalUpdateId))
        {
            return finalUpdateId;
        }

        if (dataElement.TryGetProperty("lastUpdateId", out var lastUpdateIdElement) &&
            lastUpdateIdElement.ValueKind == JsonValueKind.Number &&
            lastUpdateIdElement.TryGetInt64(out var lastUpdateId))
        {
            return lastUpdateId;
        }

        return null;
    }

    private static DateTime ResolveEventTimeUtc(JsonElement dataElement, DateTime fallbackUtc)
    {
        if (dataElement.TryGetProperty("E", out var eventTimeElement) &&
            eventTimeElement.ValueKind == JsonValueKind.Number &&
            eventTimeElement.TryGetInt64(out var eventTimeMilliseconds))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(eventTimeMilliseconds).UtcDateTime;
        }

        if (dataElement.TryGetProperty("T", out var transactionTimeElement) &&
            transactionTimeElement.ValueKind == JsonValueKind.Number &&
            transactionTimeElement.TryGetInt64(out var transactionTimeMilliseconds))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(transactionTimeMilliseconds).UtcDateTime;
        }

        return fallbackUtc;
    }

    private static IReadOnlyCollection<MarketDepthLevelSnapshot>? TryReadLevels(
        JsonElement dataElement,
        string compactPropertyName,
        string expandedPropertyName)
    {
        if (!dataElement.TryGetProperty(compactPropertyName, out var levelsElement) &&
            !dataElement.TryGetProperty(expandedPropertyName, out levelsElement))
        {
            return null;
        }

        if (levelsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var levels = new List<MarketDepthLevelSnapshot>();
        foreach (var levelElement in levelsElement.EnumerateArray())
        {
            if (levelElement.ValueKind != JsonValueKind.Array ||
                levelElement.GetArrayLength() < 2 ||
                !TryReadDecimal(levelElement[0], out var price) ||
                !TryReadDecimal(levelElement[1], out var quantity))
            {
                return null;
            }

            levels.Add(new MarketDepthLevelSnapshot(price, quantity));
        }

        return levels.Count == 0
            ? null
            : levels;
    }

    private static bool TryReadDecimal(JsonElement element, out decimal value)
    {
        value = 0m;

        return element.ValueKind switch
        {
            JsonValueKind.String => decimal.TryParse(
                element.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out value),
            JsonValueKind.Number => element.TryGetDecimal(out value),
            _ => false
        };
    }
}
