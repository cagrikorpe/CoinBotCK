using System.ComponentModel.DataAnnotations;

namespace CoinBot.Infrastructure.Administration;

public sealed class LogCenterRetentionOptions
{
    public bool Enabled { get; set; } = true;

    [Range(30, 3650)]
    public int DecisionTraceRetentionDays { get; set; } = 365;

    [Range(30, 3650)]
    public int ExecutionTraceRetentionDays { get; set; } = 365;

    [Range(30, 3650)]
    public int AdminAuditLogRetentionDays { get; set; } = 365;

    [Range(30, 3650)]
    public int IncidentRetentionDays { get; set; } = 730;

    [Range(30, 3650)]
    public int ApprovalRetentionDays { get; set; } = 730;

    [Range(1, 1000)]
    public int BatchSize { get; set; } = 250;

    [Range(5, 1440)]
    public int WorkerIntervalMinutes { get; set; } = 60;
}
