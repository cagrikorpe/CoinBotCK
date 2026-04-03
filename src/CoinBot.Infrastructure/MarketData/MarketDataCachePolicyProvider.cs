using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.MarketData;

public sealed class MarketDataCachePolicyProvider(IOptions<InMemoryCacheOptions> options)
{
    private readonly InMemoryCacheOptions optionsValue = options.Value;

    public MemoryCacheEntryOptions CreateSymbolMetadataOptions()
    {
        return new MemoryCacheEntryOptions()
            .SetSize(1)
            .SetAbsoluteExpiration(TimeSpan.FromMinutes(optionsValue.SymbolMetadataTtlMinutes));
    }

    public MemoryCacheEntryOptions CreateLatestPriceOptions()
    {
        return new MemoryCacheEntryOptions()
            .SetSize(1)
            .SetAbsoluteExpiration(TimeSpan.FromSeconds(optionsValue.LatestPriceTtlSeconds));
    }

    public TimeSpan GetLatestPriceFreshness()
    {
        return TimeSpan.FromSeconds(optionsValue.LatestPriceTtlSeconds);
    }

    public TimeSpan GetLatestPriceRetention()
    {
        var freshnessSeconds = Math.Max(1, optionsValue.LatestPriceTtlSeconds);
        return TimeSpan.FromSeconds(freshnessSeconds * 4);
    }
}
