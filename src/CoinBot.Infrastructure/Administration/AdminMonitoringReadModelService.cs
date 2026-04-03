using System.Globalization;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

using HealthSnapshotEntity = CoinBot.Domain.Entities.HealthSnapshot;
using MarketScannerCandidateEntity = CoinBot.Domain.Entities.MarketScannerCandidate;
using MarketScannerHandoffAttemptEntity = CoinBot.Domain.Entities.MarketScannerHandoffAttempt;
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
                    var scannerSnapshot = await LoadMarketScannerSnapshotAsync(cancellationToken);

                    return new MonitoringDashboardSnapshot(
                        healthSnapshots.Select(MapHealthSnapshot).ToArray(),
                        workerHeartbeats.Select(MapWorkerHeartbeat).ToArray(),
                        timeProvider.GetUtcNow().UtcDateTime)
                    {
                        MarketScanner = scannerSnapshot
                    };
                },
                SnapshotCacheOptions)!;
    }

    private async Task<MarketScannerDashboardSnapshot> LoadMarketScannerSnapshotAsync(CancellationToken cancellationToken)
    {
        var latestCycle = await dbContext.MarketScannerCycles
            .AsNoTracking()
            .OrderByDescending(entity => entity.CompletedAtUtc)
            .ThenByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestCycle is null)
        {
            return MarketScannerDashboardSnapshot.Empty();
        }

        var topCandidateEntities = await dbContext.MarketScannerCandidates
            .AsNoTracking()
            .Where(entity => entity.ScanCycleId == latestCycle.Id && entity.IsTopCandidate)
            .OrderBy(entity => entity.Rank ?? int.MaxValue)
            .ThenByDescending(entity => entity.Score)
            .ThenBy(entity => entity.Symbol)
            .ToListAsync(cancellationToken);

        var rejectedSampleEntities = await dbContext.MarketScannerCandidates
            .AsNoTracking()
            .Where(entity => entity.ScanCycleId == latestCycle.Id && !entity.IsEligible)
            .OrderBy(entity => entity.RejectionReason)
            .ThenBy(entity => entity.Symbol)
            .Take(5)
            .ToListAsync(cancellationToken);

        var latestHandoffEntity = await dbContext.MarketScannerHandoffAttempts
            .AsNoTracking()
            .OrderByDescending(entity => entity.CompletedAtUtc)
            .ThenByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);
        var lastSuccessfulHandoffEntity = await dbContext.MarketScannerHandoffAttempts
            .AsNoTracking()
            .Where(entity => entity.ExecutionRequestStatus == "Prepared")
            .OrderByDescending(entity => entity.CompletedAtUtc)
            .ThenByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);
        var lastBlockedHandoffEntity = await dbContext.MarketScannerHandoffAttempts
            .AsNoTracking()
            .Where(entity => entity.ExecutionRequestStatus != "Prepared")
            .OrderByDescending(entity => entity.CompletedAtUtc)
            .ThenByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);

        return new MarketScannerDashboardSnapshot(
            latestCycle.Id,
            NormalizeUtc(latestCycle.CompletedAtUtc),
            latestCycle.ScannedSymbolCount,
            latestCycle.EligibleCandidateCount,
            latestCycle.UniverseSource,
            latestCycle.BestCandidateSymbol,
            latestCycle.BestCandidateScore,
            topCandidateEntities.Select(MapMarketScannerCandidate).ToArray(),
            rejectedSampleEntities.Select(MapMarketScannerCandidate).ToArray(),
            MapMarketScannerHandoffAttempt(latestHandoffEntity),
            MapMarketScannerHandoffAttempt(lastSuccessfulHandoffEntity),
            MapMarketScannerHandoffAttempt(lastBlockedHandoffEntity));
    }
    private static MarketScannerHandoffSnapshot MapMarketScannerHandoffAttempt(MarketScannerHandoffAttemptEntity? entity)
    {
        if (entity is null)
        {
            return MarketScannerHandoffSnapshot.Empty();
        }

        return new MarketScannerHandoffSnapshot(
            entity.Id,
            entity.ScanCycleId,
            entity.SelectedCandidateId,
            entity.SelectedSymbol,
            entity.SelectedTimeframe,
            NormalizeUtc(entity.SelectedAtUtc),
            entity.CandidateRank,
            entity.CandidateScore,
            entity.SelectionReason,
            entity.OwnerUserId,
            entity.BotId,
            entity.StrategyKey,
            entity.TradingStrategyId,
            entity.TradingStrategyVersionId,
            entity.StrategySignalId,
            entity.StrategySignalVetoId,
            entity.StrategyDecisionOutcome,
            entity.StrategyVetoReasonCode,
            entity.StrategyScore,
            entity.ExecutionRequestStatus,
            entity.ExecutionSide,
            entity.ExecutionOrderType,
            entity.ExecutionEnvironment,
            entity.ExecutionQuantity,
            entity.ExecutionPrice,
            entity.BlockerCode,
            entity.BlockerDetail,
            entity.GuardSummary,
            entity.CorrelationId,
            NormalizeUtc(entity.CompletedAtUtc));
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

    private static MarketScannerCandidateSnapshot MapMarketScannerCandidate(MarketScannerCandidateEntity entity)
    {
        return new MarketScannerCandidateSnapshot(
            entity.Symbol,
            entity.UniverseSource,
            NormalizeUtc(entity.ObservedAtUtc),
            entity.LastCandleAtUtc.HasValue ? NormalizeUtc(entity.LastCandleAtUtc.Value) : null,
            entity.LastPrice,
            entity.QuoteVolume24h,
            entity.IsEligible,
            entity.RejectionReason,
            entity.Score,
            entity.Rank,
            entity.IsTopCandidate);
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

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
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



