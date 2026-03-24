using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Monitoring;

public sealed record WorkerHeartbeat(
    string WorkerKey,
    string WorkerName,
    MonitoringHealthState HealthState,
    MonitoringFreshnessTier FreshnessTier,
    CircuitBreakerStateCode CircuitBreakerState,
    DateTime LastHeartbeatAtUtc,
    DateTime LastUpdatedAtUtc,
    int ConsecutiveFailureCount,
    string? LastErrorCode = null,
    string? LastErrorMessage = null,
    int? SnapshotAgeSeconds = null,
    string? Detail = null);
