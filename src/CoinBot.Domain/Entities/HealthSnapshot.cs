using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class HealthSnapshot
{
    public Guid Id { get; set; }

    public string SnapshotKey { get; set; } = string.Empty;

    public string SentinelName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public MonitoringHealthState HealthState { get; set; } = MonitoringHealthState.Unknown;

    public MonitoringFreshnessTier FreshnessTier { get; set; } = MonitoringFreshnessTier.Stale;

    public CircuitBreakerStateCode CircuitBreakerState { get; set; } = CircuitBreakerStateCode.Degraded;

    public DateTime LastUpdatedAtUtc { get; set; }

    public DateTime? ObservedAtUtc { get; set; }

    public int? BinancePingMs { get; set; }

    public int? WebSocketStaleDurationSeconds { get; set; }

    public int? LastMessageAgeSeconds { get; set; }

    public int? ReconnectCount { get; set; }

    public int? StreamGapCount { get; set; }

    public int? RateLimitUsage { get; set; }

    public int? DbLatencyMs { get; set; }

    public int? RedisLatencyMs { get; set; }

    public int? SignalRActiveConnectionCount { get; set; }

    public DateTime? WorkerLastHeartbeatAtUtc { get; set; }

    public int? ConsecutiveFailureCount { get; set; }

    public int? SnapshotAgeSeconds { get; set; }

    public string? Detail { get; set; }
}
