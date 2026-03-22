using System.ComponentModel.DataAnnotations;

namespace CoinBot.Infrastructure.MarketData;

public sealed class HistoricalGapFillerOptions
{
    public bool Enabled { get; set; }

    [Range(1, 1440)]
    public int ScanIntervalMinutes { get; set; } = 5;

    [Range(1, 10080)]
    public int LookbackCandles { get; set; } = 1440;

    [Range(1, 1000)]
    public int MaxCandlesPerRequest { get; set; } = 500;

    [Range(0, 10)]
    public int MaxRetryAttempts { get; set; } = 3;

    [Range(1, 300)]
    public int RetryDelaySeconds { get; set; } = 2;

    public string[] Symbols { get; set; } = [];
}
