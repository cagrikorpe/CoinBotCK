using System.ComponentModel.DataAnnotations;

namespace CoinBot.Infrastructure.Execution;

public sealed class DataLatencyGuardOptions
{
    [Range(1, 60)]
    public int StaleDataThresholdSeconds { get; set; } = 3;

    [Range(1, 300)]
    public int StopDataThresholdSeconds { get; set; } = 6;

    [Range(1, 60)]
    public int ClockDriftThresholdSeconds { get; set; } = 5;
}
