using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class WorkerHeartbeat
{
    public Guid Id { get; set; }

    public string WorkerKey { get; set; } = string.Empty;

    public string WorkerName { get; set; } = string.Empty;

    public MonitoringHealthState HealthState { get; set; } = MonitoringHealthState.Unknown;

    public MonitoringFreshnessTier FreshnessTier { get; set; } = MonitoringFreshnessTier.Stale;

    public CircuitBreakerStateCode CircuitBreakerState { get; set; } = CircuitBreakerStateCode.Degraded;

    public DateTime LastHeartbeatAtUtc { get; set; }

    public DateTime LastUpdatedAtUtc { get; set; }

    public int ConsecutiveFailureCount { get; set; }

    public string? LastErrorCode { get; set; }

    public string? LastErrorMessage { get; set; }

    public int? SnapshotAgeSeconds { get; set; }

    public string? Detail { get; set; }
}
