using System.ComponentModel.DataAnnotations;

namespace CoinBot.Infrastructure.MarketData;

public sealed class MarketScannerOptions
{
    public bool Enabled { get; set; } = true;

    [Range(5, 300)]
    public int ScanIntervalSeconds { get; set; } = 30;

    [Range(1, 500)]
    public int MaxUniverseSymbols { get; set; } = 100;

    [Range(1, 100)]
    public int TopCandidateCount { get; set; } = 10;

    [Range(1, 168)]
    public int VolumeLookbackHours { get; set; } = 24;

    [Range(1, 86400)]
    public int MaxDataAgeSeconds { get; set; } = 120;

    [Range(typeof(decimal), "0", "1000000000000000")]
    public decimal Min24hQuoteVolume { get; set; } = 1_000_000m;

    [Range(typeof(decimal), "0", "1000000000000000")]
    public decimal MinPrice { get; set; } = 0.00000001m;

    [Range(typeof(decimal), "0", "1000000000000000")]
    public decimal MaxPrice { get; set; } = 1_000_000m;

    [Range(typeof(decimal), "0", "1000000000000000")]
    public decimal StrategyScoreWeight { get; set; } = 1_000m;

    public string[] AllowedQuoteAssets { get; set; } = ["USDT"];

    public bool HandoffEnabled { get; set; } = true;
}
