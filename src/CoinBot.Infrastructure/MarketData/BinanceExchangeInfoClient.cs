using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoinBot.Application.Abstractions.Autonomy;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.MarketData;

public sealed class BinanceExchangeInfoClient(
    HttpClient httpClient,
    TimeProvider timeProvider,
    ILogger<BinanceExchangeInfoClient> logger,
    IMonitoringTelemetryCollector? monitoringTelemetryCollector = null,
    IServiceScopeFactory? serviceScopeFactory = null) : IBinanceExchangeInfoClient
{
    private const string BreakerActor = "system:rest-market-data";
    private static readonly TimeSpan ServerTimeProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyCollection<SymbolMetadataSnapshot>> GetSymbolMetadataAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        var normalizedSymbols = MarketDataSymbolNormalizer.NormalizeMany(symbols);

        if (normalizedSymbols.Count == 0)
        {
            return Array.Empty<SymbolMetadataSnapshot>();
        }

        var symbolsPayload = Uri.EscapeDataString(JsonSerializer.Serialize(normalizedSymbols, SerializerOptions));
        var observedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        IReadOnlyCollection<SymbolMetadataSnapshot> results = [];

        try
        {
            response = await httpClient.GetAsync(
                $"api/v3/exchangeInfo?symbols={symbolsPayload}",
                cancellationToken);

            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<BinanceExchangeInfoResponse>(
                responseStream,
                SerializerOptions,
                cancellationToken);
            var refreshedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            results = payload?.Symbols?
                .Select(symbol => TryMapSymbol(symbol, refreshedAtUtc))
                .OfType<SymbolMetadataSnapshot>()
                .OrderBy(snapshot => snapshot.Symbol, StringComparer.Ordinal)
                .ToArray()
                ?? [];

            await RecordBreakerSuccessAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordBreakerFailureAsync(exception, cancellationToken);

            throw;
        }
        finally
        {
            stopwatch.Stop();
            monitoringTelemetryCollector?.RecordBinancePing(
                stopwatch.Elapsed,
                TryReadRateLimitUsage(response),
                observedAtUtc);
            response?.Dispose();
        }

        logger.LogInformation(
            "Binance symbol metadata refresh returned {SymbolCount} symbols.",
            results.Count);

        return results;
    }

    public async Task<DateTime?> GetServerTimeUtcAsync(CancellationToken cancellationToken = default)
    {
        HttpResponseMessage? response = null;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ServerTimeProbeTimeout);

        try
        {
            response = await httpClient.GetAsync("api/v3/time", timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug(
                    "Binance server time probe returned HTTP {StatusCode}.",
                    (int)response.StatusCode);

                return null;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<BinanceServerTimeResponse>(
                responseStream,
                SerializerOptions,
                cancellationToken);

            if (payload is null || payload.ServerTime <= 0)
            {
                return null;
            }

            return DateTimeOffset.FromUnixTimeMilliseconds(payload.ServerTime).UtcDateTime;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Binance server time probe failed.");

            return null;
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }

    private async Task RecordBreakerSuccessAsync(CancellationToken cancellationToken)
    {
        if (serviceScopeFactory is null)
        {
            return;
        }

        using var scope = serviceScopeFactory.CreateScope();
        var dependencyCircuitBreakerStateManager = scope.ServiceProvider.GetService<IDependencyCircuitBreakerStateManager>();

        if (dependencyCircuitBreakerStateManager is null)
        {
            return;
        }

        await dependencyCircuitBreakerStateManager.RecordSuccessAsync(
            new DependencyCircuitBreakerSuccessRequest(
                DependencyCircuitBreakerKind.RestMarketData,
                BreakerActor),
            cancellationToken);
    }

    private async Task RecordBreakerFailureAsync(Exception exception, CancellationToken cancellationToken)
    {
        if (serviceScopeFactory is null)
        {
            return;
        }

        using var scope = serviceScopeFactory.CreateScope();
        var dependencyCircuitBreakerStateManager = scope.ServiceProvider.GetService<IDependencyCircuitBreakerStateManager>();

        if (dependencyCircuitBreakerStateManager is null)
        {
            return;
        }

        await dependencyCircuitBreakerStateManager.RecordFailureAsync(
            new DependencyCircuitBreakerFailureRequest(
                DependencyCircuitBreakerKind.RestMarketData,
                BreakerActor,
                exception.GetType().Name,
                Truncate(exception.Message, 512) ?? "Exchange info request failed."),
            cancellationToken);
    }

    private static SymbolMetadataSnapshot? TryMapSymbol(BinanceSymbolPayload symbol, DateTime refreshedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(symbol.Symbol) ||
            string.IsNullOrWhiteSpace(symbol.BaseAsset) ||
            string.IsNullOrWhiteSpace(symbol.QuoteAsset))
        {
            return null;
        }

        var tickSize = TryReadFilterDecimal(symbol.Filters, "PRICE_FILTER", filter => filter.TickSize);
        var stepSize = TryReadFilterDecimal(symbol.Filters, "LOT_SIZE", filter => filter.StepSize);

        if (tickSize is null || stepSize is null)
        {
            return null;
        }

        var tradingStatus = string.IsNullOrWhiteSpace(symbol.Status)
            ? "UNKNOWN"
            : symbol.Status.Trim();

        return new SymbolMetadataSnapshot(
            MarketDataSymbolNormalizer.Normalize(symbol.Symbol),
            Exchange: "Binance",
            BaseAsset: symbol.BaseAsset.Trim(),
            QuoteAsset: symbol.QuoteAsset.Trim(),
            TickSize: tickSize.Value,
            StepSize: stepSize.Value,
            TradingStatus: tradingStatus,
            IsTradingEnabled: string.Equals(tradingStatus, "TRADING", StringComparison.OrdinalIgnoreCase),
            RefreshedAtUtc: NormalizeTimestamp(refreshedAtUtc));
    }

    private static decimal? TryReadFilterDecimal(
        IReadOnlyCollection<BinanceFilterPayload>? filters,
        string filterType,
        Func<BinanceFilterPayload, string?> selector)
    {
        if (filters is null)
        {
            return null;
        }

        var matchingFilter = filters.FirstOrDefault(filter =>
            string.Equals(filter.FilterType, filterType, StringComparison.Ordinal));
        var filterValue = matchingFilter is null
            ? null
            : selector(matchingFilter);

        if (string.IsNullOrWhiteSpace(filterValue))
        {
            return null;
        }

        return decimal.TryParse(filterValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
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

    private sealed record BinanceExchangeInfoResponse(
        [property: JsonPropertyName("symbols")] IReadOnlyCollection<BinanceSymbolPayload>? Symbols);

    private sealed record BinanceServerTimeResponse(
        [property: JsonPropertyName("serverTime")] long ServerTime);

    private sealed record BinanceSymbolPayload(
        [property: JsonPropertyName("symbol")] string? Symbol,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("baseAsset")] string? BaseAsset,
        [property: JsonPropertyName("quoteAsset")] string? QuoteAsset,
        [property: JsonPropertyName("filters")] IReadOnlyCollection<BinanceFilterPayload>? Filters);

    private sealed record BinanceFilterPayload(
        [property: JsonPropertyName("filterType")] string? FilterType,
        [property: JsonPropertyName("tickSize")] string? TickSize,
        [property: JsonPropertyName("stepSize")] string? StepSize);
}
