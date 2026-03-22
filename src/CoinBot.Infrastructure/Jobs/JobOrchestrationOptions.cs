using System.ComponentModel.DataAnnotations;

namespace CoinBot.Infrastructure.Jobs;

public sealed class JobOrchestrationOptions
{
    public bool Enabled { get; set; }

    [Range(1, 300)]
    public int SchedulerPollIntervalSeconds { get; set; } = 5;

    [Range(1, 3600)]
    public int BotExecutionIntervalSeconds { get; set; } = 30;

    [Range(5, 600)]
    public int LeaseDurationSeconds { get; set; } = 30;

    [Range(1, 300)]
    public int KeepAliveIntervalSeconds { get; set; } = 10;

    [Range(5, 3600)]
    public int CleanupIntervalSeconds { get; set; } = 60;

    [Range(5, 3600)]
    public int WatchdogIntervalSeconds { get; set; } = 30;

    [Range(5, 7200)]
    public int WatchdogTimeoutSeconds { get; set; } = 90;

    [Range(1, 86400)]
    public int CleanupGracePeriodSeconds { get; set; } = 300;

    [Range(0, 10)]
    public int MaxRetryAttempts { get; set; } = 3;

    [Range(1, 600)]
    public int InitialRetryDelaySeconds { get; set; } = 5;

    [Range(1, 3600)]
    public int MaxRetryDelaySeconds { get; set; } = 60;

    [Range(1, 1000)]
    public int MaxBotsPerCycle { get; set; } = 100;

    [MaxLength(128)]
    public string WorkerInstanceId { get; set; } = string.Empty;
}
