using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CoinBot.Infrastructure.Observability;

public sealed class WorkerHeartbeatHealthCheck : IHealthCheck
{
    private const int WarningAgeSeconds = 60;
    private const int CriticalAgeSeconds = 300;

    private readonly ApplicationDbContext dbContext;
    private readonly TimeProvider timeProvider;

    public WorkerHeartbeatHealthCheck(ApplicationDbContext dbContext, TimeProvider timeProvider)
    {
        this.dbContext = dbContext;
        this.timeProvider = timeProvider;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var heartbeats = await dbContext.WorkerHeartbeats
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (heartbeats.Count == 0)
        {
            return HealthCheckResult.Unhealthy(
                "Worker heartbeat readiness evidence is missing.",
                data: new Dictionary<string, object>
                {
                    ["heartbeatCount"] = 0,
                    ["criticalCount"] = 0,
                    ["degradedCount"] = 0,
                    ["staleCount"] = 0,
                    ["oldestHeartbeatAgeSeconds"] = 0
                });
        }

        var snapshots = heartbeats
            .Select(heartbeat => CreateSnapshot(heartbeat, now))
            .ToList();
        var criticalSnapshots = snapshots
            .Where(IsCritical)
            .OrderByDescending(snapshot => snapshot.AgeSeconds)
            .ThenBy(snapshot => snapshot.WorkerKey, StringComparer.Ordinal)
            .ToList();
        var degradedSnapshots = snapshots
            .Where(snapshot => !IsCritical(snapshot) && IsDegraded(snapshot))
            .OrderByDescending(snapshot => snapshot.AgeSeconds)
            .ThenBy(snapshot => snapshot.WorkerKey, StringComparer.Ordinal)
            .ToList();

        var data = new Dictionary<string, object>
        {
            ["heartbeatCount"] = snapshots.Count,
            ["criticalCount"] = criticalSnapshots.Count,
            ["degradedCount"] = degradedSnapshots.Count,
            ["staleCount"] = snapshots.Count(snapshot => snapshot.FreshnessTier == MonitoringFreshnessTier.Stale),
            ["oldestHeartbeatAgeSeconds"] = snapshots.Max(snapshot => snapshot.AgeSeconds),
            ["criticalWorkers"] = FormatSnapshots(criticalSnapshots),
            ["degradedWorkers"] = FormatSnapshots(degradedSnapshots)
        };

        if (criticalSnapshots.Count > 0)
        {
            return HealthCheckResult.Unhealthy("One or more worker heartbeats are stale or critical.", data: data);
        }

        if (degradedSnapshots.Count > 0)
        {
            return HealthCheckResult.Degraded("One or more worker heartbeats are degraded.", data: data);
        }

        return HealthCheckResult.Healthy("Worker heartbeats are fresh.", data);
    }

    private static WorkerHeartbeatSnapshot CreateSnapshot(WorkerHeartbeat heartbeat, DateTime now)
    {
        var lastHeartbeatAtUtc = heartbeat.LastHeartbeatAtUtc.Kind == DateTimeKind.Utc
            ? heartbeat.LastHeartbeatAtUtc
            : DateTime.SpecifyKind(heartbeat.LastHeartbeatAtUtc, DateTimeKind.Utc);
        var ageSeconds = Math.Max(0, (int)Math.Ceiling((now - lastHeartbeatAtUtc).TotalSeconds));

        return new WorkerHeartbeatSnapshot(
            NormalizeToken(heartbeat.WorkerKey, "worker"),
            heartbeat.HealthState,
            heartbeat.FreshnessTier,
            ageSeconds,
            NormalizeToken(heartbeat.LastErrorCode, "none"));
    }

    private static bool IsCritical(WorkerHeartbeatSnapshot snapshot)
    {
        return snapshot.HealthState == MonitoringHealthState.Critical ||
            snapshot.FreshnessTier == MonitoringFreshnessTier.Stale ||
            snapshot.AgeSeconds > CriticalAgeSeconds;
    }

    private static bool IsDegraded(WorkerHeartbeatSnapshot snapshot)
    {
        return snapshot.HealthState is MonitoringHealthState.Warning or MonitoringHealthState.Degraded or MonitoringHealthState.Unknown ||
            snapshot.FreshnessTier == MonitoringFreshnessTier.Cold ||
            snapshot.AgeSeconds > WarningAgeSeconds;
    }

    private static string FormatSnapshots(IReadOnlyCollection<WorkerHeartbeatSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
        {
            return "none";
        }

        return string.Join(
            ", ",
            snapshots
                .Take(8)
                .Select(snapshot =>
                    $"{snapshot.WorkerKey}:{snapshot.HealthState}/{snapshot.FreshnessTier}:{snapshot.AgeSeconds}s:{snapshot.LastErrorCode}"));
    }

    private static string NormalizeToken(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Trim();
        foreach (var character in normalized)
        {
            if (!char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.')
            {
                return fallback;
            }
        }

        return normalized.Length <= 64 ? normalized : normalized[..64];
    }

    private sealed record WorkerHeartbeatSnapshot(
        string WorkerKey,
        MonitoringHealthState HealthState,
        MonitoringFreshnessTier FreshnessTier,
        int AgeSeconds,
        string LastErrorCode);
}
