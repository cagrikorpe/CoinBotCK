using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Exchange;

public sealed class BinanceSpotPrivateStreamClient(
    IOptions<BinancePrivateDataOptions> options,
    ILogger<BinanceSpotPrivateStreamClient> logger) : IBinanceSpotPrivateStreamClient
{
    private readonly BinancePrivateDataOptions optionsValue = options.Value;

    public async IAsyncEnumerable<BinancePrivateStreamEvent> StreamAsync(
        string listenKey,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listenKey);

        using var socket = new ClientWebSocket();
        var streamUri = CreateStreamUri(listenKey);

        logger.LogInformation("Connecting Binance spot private user data stream.");

        await socket.ConnectAsync(streamUri, cancellationToken);

        var receiveBuffer = new byte[4096];

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var payload = await ReceiveMessageAsync(socket, receiveBuffer, cancellationToken);

            if (payload is null)
            {
                yield break;
            }

            var streamEvent = TryParseEvent(payload);

            if (streamEvent is not null)
            {
                yield return streamEvent;
            }
        }
    }

    private Uri CreateStreamUri(string listenKey)
    {
        var baseUrl = optionsValue.SpotWebSocketBaseUrl.TrimEnd('/');
        return new Uri($"{baseUrl}/ws/{listenKey.Trim()}", UriKind.Absolute);
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

    private BinancePrivateStreamEvent? TryParseEvent(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        if (!root.TryGetProperty("e", out var eventTypeElement))
        {
            return null;
        }

        var eventType = eventTypeElement.GetString()?.Trim();

        if (string.IsNullOrWhiteSpace(eventType))
        {
            return null;
        }

        var eventTimeUtc = TryReadUnixMilliseconds(root, "E", DateTime.UtcNow);

        if (string.Equals(eventType, "listenKeyExpired", StringComparison.Ordinal))
        {
            return new BinancePrivateStreamEvent(
                eventType,
                eventTimeUtc,
                [],
                [],
                [],
                RequiresAccountRefresh: false,
                Plane: ExchangeDataPlane.Spot);
        }

        if (string.Equals(eventType, "outboundAccountPosition", StringComparison.Ordinal))
        {
            return new BinancePrivateStreamEvent(
                eventType,
                eventTimeUtc,
                ParseBalances(root, eventTimeUtc),
                [],
                [],
                RequiresAccountRefresh: false,
                Plane: ExchangeDataPlane.Spot);
        }

        if (string.Equals(eventType, "balanceUpdate", StringComparison.Ordinal))
        {
            return new BinancePrivateStreamEvent(
                eventType,
                eventTimeUtc,
                [],
                [],
                [],
                RequiresAccountRefresh: true,
                Plane: ExchangeDataPlane.Spot);
        }

        if (string.Equals(eventType, "executionReport", StringComparison.Ordinal))
        {
            var orderUpdate = ParseOrderUpdate(root, eventTimeUtc);

            return orderUpdate is null
                ? null
                : new BinancePrivateStreamEvent(
                    eventType,
                    eventTimeUtc,
                    [],
                    [],
                    [orderUpdate],
                    RequiresAccountRefresh: orderUpdate.LastExecutedQuantity > 0m,
                    Plane: ExchangeDataPlane.Spot);
        }

        logger.LogDebug("Ignoring unsupported Binance spot private stream event type {EventType}.", eventType);
        return null;
    }

    private static IReadOnlyCollection<ExchangeBalanceSnapshot> ParseBalances(JsonElement root, DateTime fallbackTimestampUtc)
    {
        if (!root.TryGetProperty("B", out var balancesElement) || balancesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var eventTimeUtc = TryReadUnixMilliseconds(root, "u", fallbackTimestampUtc);
        var balances = new List<ExchangeBalanceSnapshot>();

        foreach (var balanceElement in balancesElement.EnumerateArray())
        {
            var asset = balanceElement.TryGetProperty("a", out var assetElement)
                ? NormalizeCode(assetElement.GetString())
                : null;
            var freeBalance = TryReadDecimal(balanceElement, "f");
            var lockedBalance = TryReadDecimal(balanceElement, "l");

            if (string.IsNullOrWhiteSpace(asset) ||
                freeBalance is null ||
                lockedBalance is null)
            {
                continue;
            }

            var walletBalance = freeBalance.Value + lockedBalance.Value;
            balances.Add(new ExchangeBalanceSnapshot(
                asset,
                walletBalance,
                walletBalance,
                freeBalance,
                freeBalance,
                eventTimeUtc,
                lockedBalance,
                ExchangeDataPlane.Spot));
        }

        return balances
            .OrderBy(balance => balance.Asset, StringComparer.Ordinal)
            .ToArray();
    }

    private static BinanceOrderStatusSnapshot? ParseOrderUpdate(JsonElement root, DateTime fallbackTimestampUtc)
    {
        var symbol = root.TryGetProperty("s", out var symbolElement)
            ? NormalizeCode(symbolElement.GetString())
            : null;
        var exchangeOrderId = root.TryGetProperty("i", out var orderIdElement)
            ? orderIdElement.ToString()
            : null;
        var clientOrderId = root.TryGetProperty("c", out var clientOrderIdElement)
            ? clientOrderIdElement.GetString()?.Trim()
            : null;
        var status = root.TryGetProperty("X", out var statusElement)
            ? NormalizeCode(statusElement.GetString())
            : null;
        var originalQuantity = TryReadDecimal(root, "q");
        var executedQuantity = TryReadDecimal(root, "z");
        var cumulativeQuoteQuantity = TryReadDecimal(root, "Z") ?? 0m;
        var averagePrice = executedQuantity > 0m && cumulativeQuoteQuantity > 0m
            ? cumulativeQuoteQuantity / executedQuantity.Value
            : 0m;
        var lastExecutedQuantity = TryReadDecimal(root, "l") ?? 0m;
        var lastExecutedPrice = TryReadDecimal(root, "L") ?? 0m;
        var feeAmount = TryReadDecimal(root, "n");
        var feeAsset = root.TryGetProperty("N", out var feeAssetElement)
            ? NormalizeCode(feeAssetElement.GetString())
            : null;
        var tradeId = root.TryGetProperty("t", out var tradeIdElement) && tradeIdElement.TryGetInt64(out var parsedTradeId) && parsedTradeId >= 0
            ? parsedTradeId
            : (long?)null;
        var eventTimeUtc = TryReadUnixMilliseconds(root, "T", fallbackTimestampUtc);

        if (string.IsNullOrWhiteSpace(symbol) ||
            string.IsNullOrWhiteSpace(exchangeOrderId) ||
            string.IsNullOrWhiteSpace(clientOrderId) ||
            string.IsNullOrWhiteSpace(status) ||
            originalQuantity is null ||
            executedQuantity is null)
        {
            return null;
        }

        return new BinanceOrderStatusSnapshot(
            symbol,
            exchangeOrderId,
            clientOrderId,
            status,
            originalQuantity.Value,
            executedQuantity.Value,
            cumulativeQuoteQuantity,
            averagePrice,
            lastExecutedQuantity,
            lastExecutedPrice,
            eventTimeUtc,
            "Binance.SpotPrivateStream.ExecutionReport",
            tradeId,
            feeAsset,
            feeAmount,
            ExchangeDataPlane.Spot);
    }

    private static decimal? TryReadDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var propertyElement))
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

    private static DateTime TryReadUnixMilliseconds(JsonElement element, string propertyName, DateTime fallbackTimestampUtc)
    {
        if (!element.TryGetProperty(propertyName, out var propertyElement))
        {
            return fallbackTimestampUtc;
        }

        return propertyElement.ValueKind switch
        {
            JsonValueKind.Number when propertyElement.TryGetInt64(out var milliseconds) && milliseconds > 0 =>
                DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime,
            JsonValueKind.String when long.TryParse(propertyElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMilliseconds) && parsedMilliseconds > 0 =>
                DateTimeOffset.FromUnixTimeMilliseconds(parsedMilliseconds).UtcDateTime,
            _ => fallbackTimestampUtc
        };
    }

    private static string? NormalizeCode(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToUpperInvariant();
    }
}
