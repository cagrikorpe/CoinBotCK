using CoinBot.Application.Abstractions.MarketData;

namespace CoinBot.Infrastructure.MarketData;

internal static class SharedMarketDataCacheKeyBuilder
{
    private const string Prefix = "coinbot:market-data:v1";
    private const string DefaultTimeframe = "spot";

    public static string Build(
        SharedMarketDataCacheDataType dataType,
        string symbol,
        string? timeframe)
    {
        var normalizedSymbol = MarketDataSymbolNormalizer.Normalize(symbol);
        var normalizedTimeframe = NormalizeTimeframe(dataType, timeframe);
        var @namespace = dataType switch
        {
            SharedMarketDataCacheDataType.Kline => "kline",
            SharedMarketDataCacheDataType.Ticker => "ticker",
            SharedMarketDataCacheDataType.Depth => "depth",
            _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, "Unsupported market data cache type.")
        };

        return $"{Prefix}:{@namespace}:{normalizedSymbol}:{normalizedTimeframe}";
    }

    public static string? NormalizeTimeframe(SharedMarketDataCacheDataType dataType, string? timeframe)
    {
        if (dataType == SharedMarketDataCacheDataType.Kline)
        {
            var normalizedRequiredTimeframe = timeframe?.Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(normalizedRequiredTimeframe))
            {
                throw new ArgumentException("The timeframe is required for kline cache entries.", nameof(timeframe));
            }

            return normalizedRequiredTimeframe;
        }

        var normalizedTimeframe = timeframe?.Trim().ToLowerInvariant();

        return string.IsNullOrWhiteSpace(normalizedTimeframe)
            ? DefaultTimeframe
            : normalizedTimeframe;
    }
}
