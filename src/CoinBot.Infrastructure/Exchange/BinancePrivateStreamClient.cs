using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CoinBot.Application.Abstractions.Exchange;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Exchange;

public sealed class BinancePrivateStreamClient(
    IOptions<BinancePrivateDataOptions> options,
    ILogger<BinancePrivateStreamClient> logger) : IBinancePrivateStreamClient
{
    private readonly BinancePrivateDataOptions optionsValue = options.Value;

    public async IAsyncEnumerable<BinancePrivateStreamEvent> StreamAsync(
        string listenKey,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listenKey);

        using var socket = new ClientWebSocket();
        var streamUri = CreateStreamUri(listenKey);

        logger.LogInformation("Connecting Binance private user data stream.");

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
        var baseUrl = optionsValue.WebSocketBaseUrl.TrimEnd('/');
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
            return new BinancePrivateStreamEvent(eventType, eventTimeUtc, [], []);
        }

        if (!string.Equals(eventType, "ACCOUNT_UPDATE", StringComparison.Ordinal))
        {
            logger.LogDebug("Ignoring unsupported Binance private stream event type {EventType}.", eventType);
            return null;
        }

        if (!root.TryGetProperty("a", out var accountUpdateElement))
        {
            return null;
        }

        return new BinancePrivateStreamEvent(
            eventType,
            eventTimeUtc,
            ParseBalances(accountUpdateElement, eventTimeUtc),
            ParsePositions(accountUpdateElement, eventTimeUtc));
    }

    private static IReadOnlyCollection<ExchangeBalanceSnapshot> ParseBalances(JsonElement accountUpdateElement, DateTime eventTimeUtc)
    {
        if (!accountUpdateElement.TryGetProperty("B", out var balancesElement) || balancesElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ExchangeBalanceSnapshot>();
        }

        var balances = new List<ExchangeBalanceSnapshot>();

        foreach (var balanceElement in balancesElement.EnumerateArray())
        {
            var asset = balanceElement.TryGetProperty("a", out var assetElement)
                ? NormalizeCode(assetElement.GetString())
                : null;
            var walletBalance = TryReadDecimal(balanceElement, "wb");
            var crossWalletBalance = TryReadDecimal(balanceElement, "cw");

            if (string.IsNullOrWhiteSpace(asset) ||
                walletBalance is null ||
                crossWalletBalance is null)
            {
                continue;
            }

            balances.Add(new ExchangeBalanceSnapshot(
                asset,
                walletBalance.Value,
                crossWalletBalance.Value,
                AvailableBalance: null,
                MaxWithdrawAmount: null,
                eventTimeUtc));
        }

        return balances
            .OrderBy(balance => balance.Asset, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyCollection<ExchangePositionSnapshot> ParsePositions(JsonElement accountUpdateElement, DateTime eventTimeUtc)
    {
        if (!accountUpdateElement.TryGetProperty("P", out var positionsElement) || positionsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ExchangePositionSnapshot>();
        }

        var positions = new List<ExchangePositionSnapshot>();

        foreach (var positionElement in positionsElement.EnumerateArray())
        {
            var symbol = positionElement.TryGetProperty("s", out var symbolElement)
                ? NormalizeCode(symbolElement.GetString())
                : null;
            var positionSide = positionElement.TryGetProperty("ps", out var positionSideElement)
                ? NormalizeCode(positionSideElement.GetString())
                : null;
            var marginType = positionElement.TryGetProperty("mt", out var marginTypeElement)
                ? NormalizeMarginType(marginTypeElement.GetString())
                : "cross";
            var quantity = TryReadDecimal(positionElement, "pa");
            var entryPrice = TryReadDecimal(positionElement, "ep");
            var breakEvenPrice = TryReadDecimal(positionElement, "bep");
            var unrealizedProfit = TryReadDecimal(positionElement, "up");
            var isolatedWallet = TryReadDecimal(positionElement, "iw");

            if (string.IsNullOrWhiteSpace(symbol) ||
                string.IsNullOrWhiteSpace(positionSide) ||
                quantity is null ||
                entryPrice is null ||
                breakEvenPrice is null ||
                unrealizedProfit is null ||
                isolatedWallet is null)
            {
                continue;
            }

            positions.Add(new ExchangePositionSnapshot(
                symbol,
                positionSide,
                quantity.Value,
                entryPrice.Value,
                breakEvenPrice.Value,
                unrealizedProfit.Value,
                marginType,
                isolatedWallet.Value,
                eventTimeUtc));
        }

        return positions
            .OrderBy(position => position.Symbol, StringComparer.Ordinal)
            .ThenBy(position => position.PositionSide, StringComparer.Ordinal)
            .ToArray();
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

    private static string NormalizeMarginType(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "cross"
            : value.Trim().ToLowerInvariant();
    }
}
