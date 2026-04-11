using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.MarketData;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Exchange;

public sealed class BinanceCredentialProbeClient(
    IHttpClientFactory httpClientFactory,
    IOptions<BinanceMarketDataOptions> marketDataOptions,
    IOptions<BinancePrivateDataOptions> privateDataOptions,
    TimeProvider timeProvider,
    IBinanceTimeSyncService timeSyncService) : IBinanceCredentialProbeClient
{
    private const string SpotClientName = "BinanceCredentialProbeSpot";
    private const string FuturesClientName = "BinanceCredentialProbeFutures";
    private const string LiveSpotProbeBaseUrl = "https://api.binance.com";
    private const string DemoSpotProbeBaseUrl = "https://testnet.binance.vision";
    private const string LiveFuturesProbeBaseUrl = "https://fapi.binance.com";
    private const string DemoFuturesProbeBaseUrl = "https://testnet.binancefuture.com";
    private readonly BinanceMarketDataOptions marketDataOptionsValue = marketDataOptions.Value;
    private readonly BinancePrivateDataOptions privateDataOptionsValue = privateDataOptions.Value;

    public Task<BinanceCredentialProbeSnapshot> ProbeAsync(
        string apiKey,
        string apiSecret,
        CancellationToken cancellationToken = default)
    {
        return ProbeAsyncCore(apiKey, apiSecret, null, null, cancellationToken);
    }

    public Task<BinanceCredentialProbeSnapshot> ProbeAsync(
        string apiKey,
        string apiSecret,
        ExecutionEnvironment requestedEnvironment,
        ExchangeTradeModeSelection requestedTradeMode,
        CancellationToken cancellationToken = default)
    {
        return ProbeAsyncCore(apiKey, apiSecret, requestedEnvironment, requestedTradeMode, cancellationToken);
    }

    private async Task<BinanceCredentialProbeSnapshot> ProbeAsyncCore(
        string apiKey,
        string apiSecret,
        ExecutionEnvironment? requestedEnvironment,
        ExchangeTradeModeSelection? requestedTradeMode,
        CancellationToken cancellationToken)
    {
        var normalizedApiKey = NormalizeRequired(apiKey, nameof(apiKey));
        var normalizedApiSecret = NormalizeRequired(apiSecret, nameof(apiSecret));
        var spotProbeBaseUrl = ResolveSpotProbeBaseUrl(requestedEnvironment);
        var futuresProbeBaseUrl = ResolveFuturesProbeBaseUrl(requestedEnvironment);
        var spotEnvironmentScope = InferEnvironmentScope(spotProbeBaseUrl);
        var futuresEnvironmentScope = InferEnvironmentScope(futuresProbeBaseUrl);

        var spotProbe = ShouldProbeSpot(requestedTradeMode)
            ? await ProbeSpotAsync(normalizedApiKey, normalizedApiSecret, spotProbeBaseUrl, cancellationToken)
            : EndpointProbeResult.NotRequested();
        var futuresProbe = ShouldProbeFutures(requestedTradeMode)
            ? await ProbeFuturesAsync(normalizedApiKey, normalizedApiSecret, futuresProbeBaseUrl, cancellationToken)
            : EndpointProbeResult.NotRequested();

        return new BinanceCredentialProbeSnapshot(
            IsKeyValid: spotProbe.IsSuccess || futuresProbe.IsSuccess,
            CanTrade: spotProbe.CanTrade || futuresProbe.CanTrade,
            CanWithdraw: spotProbe.CanWithdraw,
            SupportsSpot: spotProbe.SupportsSpot,
            SupportsFutures: futuresProbe.SupportsFutures,
            HasTimestampSkew: spotProbe.HasTimestampSkew || futuresProbe.HasTimestampSkew,
            HasIpRestrictionIssue: spotProbe.HasIpRestrictionIssue || futuresProbe.HasIpRestrictionIssue,
            SpotEnvironmentScope: spotEnvironmentScope,
            FuturesEnvironmentScope: futuresEnvironmentScope,
            PermissionSummary: BuildPermissionSummary(spotProbe, futuresProbe, spotEnvironmentScope, futuresEnvironmentScope),
            SafeFailureReason: ResolveSafeFailureReason(spotProbe, futuresProbe));
    }
    private async Task<EndpointProbeResult> ProbeSpotAsync(
        string apiKey,
        string apiSecret,
        string probeBaseUrl,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(SpotClientName);
        string path;

        try
        {
            path = await CreateSignedPathAsync("/api/v3/account", apiSecret, cancellationToken);
        }
        catch (BinanceClockDriftException)
        {
            return new EndpointProbeResult(
                IsSuccess: false,
                CanTrade: false,
                CanWithdraw: null,
                SupportsSpot: false,
                SupportsFutures: false,
                HasTimestampSkew: true,
                HasIpRestrictionIssue: false,
                SafeFailureReason: "Binance server-time offset üretilemedi.");
        }

        using var request = CreateApiKeyRequest(path, apiKey, probeBaseUrl);
        using var response = await client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var failure = ParseFailure(responseBody, response.StatusCode);

            if (failure.HasTimestampSkew)
            {
                await timeSyncService.GetSnapshotAsync(forceRefresh: true, cancellationToken);
            }

            return failure;
        }

        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        var supportsSpot = true;
        var canTrade = TryReadBoolean(root, "canTrade") ?? false;
        var canWithdraw = TryReadBoolean(root, "canWithdraw");

        if (root.TryGetProperty("permissions", out var permissionsElement) &&
            permissionsElement.ValueKind == JsonValueKind.Array)
        {
            supportsSpot = permissionsElement
                .EnumerateArray()
                .Select(element => element.GetString())
                .Any(permission => string.Equals(permission, "SPOT", StringComparison.OrdinalIgnoreCase));
        }

        return EndpointProbeResult.Success(
            canTrade: canTrade,
            canWithdraw: canWithdraw,
            supportsSpot: supportsSpot,
            supportsFutures: false);
    }
    private async Task<EndpointProbeResult> ProbeFuturesAsync(
        string apiKey,
        string apiSecret,
        string probeBaseUrl,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(FuturesClientName);
        string path;

        try
        {
            path = await CreateSignedPathAsync("/fapi/v3/account", apiSecret, cancellationToken);
        }
        catch (BinanceClockDriftException)
        {
            return new EndpointProbeResult(
                IsSuccess: false,
                CanTrade: false,
                CanWithdraw: null,
                SupportsSpot: false,
                SupportsFutures: false,
                HasTimestampSkew: true,
                HasIpRestrictionIssue: false,
                SafeFailureReason: "Binance server-time offset üretilemedi.");
        }

        using var request = CreateApiKeyRequest(path, apiKey, probeBaseUrl);
        using var response = await client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var failure = ParseFailure(responseBody, response.StatusCode);

            if (failure.HasTimestampSkew)
            {
                await timeSyncService.GetSnapshotAsync(forceRefresh: true, cancellationToken);
            }

            return failure;
        }

        return EndpointProbeResult.Success(
            canTrade: true,
            canWithdraw: null,
            supportsSpot: false,
            supportsFutures: true);
    }
    private async Task<string> CreateSignedPathAsync(
        string resourcePath,
        string apiSecret,
        CancellationToken cancellationToken)
    {
        var timestamp = (await timeSyncService.GetCurrentTimestampMillisecondsAsync(cancellationToken))
            .ToString(CultureInfo.InvariantCulture);
        var unsignedQuery = $"timestamp={timestamp}&recvWindow={privateDataOptionsValue.RecvWindowMilliseconds.ToString(CultureInfo.InvariantCulture)}";
        var signature = ComputeSignature(unsignedQuery, apiSecret);
        return $"{resourcePath}?{unsignedQuery}&signature={signature}";
    }

    private static HttpRequestMessage CreateApiKeyRequest(string path, string apiKey, string probeBaseUrl)
    {
        var normalizedBaseUrl = probeBaseUrl.EndsWith("/", StringComparison.Ordinal)
            ? probeBaseUrl
            : $"{probeBaseUrl}/";
        var requestUri = new Uri(new Uri(normalizedBaseUrl, UriKind.Absolute), path.TrimStart('/'));
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("X-MBX-APIKEY", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private string ResolveSpotProbeBaseUrl(ExecutionEnvironment? requestedEnvironment)
    {
        return requestedEnvironment switch
        {
            ExecutionEnvironment.Demo => DemoSpotProbeBaseUrl,
            ExecutionEnvironment.Live => LiveSpotProbeBaseUrl,
            _ => NormalizeBaseUrl(privateDataOptionsValue.SpotRestBaseUrl, marketDataOptionsValue.RestBaseUrl, LiveSpotProbeBaseUrl)
        };
    }

    private string ResolveFuturesProbeBaseUrl(ExecutionEnvironment? requestedEnvironment)
    {
        return requestedEnvironment switch
        {
            ExecutionEnvironment.Demo => DemoFuturesProbeBaseUrl,
            ExecutionEnvironment.Live => LiveFuturesProbeBaseUrl,
            _ => NormalizeBaseUrl(privateDataOptionsValue.RestBaseUrl, LiveFuturesProbeBaseUrl)
        };
    }

    private static string NormalizeBaseUrl(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var normalizedCandidate = candidate?.Trim();

            if (!string.IsNullOrWhiteSpace(normalizedCandidate))
            {
                return normalizedCandidate;
            }
        }

        return LiveFuturesProbeBaseUrl;
    }

    private static bool ShouldProbeSpot(ExchangeTradeModeSelection? requestedTradeMode)
    {
        return requestedTradeMode is null or ExchangeTradeModeSelection.Spot or ExchangeTradeModeSelection.Both;
    }

    private static bool ShouldProbeFutures(ExchangeTradeModeSelection? requestedTradeMode)
    {
        return requestedTradeMode is null or ExchangeTradeModeSelection.Futures or ExchangeTradeModeSelection.Both;
    }

    private static EndpointProbeResult ParseFailure(string responseBody, System.Net.HttpStatusCode statusCode)
    {
        var (code, message) = ParseError(responseBody);
        var normalizedMessage = message ?? $"Binance isteği {(int)statusCode} durumu ile reddedildi.";
        var hasTimestampSkew = code == "-1021" ||
                               normalizedMessage.Contains("timestamp", StringComparison.OrdinalIgnoreCase) ||
                               normalizedMessage.Contains("recvWindow", StringComparison.OrdinalIgnoreCase);
        var hasIpRestrictionIssue = IsExplicitIpRestriction(code, normalizedMessage);

        return new EndpointProbeResult(
            IsSuccess: false,
            CanTrade: false,
            CanWithdraw: null,
            SupportsSpot: false,
            SupportsFutures: false,
            HasTimestampSkew: hasTimestampSkew,
            HasIpRestrictionIssue: hasIpRestrictionIssue,
            SafeFailureReason: hasTimestampSkew
                ? "Binance zaman damgası doğrulamayı reddetti."
                : hasIpRestrictionIssue
                    ? "Binance IP kısıtı nedeniyle doğrulamayı reddetti."
                    : MapFailureReason(code, normalizedMessage));
    }

    private static (string? Code, string? Message) ParseError(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var codeElement)
                ? codeElement.ToString()
                : null;
            var message = root.TryGetProperty("msg", out var messageElement)
                ? messageElement.GetString()?.Trim()
                : null;
            return (code, message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string MapFailureReason(string? code, string message)
    {
        if (string.Equals(code, "-2014", StringComparison.Ordinal) ||
            string.Equals(code, "-2015", StringComparison.Ordinal) ||
            message.Contains("API-key", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("signature", StringComparison.OrdinalIgnoreCase))
        {
            return "API key, secret veya gerekli izinler doğrulanamadı.";
        }

        if (message.Contains("permissions", StringComparison.OrdinalIgnoreCase))
        {
            return "Binance gerekli hesap izinlerini doğrulamadı.";
        }

        return "Binance bağlantısı doğrulanamadı.";
    }

    private static string BuildPermissionSummary(
        EndpointProbeResult spotProbe,
        EndpointProbeResult futuresProbe,
        string spotEnvironmentScope,
        string futuresEnvironmentScope)
    {
        var environmentScope = string.Equals(spotEnvironmentScope, futuresEnvironmentScope, StringComparison.OrdinalIgnoreCase)
            ? spotEnvironmentScope
            : "Mixed";

        return $"Trade={(spotProbe.CanTrade || futuresProbe.CanTrade ? "Y" : "N")}; Withdraw={FormatNullableBoolean(spotProbe.CanWithdraw)}; Spot={(spotProbe.SupportsSpot ? "Y" : "N")}; Futures={(futuresProbe.SupportsFutures ? "Y" : "N")}; Env={environmentScope}";
    }

    private static string? ResolveSafeFailureReason(EndpointProbeResult spotProbe, EndpointProbeResult futuresProbe)
    {
        if (spotProbe.IsSuccess && futuresProbe.IsSuccess)
        {
            return null;
        }

        if (!spotProbe.IsSuccess && !string.IsNullOrWhiteSpace(spotProbe.SafeFailureReason))
        {
            return spotProbe.SafeFailureReason;
        }

        if (!futuresProbe.IsSuccess && !string.IsNullOrWhiteSpace(futuresProbe.SafeFailureReason))
        {
            return futuresProbe.SafeFailureReason;
        }

        return null;
    }

    private static string InferEnvironmentScope(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return "Unknown";
        }

        var normalized = baseUrl.Trim();
        return normalized.Contains("testnet", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("demo", StringComparison.OrdinalIgnoreCase)
            ? "Demo"
            : "Live";
    }

    private static bool? TryReadBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(element.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string FormatNullableBoolean(bool? value)
    {
        return value switch
        {
            true => "Y",
            false => "N",
            _ => "?"
        };
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

    private static bool IsExplicitIpRestriction(string? code, string message)
    {
        if (string.Equals(code, "-2015", StringComparison.Ordinal) &&
            message.Contains("API-key, IP, or permissions", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return message.Contains("whitelist", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("not in the ip", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("ip address", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("restricted to", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record EndpointProbeResult(
        bool IsSuccess,
        bool CanTrade,
        bool? CanWithdraw,
        bool SupportsSpot,
        bool SupportsFutures,
        bool HasTimestampSkew,
        bool HasIpRestrictionIssue,
        string? SafeFailureReason)
    {
        public static EndpointProbeResult NotRequested()
        {
            return new EndpointProbeResult(
                IsSuccess: false,
                CanTrade: false,
                CanWithdraw: null,
                SupportsSpot: false,
                SupportsFutures: false,
                HasTimestampSkew: false,
                HasIpRestrictionIssue: false,
                SafeFailureReason: null);
        }

        public static EndpointProbeResult Success(
            bool canTrade,
            bool? canWithdraw,
            bool supportsSpot,
            bool supportsFutures)
        {
            return new EndpointProbeResult(
                IsSuccess: true,
                CanTrade: canTrade,
                CanWithdraw: canWithdraw,
                SupportsSpot: supportsSpot,
                SupportsFutures: supportsFutures,
                HasTimestampSkew: false,
                HasIpRestrictionIssue: false,
                SafeFailureReason: null);
        }
    }
}
