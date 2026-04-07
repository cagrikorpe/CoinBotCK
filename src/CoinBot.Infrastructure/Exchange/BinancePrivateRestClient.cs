using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Exchange;

public sealed class BinancePrivateRestClient(
    HttpClient httpClient,
    IOptions<BinancePrivateDataOptions> options,
    TimeProvider timeProvider,
    IBinanceTimeSyncService timeSyncService,
    ILogger<BinancePrivateRestClient> logger,
    IMonitoringTelemetryCollector? monitoringTelemetryCollector = null,
    IServiceScopeFactory? serviceScopeFactory = null) : IBinancePrivateRestClient
{
    private readonly BinancePrivateDataOptions optionsValue = options.Value;

    public async Task EnsureMarginTypeAsync(
        Guid exchangeAccountId,
        string symbol,
        string marginType,
        string apiKey,
        string apiSecret,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = NormalizeCode(symbol)
            ?? throw new ArgumentException("The symbol is required.", nameof(symbol));
        var normalizedMarginType = NormalizeCode(marginType)
            ?? throw new ArgumentException("The margin type is required.", nameof(marginType));
        var timestamp = await GetTimestampAsync(cancellationToken);
        var unsignedQuery =
            $"symbol={Uri.EscapeDataString(normalizedSymbol)}&marginType={Uri.EscapeDataString(normalizedMarginType)}&timestamp={timestamp}&recvWindow={optionsValue.RecvWindowMilliseconds.ToString(CultureInfo.InvariantCulture)}";
        var signature = ComputeSignature(unsignedQuery, apiSecret);
        var path = $"/fapi/v1/marginType?{unsignedQuery}&signature={signature}";

        using var request = CreateApiKeyRequest(HttpMethod.Post, path, apiKey);
        using var response = await SendAsyncWithTelemetryAsync(request, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (string.Equals(TryReadExchangeCode(responseBody), "-4046", StringComparison.Ordinal))
        {
            return;
        }

        await ThrowClockDriftAwareFailureAsync(
            $"Binance margin type configuration failed with status {(int)response.StatusCode}.",
            TryReadExchangeCode(responseBody),
            responseBody,
            cancellationToken);
    }

    public async Task EnsureLeverageAsync(
        Guid exchangeAccountId,
        string symbol,
        decimal leverage,
        string apiKey,
        string apiSecret,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = NormalizeCode(symbol)
            ?? throw new ArgumentException("The symbol is required.", nameof(symbol));
        var normalizedLeverage = decimal.Truncate(leverage);

        if (normalizedLeverage < 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(leverage), "Leverage must be at least 1.");
        }

        var timestamp = await GetTimestampAsync(cancellationToken);
        var unsignedQuery =
            $"symbol={Uri.EscapeDataString(normalizedSymbol)}&leverage={normalizedLeverage.ToString(CultureInfo.InvariantCulture)}&timestamp={timestamp}&recvWindow={optionsValue.RecvWindowMilliseconds.ToString(CultureInfo.InvariantCulture)}";
        var signature = ComputeSignature(unsignedQuery, apiSecret);
        var path = $"/fapi/v1/leverage?{unsignedQuery}&signature={signature}";

        using var request = CreateApiKeyRequest(HttpMethod.Post, path, apiKey);
        using var response = await SendAsyncWithTelemetryAsync(request, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            await ThrowClockDriftAwareFailureAsync(
                $"Binance leverage configuration failed with status {(int)response.StatusCode}.",
                TryReadExchangeCode(responseBody),
                responseBody,
                cancellationToken);
        }
    }

    public async Task<BinanceOrderPlacementResult> PlaceOrderAsync(
        BinanceOrderPlacementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var submittedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var timestamp = await GetTimestampAsync(cancellationToken);
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("symbol", NormalizeCode(request.Symbol) ?? string.Empty),
            new("side", request.Side == ExecutionOrderSide.Buy ? "BUY" : "SELL"),
            new("type", request.OrderType == ExecutionOrderType.Market ? "MARKET" : "LIMIT"),
            new("quantity", FormatDecimal(request.Quantity)),
            new("newClientOrderId", request.ClientOrderId.Trim()),
            new("timestamp", timestamp),
            new("recvWindow", optionsValue.RecvWindowMilliseconds.ToString(CultureInfo.InvariantCulture))
        };

        if (request.OrderType == ExecutionOrderType.Limit)
        {
            parameters.Add(new("timeInForce", "GTC"));
            parameters.Add(new("price", FormatDecimal(request.Price)));
        }

        if (request.ReduceOnly)
        {
            parameters.Add(new("reduceOnly", "true"));
        }

        var unsignedQuery = string.Join(
            "&",
            parameters.Select(parameter =>
                $"{parameter.Key}={Uri.EscapeDataString(parameter.Value)}"));
        var signature = ComputeSignature(unsignedQuery, request.ApiSecret);
        var path = $"/fapi/v1/order?{unsignedQuery}&signature={signature}";
        var maskedRequest = SensitivePayloadMasker.Mask(
            JsonSerializer.Serialize(new
            {
                Endpoint = path,
                Headers = new Dictionary<string, string?>
                {
                    ["X-MBX-APIKEY"] = request.ApiKey,
                    ["Authorization"] = "BinanceApiKey"
                }
            }));
        string? responseBody = null;
        string? maskedResponse = null;
        string? exchangeCode = null;
        int? httpStatusCode = null;
        var traceWritten = false;

        try
        {
            using var httpRequest = CreateApiKeyRequest(HttpMethod.Post, path, request.ApiKey);
            using var response = await SendAsyncWithTelemetryAsync(httpRequest, submittedAtUtc, cancellationToken);
            httpStatusCode = (int)response.StatusCode;
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            maskedResponse = SensitivePayloadMasker.Mask(responseBody);
            exchangeCode = TryReadExchangeCode(responseBody);

            if (!response.IsSuccessStatusCode)
            {
                await WriteExecutionTraceAsync(
                    request,
                    maskedRequest,
                    maskedResponse,
                    httpStatusCode,
                    exchangeCode,
                    stopwatch.ElapsedMilliseconds,
                    cancellationToken);
                traceWritten = true;

                await ThrowOrderPlacementFailureAsync(
                    (int)response.StatusCode,
                    exchangeCode,
                    responseBody,
                    cancellationToken);
            }

            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var orderId = root.TryGetProperty("orderId", out var orderIdElement)
                ? orderIdElement.ToString()
                : request.ClientOrderId;
            var clientOrderId = root.TryGetProperty("clientOrderId", out var clientOrderIdElement)
                ? clientOrderIdElement.GetString()?.Trim()
                : request.ClientOrderId;
            var snapshot = TryBuildPlacementSnapshot(root, request.Symbol, submittedAtUtc);

            await WriteExecutionTraceAsync(
                request,
                maskedRequest,
                maskedResponse,
                httpStatusCode,
                exchangeCode,
                stopwatch.ElapsedMilliseconds,
                cancellationToken);
            traceWritten = true;

            logger.LogInformation(
                "Binance order placed for account {ExchangeAccountId} on {Symbol}.",
                request.ExchangeAccountId,
                request.Symbol);

            return new BinanceOrderPlacementResult(
                string.IsNullOrWhiteSpace(orderId) ? request.ClientOrderId : orderId,
                string.IsNullOrWhiteSpace(clientOrderId) ? request.ClientOrderId : clientOrderId,
                submittedAtUtc,
                snapshot);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && !traceWritten)
        {
            await WriteExecutionTraceAsync(
                request,
                maskedRequest,
                maskedResponse ?? SensitivePayloadMasker.Mask(exception.Message),
                httpStatusCode,
                exchangeCode,
                stopwatch.ElapsedMilliseconds,
                cancellationToken);

            throw;
        }
    }

    public async Task<BinanceOrderStatusSnapshot> CancelOrderAsync(
        BinanceOrderCancelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ExchangeOrderId) &&
            string.IsNullOrWhiteSpace(request.ClientOrderId))
        {
            throw new ArgumentException("ExchangeOrderId or ClientOrderId is required.", nameof(request));
        }

        var stopwatch = Stopwatch.StartNew();
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
        var path = $"/fapi/v1/order?{unsignedQuery}&signature={signature}";
        var maskedRequest = SensitivePayloadMasker.Mask(
            JsonSerializer.Serialize(new
            {
                Endpoint = path,
                Headers = new Dictionary<string, string?>
                {
                    ["X-MBX-APIKEY"] = request.ApiKey,
                    ["Authorization"] = "BinanceApiKey"
                }
            }));
        string? responseBody = null;
        string? maskedResponse = null;
        string? exchangeCode = null;
        int? httpStatusCode = null;
        var traceWritten = false;

        try
        {
            using var httpRequest = CreateApiKeyRequest(HttpMethod.Delete, path, request.ApiKey);
            using var response = await SendAsyncWithTelemetryAsync(httpRequest, observedAtUtc, cancellationToken);
            httpStatusCode = (int)response.StatusCode;
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            maskedResponse = SensitivePayloadMasker.Mask(responseBody);
            exchangeCode = TryReadExchangeCode(responseBody);

            if (!response.IsSuccessStatusCode)
            {
                await WriteExecutionTraceAsync(
                    request,
                    maskedRequest,
                    maskedResponse,
                    httpStatusCode,
                    exchangeCode,
                    stopwatch.ElapsedMilliseconds,
                    cancellationToken);
                traceWritten = true;

                await ThrowClockDriftAwareFailureAsync(
                    $"Binance order cancel request failed with status {(int)response.StatusCode}.",
                    exchangeCode,
                    responseBody,
                    cancellationToken);
            }

            await WriteExecutionTraceAsync(
                request,
                maskedRequest,
                maskedResponse,
                httpStatusCode,
                exchangeCode,
                stopwatch.ElapsedMilliseconds,
                cancellationToken);
            traceWritten = true;

            using var document = JsonDocument.Parse(responseBody);
            return BuildOrderStatusSnapshot(
                document.RootElement,
                request.Symbol,
                observedAtUtc,
                "Binance.PrivateRest.Cancel");
        }
        catch (Exception exception) when (exception is not OperationCanceledException && !traceWritten)
        {
            await WriteExecutionTraceAsync(
                request,
                maskedRequest,
                maskedResponse ?? SensitivePayloadMasker.Mask(exception.Message),
                httpStatusCode,
                exchangeCode,
                stopwatch.ElapsedMilliseconds,
                cancellationToken);

            throw;
        }
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
        var path = $"/fapi/v1/order?{unsignedQuery}&signature={signature}";

        using var httpRequest = CreateApiKeyRequest(HttpMethod.Get, path, request.ApiKey);
        using var response = await SendAsyncWithTelemetryAsync(httpRequest, observedAtUtc, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            await ThrowClockDriftAwareFailureAsync(
                $"Binance order status request failed with status {(int)response.StatusCode}.",
                TryReadExchangeCode(responseBody),
                responseBody,
                cancellationToken);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        logger.LogDebug(
            "Binance order status refreshed for account {ExchangeAccountId} on {Symbol}.",
            request.ExchangeAccountId,
            request.Symbol);

        return BuildOrderStatusSnapshot(
            root,
            request.Symbol,
            observedAtUtc,
            "Binance.PrivateRest.Order");
    }

    public async Task<string> StartListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        var observedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        using var request = CreateApiKeyRequest(HttpMethod.Post, "/fapi/v1/listenKey", apiKey);
        using var response = await SendAsyncWithTelemetryAsync(request, observedAtUtc, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Binance listen key start request failed with status {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("listenKey", out var listenKeyElement))
        {
            throw new InvalidOperationException("Binance listen key start response did not contain a listenKey.");
        }

        var listenKey = listenKeyElement.GetString()?.Trim();

        if (string.IsNullOrWhiteSpace(listenKey))
        {
            throw new InvalidOperationException("Binance listen key start response contained an empty listenKey.");
        }

        logger.LogDebug("Binance private listen key acquired.");
        return listenKey;
    }

    public async Task KeepAliveListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        var observedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        using var request = CreateApiKeyRequest(HttpMethod.Put, "/fapi/v1/listenKey", apiKey);
        using var response = await SendAsyncWithTelemetryAsync(request, observedAtUtc, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Binance listen key keepalive request failed with status {(int)response.StatusCode}.");
        }
    }

    public async Task CloseListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        var observedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        using var request = CreateApiKeyRequest(HttpMethod.Delete, "/fapi/v1/listenKey", apiKey);
        using var response = await SendAsyncWithTelemetryAsync(request, observedAtUtc, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Binance listen key close request failed with status {(int)response.StatusCode}.");
        }
    }

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
        var path = $"/fapi/v3/account?{unsignedQuery}&signature={signature}";

        using var request = CreateApiKeyRequest(HttpMethod.Get, path, apiKey);
        using var response = await SendAsyncWithTelemetryAsync(request, observedAtUtc, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            await ThrowClockDriftAwareFailureAsync(
                $"Binance account snapshot request failed with status {(int)response.StatusCode}.",
                TryReadExchangeCode(responseBody),
                responseBody,
                cancellationToken);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var balances = ParseBalances(root, observedAtUtc);
        var positions = ParsePositions(root, observedAtUtc);
        var receivedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        logger.LogDebug(
            "Binance private account snapshot refreshed. Balances={BalanceCount}, Positions={PositionCount}.",
            balances.Count,
            positions.Count);

        return new ExchangeAccountSnapshot(
            exchangeAccountId,
            ownerUserId.Trim(),
            exchangeName.Trim(),
            balances,
            positions,
            observedAtUtc,
            receivedAtUtc,
            "Binance.PrivateRest.Account");
    }

    private static HttpRequestMessage CreateApiKeyRequest(HttpMethod method, string path, string apiKey)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-MBX-APIKEY", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<HttpResponseMessage> SendAsyncWithTelemetryAsync(
        HttpRequestMessage request,
        DateTime observedAtUtc,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage? response = null;

        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
            return response;
        }
        finally
        {
            stopwatch.Stop();
            monitoringTelemetryCollector?.RecordBinancePing(
                stopwatch.Elapsed,
                TryReadRateLimitUsage(response),
                observedAtUtc);
        }
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
                $"Binance request timestamp was rejected. Status={snapshot.StatusCode}; DriftMs={driftText}; OffsetMs={snapshot.OffsetMilliseconds.ToString(CultureInfo.InvariantCulture)}; LastSyncUtc={lastSyncText}.");
        }

        throw new InvalidOperationException(defaultMessage);
    }

    private async Task ThrowOrderPlacementFailureAsync(
        int httpStatusCode,
        string? exchangeCode,
        string? responseBody,
        CancellationToken cancellationToken)
    {
        if (IsClockDriftResponse(exchangeCode, responseBody))
        {
            await ThrowClockDriftAwareFailureAsync(
                $"Binance order placement request failed with status {httpStatusCode}.",
                exchangeCode,
                responseBody,
                cancellationToken);
        }

        var exchangeMessage = SanitizeExchangeMessage(TryReadExchangeMessage(responseBody));
        var failureCode = ResolveOrderPlacementFailureCode(exchangeCode, exchangeMessage);
        var safeDetail = BuildOrderPlacementFailureDetail(httpStatusCode, exchangeCode, exchangeMessage);
        throw new BinanceExchangeRejectedException(failureCode, safeDetail, exchangeCode, httpStatusCode);
    }

    private static string ResolveOrderPlacementFailureCode(string? exchangeCode, string? exchangeMessage)
    {
        return string.Equals(exchangeCode, "-2019", StringComparison.Ordinal) ||
               exchangeMessage?.Contains("insufficient", StringComparison.OrdinalIgnoreCase) == true
            ? "FuturesMarginInsufficient"
            : "ExchangeRejected";
    }

    private static string BuildOrderPlacementFailureDetail(
        int httpStatusCode,
        string? exchangeCode,
        string? exchangeMessage)
    {
        if (!string.IsNullOrWhiteSpace(exchangeCode) && !string.IsNullOrWhiteSpace(exchangeMessage))
        {
            return $"Binance futures order rejected with exchange code {exchangeCode} ({exchangeMessage}).";
        }

        if (!string.IsNullOrWhiteSpace(exchangeCode))
        {
            return $"Binance futures order rejected with exchange code {exchangeCode} and HTTP status {httpStatusCode}.";
        }

        if (!string.IsNullOrWhiteSpace(exchangeMessage))
        {
            return $"Binance futures order rejected with HTTP status {httpStatusCode} ({exchangeMessage}).";
        }

        return $"Binance futures order rejected with HTTP status {httpStatusCode}.";
    }

    private static string? SanitizeExchangeMessage(string? exchangeMessage)
    {
        if (string.IsNullOrWhiteSpace(exchangeMessage))
        {
            return null;
        }

        var builder = new StringBuilder(exchangeMessage.Length);

        foreach (var character in exchangeMessage)
        {
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        var normalized = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized.Length <= 180
                ? normalized
                : normalized[..180];
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

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.##################", CultureInfo.InvariantCulture);
    }

    private static IReadOnlyCollection<ExchangeBalanceSnapshot> ParseBalances(JsonElement root, DateTime fallbackTimestampUtc)
    {
        if (!root.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ExchangeBalanceSnapshot>();
        }

        var balances = new List<ExchangeBalanceSnapshot>();

        foreach (var assetElement in assetsElement.EnumerateArray())
        {
            var asset = assetElement.TryGetProperty("asset", out var assetNameElement)
                ? NormalizeCode(assetNameElement.GetString())
                : null;
            var walletBalance = TryReadDecimal(assetElement, "walletBalance");
            var crossWalletBalance = TryReadDecimal(assetElement, "crossWalletBalance");
            var availableBalance = TryReadDecimal(assetElement, "availableBalance");
            var maxWithdrawAmount = TryReadDecimal(assetElement, "maxWithdrawAmount");
            var exchangeUpdatedAtUtc = TryReadUnixMilliseconds(assetElement, "updateTime", fallbackTimestampUtc);

            if (string.IsNullOrWhiteSpace(asset) ||
                walletBalance is null ||
                crossWalletBalance is null)
            {
                continue;
            }

            var snapshot = new ExchangeBalanceSnapshot(
                asset,
                walletBalance.Value,
                crossWalletBalance.Value,
                availableBalance,
                maxWithdrawAmount,
                exchangeUpdatedAtUtc);

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

    private static IReadOnlyCollection<ExchangePositionSnapshot> ParsePositions(JsonElement root, DateTime fallbackTimestampUtc)
    {
        if (!root.TryGetProperty("positions", out var positionsElement) || positionsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ExchangePositionSnapshot>();
        }

        var positions = new List<ExchangePositionSnapshot>();

        foreach (var positionElement in positionsElement.EnumerateArray())
        {
            var symbol = positionElement.TryGetProperty("symbol", out var symbolElement)
                ? NormalizeCode(symbolElement.GetString())
                : null;
            var positionSide = positionElement.TryGetProperty("positionSide", out var positionSideElement)
                ? NormalizeCode(positionSideElement.GetString())
                : null;
            var marginType = positionElement.TryGetProperty("marginType", out var marginTypeElement)
                ? NormalizeMarginType(marginTypeElement.GetString())
                : "cross";
            var quantity = TryReadDecimal(positionElement, "positionAmt");
            var entryPrice = TryReadDecimal(positionElement, "entryPrice");
            var breakEvenPrice = TryReadDecimal(positionElement, "breakEvenPrice");
            var unrealizedProfit = TryReadDecimal(positionElement, "unrealizedProfit");
            var isolatedWallet = TryReadDecimal(positionElement, "isolatedWallet");
            var exchangeUpdatedAtUtc = TryReadUnixMilliseconds(positionElement, "updateTime", fallbackTimestampUtc);

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

            var snapshot = new ExchangePositionSnapshot(
                symbol,
                positionSide,
                quantity.Value,
                entryPrice.Value,
                breakEvenPrice.Value,
                unrealizedProfit.Value,
                marginType,
                isolatedWallet.Value,
                exchangeUpdatedAtUtc);

            if (IsFlatPosition(snapshot))
            {
                continue;
            }

            positions.Add(snapshot);
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

    private static bool IsEmptyBalance(ExchangeBalanceSnapshot snapshot)
    {
        return snapshot.WalletBalance == 0m &&
               snapshot.CrossWalletBalance == 0m &&
               (snapshot.AvailableBalance ?? 0m) == 0m &&
               (snapshot.MaxWithdrawAmount ?? 0m) == 0m;
    }

    private static bool IsFlatPosition(ExchangePositionSnapshot snapshot)
    {
        return snapshot.Quantity == 0m &&
               snapshot.EntryPrice == 0m &&
               snapshot.BreakEvenPrice == 0m &&
               snapshot.UnrealizedProfit == 0m &&
               snapshot.IsolatedWallet == 0m;
    }

    private static int? TryReadRateLimitUsage(HttpResponseMessage? response)
    {
        if (response is null)
        {
            return null;
        }

        var headerNames = new[]
        {
            "X-MBX-USED-WEIGHT-1M",
            "X-MBX-USED-WEIGHT",
            "X-MBX-ORDER-COUNT-10S",
            "X-MBX-ORDER-COUNT-1M"
        };

        foreach (var headerName in headerNames)
        {
            if (!response.Headers.TryGetValues(headerName, out var values))
            {
                continue;
            }

            foreach (var value in values)
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue))
                {
                    return parsedValue;
                }
            }
        }

        return null;
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

    private Task WriteExecutionTraceAsync(
        BinanceOrderPlacementRequest request,
        string? maskedRequest,
        string? maskedResponse,
        int? httpStatusCode,
        string? exchangeCode,
        long latencyMs,
        CancellationToken cancellationToken)
    {
        return WriteExecutionTraceAsync(
            new ExecutionTraceWriteRequest(
                request.CommandId ?? request.ClientOrderId,
                request.UserId ?? "system:unknown",
                "Binance.PrivateRest",
                "/fapi/v1/order",
                maskedRequest,
                maskedResponse,
                request.CorrelationId,
                request.ExecutionAttemptId,
                request.ExecutionOrderId,
                httpStatusCode,
                exchangeCode,
                latencyMs > int.MaxValue ? int.MaxValue : (int)latencyMs),
            cancellationToken);
    }

    private Task WriteExecutionTraceAsync(
        BinanceOrderCancelRequest request,
        string? maskedRequest,
        string? maskedResponse,
        int? httpStatusCode,
        string? exchangeCode,
        long latencyMs,
        CancellationToken cancellationToken)
    {
        return WriteExecutionTraceAsync(
            new ExecutionTraceWriteRequest(
                request.CommandId ?? request.ClientOrderId ?? request.ExchangeOrderId ?? "cancel:unknown",
                request.UserId ?? "system:unknown",
                "Binance.PrivateRest",
                "/fapi/v1/order",
                maskedRequest,
                maskedResponse,
                request.CorrelationId,
                ExecutionAttemptId: null,
                request.ExecutionOrderId,
                httpStatusCode,
                exchangeCode,
                latencyMs > int.MaxValue ? int.MaxValue : (int)latencyMs),
            cancellationToken);
    }

    private async Task WriteExecutionTraceAsync(
        ExecutionTraceWriteRequest request,
        CancellationToken cancellationToken)
    {
        if (serviceScopeFactory is null)
        {
            return;
        }

        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var traceService = scope.ServiceProvider.GetService<ITraceService>();

        if (traceService is null)
        {
            return;
        }

        await traceService.WriteExecutionTraceAsync(request, cancellationToken);
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
        var cumulativeQuoteQuantity = TryReadDecimal(root, "cumQuote") ?? 0m;
        var averagePrice = TryReadDecimal(root, "avgPrice") ?? 0m;
        var updatedAtUtc = TryReadUnixMilliseconds(root, "updateTime", observedAtUtc);

        if (string.IsNullOrWhiteSpace(orderId) ||
            string.IsNullOrWhiteSpace(clientOrderId) ||
            string.IsNullOrWhiteSpace(status))
        {
            throw new InvalidOperationException("Binance order status response was incomplete.");
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
            source);
    }

    private static BinanceOrderStatusSnapshot? TryBuildPlacementSnapshot(
        JsonElement root,
        string requestedSymbol,
        DateTime observedAtUtc)
    {
        return root.TryGetProperty("status", out _) &&
               root.TryGetProperty("orderId", out _) &&
               root.TryGetProperty("clientOrderId", out _)
            ? BuildOrderStatusSnapshot(root, requestedSymbol, observedAtUtc, "Binance.PrivateRest.OrderPlacement")
            : null;
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

