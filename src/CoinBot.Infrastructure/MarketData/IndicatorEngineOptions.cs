using System.ComponentModel.DataAnnotations;

namespace CoinBot.Infrastructure.MarketData;

public sealed class IndicatorEngineOptions
{
    [Range(2, 200)]
    public int RsiPeriod { get; set; } = 14;

    [Range(2, 200)]
    public int MacdFastPeriod { get; set; } = 12;

    [Range(3, 400)]
    public int MacdSlowPeriod { get; set; } = 26;

    [Range(2, 200)]
    public int MacdSignalPeriod { get; set; } = 9;

    [Range(2, 200)]
    public int BollingerPeriod { get; set; } = 20;

    [Range(typeof(decimal), "0.1", "10")]
    public decimal BollingerStandardDeviationMultiplier { get; set; } = 2m;

    internal int GetRequiredSampleCount()
    {
        return Math.Max(
            checked(RsiPeriod + 1),
            Math.Max(
                checked(MacdSlowPeriod + MacdSignalPeriod - 1),
                BollingerPeriod));
    }
}
