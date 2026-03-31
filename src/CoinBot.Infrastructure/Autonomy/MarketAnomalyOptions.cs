using System.ComponentModel.DataAnnotations;

namespace CoinBot.Infrastructure.Autonomy;

public sealed class MarketAnomalyOptions
{
    [Range(5, 300)]
    public int SweepIntervalSeconds { get; set; } = 30;

    [Range(5, 240)]
    public int RecentSymbolWindowMinutes { get; set; } = 90;

    [Range(5, 120)]
    public int LookbackCandles { get; set; } = 30;

    [Range(4, 60)]
    public int MinimumHistoricalCandles { get; set; } = 20;

    [Range(typeof(decimal), "0.005", "1.0")]
    public decimal PriceShockModeratePercent { get; set; } = 0.03m;

    [Range(typeof(decimal), "0.005", "1.0")]
    public decimal PriceShockSeverePercent { get; set; } = 0.06m;

    [Range(typeof(decimal), "1.0", "50.0")]
    public decimal RangeRatioModerate { get; set; } = 2.5m;

    [Range(typeof(decimal), "1.0", "50.0")]
    public decimal RangeRatioSevere { get; set; } = 4.0m;

    [Range(typeof(decimal), "0.001", "1.0")]
    public decimal RangeFloorPercent { get; set; } = 0.02m;

    [Range(typeof(decimal), "0.001", "1.0")]
    public decimal VolumeRatioModerate { get; set; } = 0.35m;

    [Range(typeof(decimal), "0.001", "1.0")]
    public decimal VolumeRatioSevere { get; set; } = 0.20m;

    [Range(1, 300)]
    public int StaleDataWarningSeconds { get; set; } = 5;

    [Range(2, 600)]
    public int StaleDataCriticalSeconds { get; set; } = 12;

    [Range(typeof(decimal), "0.10", "1.0")]
    public decimal AutoApplyConfidenceThreshold { get; set; } = 0.88m;

    [Range(1, 500)]
    public int MaxSymbolsPerSweep { get; set; } = 50;
}
