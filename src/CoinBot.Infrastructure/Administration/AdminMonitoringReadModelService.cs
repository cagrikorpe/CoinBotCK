using System.Globalization;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

using HealthSnapshotEntity = CoinBot.Domain.Entities.HealthSnapshot;
using WorkerHeartbeatEntity = CoinBot.Domain.Entities.WorkerHeartbeat;

namespace CoinBot.Infrastructure.Administration;

public sealed class AdminMonitoringReadModelService(
    ApplicationDbContext dbContext,
    IMemoryCache memoryCache,
    TimeProvider timeProvider) : IAdminMonitoringReadModelService
{
    private static readonly object CacheKey = new();
    private static readonly MemoryCacheEntryOptions SnapshotCacheOptions = new MemoryCacheEntryOptions()
        .SetSize(1)
        .SetAbsoluteExpiration(TimeSpan.FromSeconds(3));

    public Task<MonitoringDashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return memoryCache.GetOrCreateAsync(
                CacheKey,
                async entry =>
                {
                    entry.SetOptions(SnapshotCacheOptions);

                    var healthSnapshots = await dbContext.HealthSnapshots
                        .AsNoTracking()
                        .OrderBy(entity => entity.DisplayName)
                        .ToListAsync(cancellationToken);
                    var workerHeartbeats = await dbContext.WorkerHeartbeats
                        .AsNoTracking()
                        .OrderBy(entity => entity.WorkerName)
                        .ToListAsync(cancellationToken);

                    return new MonitoringDashboardSnapshot(
                        healthSnapshots.Select(MapHealthSnapshot).ToArray(),
                        workerHeartbeats.Select(MapWorkerHeartbeat).ToArray(),
                        timeProvider.GetUtcNow().UtcDateTime);
                },
                SnapshotCacheOptions)!;
    }

    private static HealthSnapshot MapHealthSnapshot(HealthSnapshotEntity entity)
    {
        return new HealthSnapshot(
            entity.SnapshotKey,
            entity.SentinelName,
            entity.DisplayName,
            entity.HealthState,
            entity.FreshnessTier,
            entity.CircuitBreakerState,
            entity.LastUpdatedAtUtc,
            new MonitoringMetricsSnapshot(
                entity.BinancePingMs,
                entity.WebSocketStaleDurationSeconds,
                entity.LastMessageAgeSeconds,
                entity.ReconnectCount,
                entity.StreamGapCount,
                entity.RateLimitUsage,
                entity.DbLatencyMs,
                entity.RedisLatencyMs,
                TryReadDetailMetric(entity.Detail, "ClockDriftMs"),
                entity.SignalRActiveConnectionCount,
                entity.WorkerLastHeartbeatAtUtc,
                entity.ConsecutiveFailureCount,
                entity.SnapshotAgeSeconds),
            entity.Detail,
            entity.ObservedAtUtc);
    }

    private static int? TryReadDetailMetric(string? detail, string metricName)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var segments = detail.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = segment[..separatorIndex].Trim();
            if (!string.Equals(key, metricName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = segment[(separatorIndex + 1)..].Trim();

            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue)
                ? parsedValue
                : null;
        }

        return null;
    }

    private static WorkerHeartbeat MapWorkerHeartbeat(WorkerHeartbeatEntity entity)
    {
        return new WorkerHeartbeat(
            entity.WorkerKey,
            entity.WorkerName,
            entity.HealthState,
            entity.FreshnessTier,
            entity.CircuitBreakerState,
            entity.LastHeartbeatAtUtc,
            entity.LastUpdatedAtUtc,
            entity.ConsecutiveFailureCount,
            entity.LastErrorCode,
            entity.LastErrorMessage,
            entity.SnapshotAgeSeconds,
            entity.Detail);
    }
}
