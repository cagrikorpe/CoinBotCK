using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Exchange;

public sealed class BinanceSpotPrivateRestClient(
    HttpClient httpClient,
    IOptions<BinancePrivateDataOptions> options,
    TimeProvider timeProvider,
    IBinanceSpotTimeSyncService timeSyncService,
    ILogger<BinanceSpotPrivateRestClient> logger) : IBinanceSpotPrivateRestClient
{
    private readonly BinancePrivateDataOptions optionsValue = options.Value;

    public async Task<ExchangeAccountSnapshot> GetAccountSnapshotAsync(
        Guid exchangeAccountId,
        string ownerUserId,
        string exchangeName,
        string apiKey,
        string apiSecret,
        CancellationToken cancellationToken = default)
    {
        var observedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var timestamp = await GetTimestampAsync(cancellationToken);
        var unsignedQuery = $"timestamp={timestamp}&recvWindow={optionsValue.RecvWindowMilliseconds.ToString(CultureInfo.InvariantCulture)}";
        var signature = ComputeSignature(unsignedQuery, apiSecret);
        var path = $"/api/v3/account?{unsignedQuery}&signature={signature}";

        using var request = CreateApiKeyRequest(HttpMethod.Get, path, apiKey);
        using var response = await SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            await ThrowClockDriftAwareFailureAsync(
                $"Binance spot account snapshot request failed with status {(int)response.StatusCode}.",
                TryReadExchangeCode(responseBody),
                responseBody,
                cancellationToken);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var balances = ParseBalances(document.RootElement, observedAtUtc);
        var receivedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        logger.LogDebug(
            "Binance spot private account snapshot refreshed. Balances={BalanceCount}.",
            balances.Count);

        return new ExchangeAccountSnapshot(
            exchangeAccountId,
            ownerUserId.Trim(),
            exchangeName.Trim(),
            balances,
            [],
            observedAtUtc,
            receivedAtUtc,
            "Binance.SpotPrivateRest.Account",
            ExchangeDataPlane.Spot);
    }

    public async Task<BinanceOrderStatusSnapshot> GetOrderAsync(
        BinanceOrderQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ExchangeOrderId) &&
            string.IsNullOrWhiteSpace(request.ClientOrderId))
        {
            throw new ArgumentException("ExchangeOrderId or ClientOrderId is required.", nameof(request));
        }

        var observedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var timestamp = await GetTimestampAsync(cancellationToken);
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("symbol", NormalizeCode(request.Symbol) ?? string.Empty),
            new("timestamp", timestamp),
            new("recvWindow", optionsValue.RecvWindowMilliseconds.ToString(CultureInfo.InvariantCulture))
        };

        if (!string.IsNullOrWhiteSpace(request.ExchangeOrderId))
        {
            parameters.Add(new("orderId", request.ExchangeOrderId.Trim()));
        }
        else
        {
            parameters.Add(new("origClientOrderId", request.ClientOrderId!.Trim()));
        }

        var unsignedQuery = string.Join(
            "&",
            parameters.Select(parameter =>
                $"{parameter.Key}={Uri.EscapeDataString(parameter.Value)}"));
        var signature = ComputeSignature(unsignedQuery, request.ApiSecret);
        var path = $"/api/v3/order?{unsignedQuery}&signature={signature}";

        using var httpRequest = CreateApiKeyRequest(HttpMethod.Get, path, request.ApiKey);
        using var response = await SendAsync(httpRequest, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            await ThrowClockDriftAwareFailureAsync(
                $"Binance spot order status request failed with status {(int)response.StatusCode}.",
                TryReadExchangeCode(responseBody),
                responseBody,
                cancellationToken);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return BuildOrderStatusSnapshot(
            document.RootElement,
            request.Symbol,
            observedAtUtc,
            "Binance.SpotPrivateRest.Order");
    }

    public async Task<IReadOnlyCollection<BinanceSpotTradeFillSnapshot>> GetTradeFillsAsync(
        BinanceOrderQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ExchangeOrderId) &&
            string.IsNullOrWhiteSpace(request.ClientOrderId))
        {
            throw new ArgumentException("ExchangeOrderId or ClientOrderId is required.", nameof(request));
        }

        var resolvedOrder = string.IsNullOrWhiteSpace(request.ExchangeOrderId)
            ? await GetOrderAsync(request, cancellationToken)
            : null;
        var exchangeOrderId = request.ExchangeOrderId?.Trim() ?? resolvedOrder!.ExchangeOrderId;
        var clientOrderId = request.ClientOrderId?.Trim() ?? resolvedOrder!.ClientOrderId;
        var observedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var timestamp = await GetTimestampAsync(cancellationToken);
        var unsignedQuery =
            $"symbol={Uri.EscapeDataString(NormalizeCode(request.Symbol) ?? string.Empty)}&orderId={Uri.EscapeDataString(exchangeOrderId)}&timestamp={timestamp}&recvWindow={optionsValue.RecvWindowMilliseconds.ToString(CultureInfo.InvariantCulture)}";
        var signature = ComputeSignature(unsignedQuery, request.ApiSecret);
        var path = $"/api/v3/myTrades?{unsignedQuery}&signature={signature}";

        using var httpRequest = CreateApiKeyRequest(HttpMethod.Get, path, request.ApiKey);
        using var response = await SendAsync(httpRequest, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            await ThrowClockDriftAwareFailureAsync(
                $"Binance spot trade fills request failed with status {(int)response.StatusCode}.",
                TryReadExchangeCode(responseBody),
                responseBody,
                cancellationToken);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return document.RootElement
            .EnumerateArray()
            .Select(element => BuildTradeFillSnapshot(
                element,
                request.Symbol,
                exchangeOrderId,
                clientOrderId,
                observedAtUtc,
                "Binance.SpotPrivateRest.MyTrades"))
            .Where(snapshot => snapshot is not null)
            .Cast<BinanceSpotTradeFillSnapshot>()
            .OrderBy(snapshot => snapshot.TradeId)
            .ToArray();
    }

    public async Task<string> StartListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        using var request = CreateApiKeyRequest(HttpMethod.Post, "/api/v3/userDataStream", apiKey);
        using var response = await SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Binance spot listen key start request failed with status {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("listenKey", out var listenKeyElement))
        {
            throw new InvalidOperationException("Binance spot listen key start response did not contain a listenKey.");
        }

        var listenKey = listenKeyElement.GetString()?.Trim();

        if (string.IsNullOrWhiteSpace(listenKey))
        {
            throw new InvalidOperationException("Binance spot listen key start response contained an empty listenKey.");
        }

        return listenKey;
    }

    public async Task KeepAliveListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        using var request = CreateApiKeyRequest(HttpMethod.Put, "/api/v3/userDataStream", apiKey);
        using var response = await SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Binance spot listen key keepalive request failed with status {(int)response.StatusCode}.");
        }
    }

    public async Task CloseListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        using var request = CreateApiKeyRequest(HttpMethod.Delete, "/api/v3/userDataStream", apiKey);
        using var response = await SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Binance spot listen key close request failed with status {(int)response.StatusCode}.");
        }
    }

    private static HttpRequestMessage CreateApiKeyRequest(HttpMethod method, string path, string apiKey)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-MBX-APIKEY", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return httpClient.SendAsync(request, cancellationToken);
    }

    private async Task<string> GetTimestampAsync(CancellationToken cancellationToken)
    {
        var timestamp = await timeSyncService.GetCurrentTimestampMillisecondsAsync(cancellationToken);
        return timestamp.ToString(CultureInfo.InvariantCulture);
    }

    private async Task ThrowClockDriftAwareFailureAsync(
        string defaultMessage,
        string? exchangeCode,
        string? responseBody,
        CancellationToken cancellationToken)
    {
        if (IsClockDriftResponse(exchangeCode, responseBody))
        {
            var snapshot = await timeSyncService.GetSnapshotAsync(forceRefresh: true, cancellationToken);
            var driftText = snapshot.ClockDriftMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? "missing";
            var lastSyncText = snapshot.LastSynchronizedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "missing";
            throw new BinanceClockDriftException(
                $"Binance spot request timestamp was rejected. Status={snapshot.StatusCode}; DriftMs={driftText}; OffsetMs={snapshot.OffsetMilliseconds.ToString(CultureInfo.InvariantCulture)}; LastSyncUtc={lastSyncText}.");
        }

        throw new InvalidOperationException(defaultMessage);
    }

    private static string ComputeSignature(string payload, string secret)
    {
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        try
        {
            using var hmac = new HMACSHA256(secretBytes);
            return Convert.ToHexStringLower(hmac.ComputeHash(payloadBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            CryptographicOperations.ZeroMemory(payloadBytes);
        }
    }

    private static IReadOnlyCollection<ExchangeBalanceSnapshot> ParseBalances(JsonElement root, DateTime fallbackTimestampUtc)
    {
        if (!root.TryGetProperty("balances", out var balancesElement) || balancesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var balances = new List<ExchangeBalanceSnapshot>();

        foreach (var balanceElement in balancesElement.EnumerateArray())
        {
            var asset = balanceElement.TryGetProperty("asset", out var assetElement)
                ? NormalizeCode(assetElement.GetString())
                : null;
            var freeBalance = TryReadDecimal(balanceElement, "free");
            var lockedBalance = TryReadDecimal(balanceElement, "locked");

            if (string.IsNullOrWhiteSpace(asset) ||
                freeBalance is null ||
                lockedBalance is null)
            {
                continue;
            }

            var walletBalance = freeBalance.Value + lockedBalance.Value;
            var snapshot = new ExchangeBalanceSnapshot(
                asset,
                walletBalance,
                walletBalance,
                freeBalance,
                freeBalance,
                fallbackTimestampUtc,
                lockedBalance,
                ExchangeDataPlane.Spot);

            if (IsEmptyBalance(snapshot))
            {
                continue;
            }

            balances.Add(snapshot);
        }

        return balances
            .OrderBy(balance => balance.Asset, StringComparer.Ordinal)
            .ToArray();
    }

    private static BinanceOrderStatusSnapshot BuildOrderStatusSnapshot(
        JsonElement root,
        string requestedSymbol,
        DateTime observedAtUtc,
        string source)
    {
        var orderId = root.TryGetProperty("orderId", out var orderIdElement)
            ? orderIdElement.ToString()
            : null;
        var clientOrderId = root.TryGetProperty("clientOrderId", out var clientOrderIdElement)
            ? clientOrderIdElement.GetString()?.Trim()
            : null;
        var status = root.TryGetProperty("status", out var statusElement)
            ? NormalizeCode(statusElement.GetString())
            : null;
        var originalQuantity = TryReadDecimal(root, "origQty") ?? 0m;
        var executedQuantity = TryReadDecimal(root, "executedQty") ?? 0m;
        var cumulativeQuoteQuantity = TryReadDecimal(root, "cummulativeQuoteQty") ?? 0m;
        var averagePrice = executedQuantity > 0m && cumulativeQuoteQuantity > 0m
            ? cumulativeQuoteQuantity / executedQuantity
            : 0m;
        var updatedAtUtc = TryReadUnixMilliseconds(root, "updateTime", observedAtUtc);

        if (updatedAtUtc == observedAtUtc)
        {
            updatedAtUtc = TryReadUnixMilliseconds(root, "workingTime", observedAtUtc);
        }

        if (string.IsNullOrWhiteSpace(orderId) ||
            string.IsNullOrWhiteSpace(clientOrderId) ||
            string.IsNullOrWhiteSpace(status))
        {
            throw new InvalidOperationException("Binance spot order status response was incomplete.");
        }

        return new BinanceOrderStatusSnapshot(
            NormalizeCode(requestedSymbol) ?? requestedSymbol.Trim(),
            orderId,
            clientOrderId,
            status,
            originalQuantity,
            executedQuantity,
            cumulativeQuoteQuantity,
            averagePrice,
            LastExecutedQuantity: 0m,
            LastExecutedPrice: 0m,
            updatedAtUtc,
            source,
            TradeId: null,
            FeeAsset: null,
            FeeAmount: null,
            Plane: ExchangeDataPlane.Spot);
    }

    private static BinanceSpotTradeFillSnapshot? BuildTradeFillSnapshot(
        JsonElement root,
        string requestedSymbol,
        string exchangeOrderId,
        string clientOrderId,
        DateTime fallbackTimestampUtc,
        string source)
    {
        var tradeId = root.TryGetProperty("id", out var tradeIdElement) && tradeIdElement.TryGetInt64(out var parsedTradeId)
            ? parsedTradeId
            : (long?)null;
        var quantity = TryReadDecimal(root, "qty");
        var quoteQuantity = TryReadDecimal(root, "quoteQty");
        var price = TryReadDecimal(root, "price");
        var feeAmount = TryReadDecimal(root, "commission");
        var feeAsset = root.TryGetProperty("commissionAsset", out var feeAssetElement)
            ? NormalizeCode(feeAssetElement.GetString())
            : null;
        var eventTimeUtc = TryReadUnixMilliseconds(root, "time", fallbackTimestampUtc);

        if (!tradeId.HasValue ||
            quantity is null ||
            quoteQuantity is null ||
            price is null)
        {
            return null;
        }

        return new BinanceSpotTradeFillSnapshot(
            NormalizeCode(requestedSymbol) ?? requestedSymbol.Trim(),
            exchangeOrderId,
            clientOrderId,
            tradeId.Value,
            quantity.Value,
            quoteQuantity.Value,
            price.Value,
            feeAsset,
            feeAmount,
            eventTimeUtc,
            source);
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

    private static bool IsEmptyBalance(ExchangeBalanceSnapshot snapshot)
    {
        return snapshot.WalletBalance == 0m &&
               snapshot.CrossWalletBalance == 0m &&
               (snapshot.AvailableBalance ?? 0m) == 0m &&
               (snapshot.MaxWithdrawAmount ?? 0m) == 0m &&
               (snapshot.LockedBalance ?? 0m) == 0m;
    }

    private static string? NormalizeCode(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToUpperInvariant();
    }

    private static string? TryReadExchangeCode(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);

            if (document.RootElement.TryGetProperty("code", out var codeElement))
            {
                return codeElement.ToString();
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadExchangeMessage(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);

            if (document.RootElement.TryGetProperty("msg", out var messageElement))
            {
                return messageElement.GetString()?.Trim();
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsClockDriftResponse(string? exchangeCode, string? responseBody)
    {
        var exchangeMessage = TryReadExchangeMessage(responseBody);

        return string.Equals(exchangeCode, "-1021", StringComparison.Ordinal) ||
               exchangeMessage?.Contains("timestamp", StringComparison.OrdinalIgnoreCase) == true ||
               exchangeMessage?.Contains("recvWindow", StringComparison.OrdinalIgnoreCase) == true;
    }
}
