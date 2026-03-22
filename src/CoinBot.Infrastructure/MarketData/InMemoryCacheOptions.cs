using System.ComponentModel.DataAnnotations;

namespace CoinBot.Infrastructure.MarketData;

public sealed class InMemoryCacheOptions
{
    [Range(128, 65536)]
    public long SizeLimit { get; set; } = 2048;

    [Range(1, 1440)]
    public int SymbolMetadataTtlMinutes { get; set; } = 60;

    [Range(1, 600)]
    public int LatestPriceTtlSeconds { get; set; } = 15;
}
