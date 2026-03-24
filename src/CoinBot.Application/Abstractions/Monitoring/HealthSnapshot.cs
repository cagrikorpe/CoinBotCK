using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Monitoring;

public sealed record HealthSnapshot(
    string SnapshotKey,
    string SentinelName,
    string DisplayName,
    MonitoringHealthState HealthState,
    MonitoringFreshnessTier FreshnessTier,
    CircuitBreakerStateCode CircuitBreakerState,
    DateTime LastUpdatedAtUtc,
    MonitoringMetricsSnapshot Metrics,
    string? Detail = null,
    DateTime? ObservedAtUtc = null);
