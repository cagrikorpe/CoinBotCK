using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoinBot.Application.Abstractions.MarketData;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.MarketData;

public sealed class BinanceExchangeInfoClient(
    HttpClient httpClient,
    TimeProvider timeProvider,
    ILogger<BinanceExchangeInfoClient> logger) : IBinanceExchangeInfoClient
{
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
        using var response = await httpClient.GetAsync(
            $"api/v3/exchangeInfo?symbols={symbolsPayload}",
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<BinanceExchangeInfoResponse>(
            responseStream,
            SerializerOptions,
            cancellationToken);
        var refreshedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var results = payload?.Symbols?
            .Select(symbol => TryMapSymbol(symbol, refreshedAtUtc))
            .OfType<SymbolMetadataSnapshot>()
            .OrderBy(snapshot => snapshot.Symbol, StringComparer.Ordinal)
            .ToArray()
            ?? [];

        logger.LogInformation(
            "Binance symbol metadata refresh returned {SymbolCount} symbols.",
            results.Length);

        return results;
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

    private sealed record BinanceExchangeInfoResponse(
        [property: JsonPropertyName("symbols")] IReadOnlyCollection<BinanceSymbolPayload>? Symbols);

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
