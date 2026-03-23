using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Enums;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CoinBot.Infrastructure.Observability;

public sealed class DataLatencyHealthCheck(
    IDataLatencyCircuitBreaker dataLatencyCircuitBreaker,
    ICorrelationContextAccessor correlationContextAccessor) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await dataLatencyCircuitBreaker.GetSnapshotAsync(
            correlationContextAccessor.Current?.CorrelationId,
            cancellationToken);
        var data = new Dictionary<string, object>
        {
            ["stateCode"] = snapshot.StateCode.ToString(),
            ["reasonCode"] = snapshot.ReasonCode.ToString(),
            ["signalFlowBlocked"] = snapshot.SignalFlowBlocked,
            ["executionFlowBlocked"] = snapshot.ExecutionFlowBlocked,
            ["latestDataTimestampAtUtc"] = snapshot.LatestDataTimestampAtUtc?.ToString("O") ?? "missing",
            ["latestHeartbeatReceivedAtUtc"] = snapshot.LatestHeartbeatReceivedAtUtc?.ToString("O") ?? "missing",
            ["latestDataAgeMilliseconds"] = snapshot.LatestDataAgeMilliseconds?.ToString() ?? "missing",
            ["latestClockDriftMilliseconds"] = snapshot.LatestClockDriftMilliseconds?.ToString() ?? "missing",
            ["lastStateChangedAtUtc"] = snapshot.LastStateChangedAtUtc?.ToString("O") ?? "missing",
            ["isPersisted"] = snapshot.IsPersisted
        };

        return snapshot.StateCode switch
        {
            DegradedModeStateCode.Normal => HealthCheckResult.Healthy(
                "Data latency guard is healthy.",
                data),
            DegradedModeStateCode.Degraded => HealthCheckResult.Degraded(
                "Data latency guard entered degraded mode because market data is stale.",
                data: data),
            _ => HealthCheckResult.Unhealthy(
                "Data latency guard stopped signal and execution because market data is unavailable or unsafe.",
                data: data)
        };
    }
}
