using System.ComponentModel.DataAnnotations;

namespace CoinBot.Infrastructure.Administration;

public sealed class UltraDebugLogRetentionOptions
{
    public bool Enabled { get; set; } = true;

    [Range(1, 3650)]
    public int NormalRetentionDays { get; set; } = 30;

    [Range(1, 3650)]
    public int UltraRetentionDays { get; set; } = 7;

    [Range(1, 100)]
    public int MinimumKeepFileCount { get; set; } = 2;

    [Range(1, 5000)]
    public int MaxFilesPerRun { get; set; } = 500;

    [Range(5, 1440)]
    public int WorkerIntervalMinutes { get; set; } = 60;
}
