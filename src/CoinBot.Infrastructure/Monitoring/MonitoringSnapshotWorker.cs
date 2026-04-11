using System.Diagnostics;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Alerts;
using CoinBot.Infrastructure.Dashboard;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Monitoring;

public sealed class MonitoringSnapshotWorker(
    IServiceScopeFactory serviceScopeFactory,
    IMonitoringTelemetryCollector telemetryCollector,
    IRedisLatencyProbe redisLatencyProbe,
    IBinanceExchangeInfoClient exchangeInfoClient,
    IOptions<DataLatencyGuardOptions> dataLatencyGuardOptions,
    TimeProvider timeProvider,
    ILogger<MonitoringSnapshotWorker> logger,
    IAlertDispatchCoordinator? alertDispatchCoordinator = null,
    IHostEnvironment? hostEnvironment = null,
    UserOperationsStreamHub? userOperationsStreamHub = null) : BackgroundService
{
    private static readonly TimeSpan WarmInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ColdInterval = TimeSpan.FromSeconds(60);
    private readonly int clockDriftThresholdMilliseconds = checked(dataLatencyGuardOptions.Value.ClockDriftThresholdSeconds * 1000);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextWarmAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var nextColdAtUtc = nextWarmAtUtc;

        while (!stoppingToken.IsCancellationRequested)
        {
            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

            try
            {
                if (nowUtc >= nextWarmAtUtc)
                {
                    await RunWarmCycleAsync(stoppingToken);
                    nextWarmAtUtc = nowUtc.Add(WarmInterval);
                }

                if (nowUtc >= nextColdAtUtc)
                {
                    await RunColdCycleAsync(stoppingToken);
                    nextColdAtUtc = nowUtc.Add(ColdInterval);
                }

                var nextDueUtc = nextWarmAtUtc < nextColdAtUtc ? nextWarmAtUtc : nextColdAtUtc;
                var delay = nextDueUtc - timeProvider.GetUtcNow().UtcDateTime;

                if (delay < TimeSpan.FromSeconds(1))
                {
                    delay = TimeSpan.FromSeconds(1);
                }

                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Monitoring snapshot worker cycle failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    internal Task RunWarmCycleAsync(CancellationToken cancellationToken = default)
    {
        return RunCycleAsync(includeWarmSnapshots: true, includeColdSnapshots: false, cancellationToken);
    }

    internal Task RunColdCycleAsync(CancellationToken cancellationToken = default)
    {
        return RunCycleAsync(includeWarmSnapshots: false, includeColdSnapshots: true, cancellationToken);
    }

    private async Task RunCycleAsync(
        bool includeWarmSnapshots,
        bool includeColdSnapshots,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        using var scope = serviceScopeFactory.CreateScope();
        using var systemScope = scope.ServiceProvider
            .GetRequiredService<IDataScopeContextAccessor>()
            .BeginScope(hasIsolationBypass: true);
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var circuitBreaker = scope.ServiceProvider.GetRequiredService<IDataLatencyCircuitBreaker>();

        var telemetryBeforeDbProbe = telemetryCollector.CaptureSnapshot(startedAtUtc);
        var dbLatency = await MeasureDatabaseLatencyAsync(dbContext, cancellationToken);
        var redisProbeResult = await redisLatencyProbe.ProbeAsync(cancellationToken);
        telemetryCollector.RecordDatabaseLatency(dbLatency, startedAtUtc);
        telemetryCollector.RecordRedisLatency(redisProbeResult.Latency, startedAtUtc);
        var telemetry = telemetryCollector.CaptureSnapshot(startedAtUtc);
        var dataLatencySnapshot = await circuitBreaker.GetSnapshotAsync(cancellationToken: cancellationToken);

        if (includeWarmSnapshots)
        {
            var exchangeServerTimeUtc = await exchangeInfoClient.GetServerTimeUtcAsync(cancellationToken);
            var warmSnapshotAtUtc = timeProvider.GetUtcNow().UtcDateTime;

            await UpsertMarketHealthSnapshotsAsync(
                dbContext,
                telemetry,
                dataLatencySnapshot,
                exchangeServerTimeUtc,
                warmSnapshotAtUtc,
                cancellationToken);
        }

        if (includeColdSnapshots)
        {
            await UpsertDependencyHealthSnapshotAsync(
                dbContext,
                telemetry,
                redisProbeResult,
                dataLatencySnapshot,
                startedAtUtc,
                cancellationToken);

            await UpsertWorkerHeartbeatsAsync(
                dbContext,
                dataLatencySnapshot,
                startedAtUtc,
                cancellationToken);
        }

        logger.LogDebug(
            "Monitoring snapshot worker cycle completed. Warm={Warm}, Cold={Cold}, BinancePingMs={BinancePingMs}, DbLatencyMs={DbLatencyMs}, RedisLatencyMs={RedisLatencyMs}, RedisProbe={RedisProbe}.",
            includeWarmSnapshots,
            includeColdSnapshots,
            telemetryBeforeDbProbe.BinancePingMs?.ToString() ?? "n/a",
            dbLatency.TotalMilliseconds.ToString("0"),
            redisProbeResult.Latency?.TotalMilliseconds.ToString("0") ?? "n/a",
            redisProbeResult.Status);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertMarketHealthSnapshotsAsync(
        ApplicationDbContext dbContext,
        MonitoringTelemetrySnapshot telemetry,
        DegradedModeSnapshot dataLatencySnapshot,
        DateTime? exchangeServerTimeUtc,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        int? clockDriftMilliseconds = exchangeServerTimeUtc is null
            ? null
            : ToMilliseconds(Math.Abs((utcNow - exchangeServerTimeUtc.Value).TotalMilliseconds));

        await UpsertHealthSnapshotAsync(
            dbContext,
            key: "market-watchdog",
            sentinelName: "MarketWatchdog",
            displayName: "Market Watchdog",
            healthState: ResolveMarketWatchdogState(dataLatencySnapshot),
            circuitBreakerState: MapCircuitBreakerState(dataLatencySnapshot),
            telemetry: telemetry,
            observedAtUtc: dataLatencySnapshot.LatestDataTimestampAtUtc ?? dataLatencySnapshot.LatestHeartbeatReceivedAtUtc ?? utcNow,
            lastUpdatedAtUtc: utcNow,
            workerLastHeartbeatAtUtc: dataLatencySnapshot.LatestHeartbeatReceivedAtUtc ?? utcNow,
            consecutiveFailureCount: dataLatencySnapshot.ReasonCode == DegradedModeReasonCode.None ? 0 : 1,
            detail: BuildMarketWatchdogDetail(dataLatencySnapshot),
            cancellationToken);

        await UpsertHealthSnapshotAsync(
            dbContext,
            key: "clock-drift-monitor",
            sentinelName: "ClockDriftMonitor",
            displayName: "Clock Drift Monitor",
            healthState: ResolveClockDriftState(clockDriftMilliseconds),
            circuitBreakerState: MapCircuitBreakerState(dataLatencySnapshot),
            telemetry: telemetry,
            observedAtUtc: utcNow,
            lastUpdatedAtUtc: utcNow,
            workerLastHeartbeatAtUtc: utcNow,
            consecutiveFailureCount: clockDriftMilliseconds is null ? 1 : 0,
            detail: BuildClockDriftDetail(clockDriftMilliseconds, utcNow, exchangeServerTimeUtc),
            cancellationToken);

        await UpsertHealthSnapshotAsync(
            dbContext,
            key: "market-data-drift-monitor",
            sentinelName: "MarketDataDriftMonitor",
            displayName: "Market Data Drift Monitor",
            healthState: ResolveMarketDataDriftState(dataLatencySnapshot),
            circuitBreakerState: MapCircuitBreakerState(dataLatencySnapshot),
            telemetry: telemetry,
            observedAtUtc: dataLatencySnapshot.LatestDataTimestampAtUtc ?? utcNow,
            lastUpdatedAtUtc: utcNow,
            workerLastHeartbeatAtUtc: dataLatencySnapshot.LatestDataTimestampAtUtc ?? dataLatencySnapshot.LatestHeartbeatReceivedAtUtc ?? utcNow,
            consecutiveFailureCount: dataLatencySnapshot.ReasonCode == DegradedModeReasonCode.None ? 0 : 1,
            detail: BuildMarketDataDriftDetail(dataLatencySnapshot),
            cancellationToken);

        await UpsertHealthSnapshotAsync(
            dbContext,
            key: "stale-data-guard",
            sentinelName: "StaleDataGuard",
            displayName: "Stale Data Guard",
            healthState: ResolveStaleDataGuardState(dataLatencySnapshot),
            circuitBreakerState: MapCircuitBreakerState(dataLatencySnapshot),
            telemetry: telemetry,
            observedAtUtc: dataLatencySnapshot.LatestDataTimestampAtUtc ?? dataLatencySnapshot.LatestHeartbeatReceivedAtUtc ?? utcNow,
            lastUpdatedAtUtc: utcNow,
            workerLastHeartbeatAtUtc: dataLatencySnapshot.LatestHeartbeatReceivedAtUtc ?? dataLatencySnapshot.LatestDataTimestampAtUtc ?? utcNow,
            consecutiveFailureCount: dataLatencySnapshot.ReasonCode == DegradedModeReasonCode.None ? 0 : 1,
            detail: BuildStaleDataGuardDetail(dataLatencySnapshot),
            cancellationToken);
    }

    private async Task UpsertDependencyHealthSnapshotAsync(
        ApplicationDbContext dbContext,
        MonitoringTelemetrySnapshot telemetry,
        RedisLatencyProbeResult redisProbeResult,
        DegradedModeSnapshot dataLatencySnapshot,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        await UpsertHealthSnapshotAsync(
            dbContext,
            key: "dependency-health-monitor",
            sentinelName: "DependencyHealthMonitor",
            displayName: "Dependency Health Monitor",
            healthState: ResolveDependencyHealthState(telemetry, redisProbeResult),
            circuitBreakerState: MapCircuitBreakerState(dataLatencySnapshot),
            telemetry: telemetry,
            observedAtUtc: telemetry.RedisLatencyObservedAtUtc ?? telemetry.DatabaseLatencyObservedAtUtc ?? utcNow,
            lastUpdatedAtUtc: utcNow,
            workerLastHeartbeatAtUtc: telemetry.RedisLatencyObservedAtUtc ?? telemetry.DatabaseLatencyObservedAtUtc ?? utcNow,
            consecutiveFailureCount: (redisProbeResult.Status == RedisProbeStatus.Failed || telemetry.DatabaseLatencyMs is null) ? 1 : 0,
            detail: BuildDependencyHealthDetail(telemetry, redisProbeResult),
            cancellationToken);
    }

    private async Task UpsertWorkerHeartbeatsAsync(
        ApplicationDbContext dbContext,
        DegradedModeSnapshot dataLatencySnapshot,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var latestJobHeartbeat = await dbContext.BackgroundJobStates
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => entity.LastHeartbeatAtUtc.HasValue)
            .OrderByDescending(entity => entity.LastHeartbeatAtUtc)
            .Select(entity => new
            {
                entity.JobType,
                entity.JobKey,
                entity.LastHeartbeatAtUtc,
                entity.ConsecutiveFailureCount,
                entity.LastErrorCode
            })
            .FirstOrDefaultAsync(cancellationToken);

        var isJobOrchestrationIdle = string.Equals(latestJobHeartbeat?.LastErrorCode, "TradeMasterDisarmed", StringComparison.Ordinal);
        var jobOrchestrationHeartbeatAtUtc = isJobOrchestrationIdle
            ? utcNow
            : latestJobHeartbeat?.LastHeartbeatAtUtc ?? utcNow;
        var jobOrchestrationHeartbeat = await UpsertWorkerHeartbeatAsync(
            dbContext,
            key: "job-orchestration",
            workerName: "Job Orchestration",
            healthState: isJobOrchestrationIdle ? MonitoringHealthState.Healthy : ResolveJobOrchestrationState(latestJobHeartbeat, utcNow),
            circuitBreakerState: MapCircuitBreakerState(dataLatencySnapshot),
            lastHeartbeatAtUtc: jobOrchestrationHeartbeatAtUtc,
            consecutiveFailureCount: isJobOrchestrationIdle ? 0 : latestJobHeartbeat?.ConsecutiveFailureCount ?? 0,
            lastErrorCode: latestJobHeartbeat?.LastErrorCode,
            lastErrorMessage: latestJobHeartbeat is null
                ? "No job heartbeat available."
                : isJobOrchestrationIdle
                    ? "Job orchestration idle while TradeMaster is disarmed."
                    : $"Latest job heartbeat from {latestJobHeartbeat.JobType}/{latestJobHeartbeat.JobKey}.",
            utcNow: utcNow,
            cancellationToken: cancellationToken);
        await TrySendWorkerHeartbeatAlertAsync(jobOrchestrationHeartbeat, "WorkerNotHealthy", cancellationToken);

        var exchangeHeartbeatRows = await (
            from entity in dbContext.ExchangeAccountSyncStates.AsNoTracking().IgnoreQueryFilters()
            join account in dbContext.ExchangeAccounts.AsNoTracking().IgnoreQueryFilters()
                on entity.ExchangeAccountId equals account.Id
            where
                !entity.IsDeleted &&
                !account.IsDeleted &&
                account.CredentialStatus == ExchangeCredentialStatus.Active &&
                !account.IsReadOnly &&
                (entity.LastPrivateStreamEventAtUtc.HasValue ||
                 entity.LastListenKeyRenewedAtUtc.HasValue ||
                 entity.LastListenKeyStartedAtUtc.HasValue ||
                 entity.LastBalanceSyncedAtUtc.HasValue ||
                 entity.LastPositionSyncedAtUtc.HasValue ||
                 entity.LastStateReconciledAtUtc.HasValue)
            select new
            {
                entity.ExchangeAccountId,
                entity.Plane,
                entity.PrivateStreamConnectionState,
                entity.LastPrivateStreamEventAtUtc,
                entity.LastListenKeyRenewedAtUtc,
                entity.LastListenKeyStartedAtUtc,
                entity.LastBalanceSyncedAtUtc,
                entity.LastPositionSyncedAtUtc,
                entity.LastStateReconciledAtUtc,
                entity.ConsecutiveStreamFailureCount,
                entity.LastErrorCode
            })
            .ToListAsync(cancellationToken);
        var exchangeHeartbeats = exchangeHeartbeatRows
            .Select(entity => new ExchangePrivateStreamHeartbeatProjection(
                entity.ExchangeAccountId,
                entity.Plane,
                entity.PrivateStreamConnectionState,
                ResolveExchangePrivateStreamHeartbeatAt(
                    entity.LastPrivateStreamEventAtUtc,
                    entity.LastListenKeyRenewedAtUtc,
                    entity.LastListenKeyStartedAtUtc,
                    entity.LastBalanceSyncedAtUtc,
                    entity.LastPositionSyncedAtUtc,
                    entity.LastStateReconciledAtUtc,
                    utcNow),
                entity.ConsecutiveStreamFailureCount,
                entity.LastErrorCode))
            .ToArray();
        var latestExchangeHeartbeats = exchangeHeartbeats
            .GroupBy(entity => new { entity.ExchangeAccountId, entity.Plane })
            .Select(group => group.OrderByDescending(entity => entity.HeartbeatAtUtc).First())
            .ToArray();
        var latestExchangeHeartbeat = latestExchangeHeartbeats
            .OrderByDescending(entity => GetExchangeStreamSeverity(entity.PrivateStreamConnectionState, utcNow, entity.HeartbeatAtUtc))
            .ThenBy(entity => entity.HeartbeatAtUtc)
            .FirstOrDefault();
        var exchangeStreamHeartbeatAtUtc = latestExchangeHeartbeats.Length == 0
            ? utcNow
            : latestExchangeHeartbeats.Min(entity => entity.HeartbeatAtUtc);

        var exchangePrivateStreamHeartbeat = await UpsertWorkerHeartbeatAsync(
            dbContext,
            key: "exchange-private-stream",
            workerName: "Exchange Private Stream",
            healthState: ResolveExchangeStreamState(latestExchangeHeartbeats, utcNow),
            circuitBreakerState: MapCircuitBreakerState(dataLatencySnapshot),
            lastHeartbeatAtUtc: exchangeStreamHeartbeatAtUtc,
            consecutiveFailureCount: latestExchangeHeartbeat?.ConsecutiveStreamFailureCount ?? 0,
            lastErrorCode: latestExchangeHeartbeat?.LastErrorCode,
            lastErrorMessage: BuildExchangeStreamHeartbeatDetail(latestExchangeHeartbeats),
            utcNow: utcNow,
            cancellationToken: cancellationToken);
        await TrySendWorkerHeartbeatAlertAsync(exchangePrivateStreamHeartbeat, "PrivateStreamDisconnected", cancellationToken);

        await UpsertWorkerHeartbeatAsync(
            dbContext,
            key: "market-data-watchdog",
            workerName: "Market Data Watchdog",
            healthState: ResolveMarketWatchdogState(dataLatencySnapshot),
            circuitBreakerState: MapCircuitBreakerState(dataLatencySnapshot),
            lastHeartbeatAtUtc: dataLatencySnapshot.LatestHeartbeatReceivedAtUtc ?? dataLatencySnapshot.LatestDataTimestampAtUtc ?? utcNow,
            consecutiveFailureCount: dataLatencySnapshot.ReasonCode == DegradedModeReasonCode.None ? 0 : 1,
            lastErrorCode: dataLatencySnapshot.ReasonCode.ToString(),
            lastErrorMessage: BuildMarketWatchdogDetail(dataLatencySnapshot),
            utcNow: utcNow,
            cancellationToken: cancellationToken);

        await UpsertWorkerHeartbeatAsync(
            dbContext,
            key: "monitoring-worker",
            workerName: "Monitoring Worker",
            healthState: MonitoringHealthState.Healthy,
            circuitBreakerState: CircuitBreakerStateCode.Closed,
            lastHeartbeatAtUtc: utcNow,
            consecutiveFailureCount: 0,
            lastErrorCode: null,
            lastErrorMessage: "Monitoring snapshot cycle completed.",
            utcNow: utcNow,
            cancellationToken: cancellationToken);
    }

    private static async Task<TimeSpan> MeasureDatabaseLatencyAsync(ApplicationDbContext dbContext, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await dbContext.Database.CanConnectAsync(cancellationToken);
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private static async Task UpsertHealthSnapshotAsync(
        ApplicationDbContext dbContext,
        string key,
        string sentinelName,
        string displayName,
        MonitoringHealthState healthState,
        CircuitBreakerStateCode circuitBreakerState,
        MonitoringTelemetrySnapshot telemetry,
        DateTime observedAtUtc,
        DateTime lastUpdatedAtUtc,
        DateTime? workerLastHeartbeatAtUtc,
        int consecutiveFailureCount,
        string? detail,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.HealthSnapshots
            .SingleOrDefaultAsync(item => item.SnapshotKey == key, cancellationToken);

        if (entity is null)
        {
            entity = new CoinBot.Domain.Entities.HealthSnapshot
            {
                Id = Guid.NewGuid(),
                SnapshotKey = key
            };
            dbContext.HealthSnapshots.Add(entity);
        }

        var ageSeconds = Math.Max(0, (int)Math.Round((lastUpdatedAtUtc - observedAtUtc).TotalSeconds, MidpointRounding.AwayFromZero));

        entity.SentinelName = sentinelName;
        entity.DisplayName = displayName;
        entity.HealthState = healthState;
        entity.FreshnessTier = ResolveFreshnessTier(ageSeconds);
        entity.CircuitBreakerState = circuitBreakerState;
        entity.LastUpdatedAtUtc = lastUpdatedAtUtc;
        entity.ObservedAtUtc = observedAtUtc;
        entity.WorkerLastHeartbeatAtUtc = workerLastHeartbeatAtUtc;
        entity.ConsecutiveFailureCount = Math.Max(0, consecutiveFailureCount);
        entity.BinancePingMs = telemetry.BinancePingMs;
        entity.WebSocketStaleDurationSeconds = telemetry.WebSocketStaleDurationSeconds;
        entity.LastMessageAgeSeconds = telemetry.WebSocketLastMessageAgeSeconds;
        entity.ReconnectCount = telemetry.WebSocketReconnectCount;
        entity.StreamGapCount = telemetry.WebSocketStreamGapCount;
        entity.RateLimitUsage = telemetry.RateLimitUsage;
        entity.DbLatencyMs = telemetry.DatabaseLatencyMs;
        entity.RedisLatencyMs = telemetry.RedisLatencyMs;
        entity.SignalRActiveConnectionCount = telemetry.SignalRActiveConnectionCount;
        entity.SnapshotAgeSeconds = ageSeconds;
        entity.Detail = detail;
    }

    private async Task<WorkerHeartbeatUpdateResult> UpsertWorkerHeartbeatAsync(
        ApplicationDbContext dbContext,
        string key,
        string workerName,
        MonitoringHealthState healthState,
        CircuitBreakerStateCode circuitBreakerState,
        DateTime lastHeartbeatAtUtc,
        int consecutiveFailureCount,
        string? lastErrorCode,
        string? lastErrorMessage,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.WorkerHeartbeats
            .SingleOrDefaultAsync(item => item.WorkerKey == key, cancellationToken);
        var previousHealthState = entity?.HealthState;
        var previousErrorCode = entity?.LastErrorCode;

        if (entity is null)
        {
            entity = new CoinBot.Domain.Entities.WorkerHeartbeat
            {
                Id = Guid.NewGuid(),
                WorkerKey = key
            };
            dbContext.WorkerHeartbeats.Add(entity);
        }

        var ageSeconds = Math.Max(0, (int)Math.Round((utcNow - lastHeartbeatAtUtc).TotalSeconds, MidpointRounding.AwayFromZero));

        entity.WorkerName = workerName;
        entity.HealthState = healthState;
        entity.FreshnessTier = ResolveFreshnessTier(ageSeconds);
        entity.CircuitBreakerState = circuitBreakerState;
        entity.LastHeartbeatAtUtc = lastHeartbeatAtUtc;
        entity.LastUpdatedAtUtc = utcNow;
        entity.ConsecutiveFailureCount = Math.Max(0, consecutiveFailureCount);
        entity.LastErrorCode = lastErrorCode;
        entity.LastErrorMessage = lastErrorMessage;
        entity.SnapshotAgeSeconds = ageSeconds;
        entity.Detail = BuildWorkerHeartbeatDetail(workerName, healthState, ageSeconds);

        return new WorkerHeartbeatUpdateResult(
            key,
            workerName,
            previousHealthState,
            previousErrorCode,
            healthState,
            lastErrorCode,
            lastErrorMessage);
    }

    private static MonitoringHealthState ResolveMarketWatchdogState(DegradedModeSnapshot snapshot)
    {
        if (snapshot.ExecutionFlowBlocked)
        {
            return snapshot.StateCode == DegradedModeStateCode.Stopped
                ? MonitoringHealthState.Critical
                : MonitoringHealthState.Degraded;
        }

        return MonitoringHealthState.Healthy;
    }

    private MonitoringHealthState ResolveClockDriftState(int? clockDriftMilliseconds)
    {
        if (clockDriftMilliseconds is null)
        {
            return MonitoringHealthState.Unknown;
        }

        return clockDriftMilliseconds >= clockDriftThresholdMilliseconds
            ? MonitoringHealthState.Critical
            : clockDriftMilliseconds >= Math.Max(1, clockDriftThresholdMilliseconds / 2)
                ? MonitoringHealthState.Warning
                : MonitoringHealthState.Healthy;
    }

    private static MonitoringHealthState ResolveMarketDataDriftState(DegradedModeSnapshot snapshot)
    {
        if (snapshot.LatestDataAgeMilliseconds is null)
        {
            return MonitoringHealthState.Unknown;
        }

        return snapshot.LatestDataAgeMilliseconds >= 6_000
            ? MonitoringHealthState.Critical
            : snapshot.LatestDataAgeMilliseconds >= 3_000
                ? MonitoringHealthState.Warning
                : MonitoringHealthState.Healthy;
    }

    private static MonitoringHealthState ResolveStaleDataGuardState(DegradedModeSnapshot snapshot)
    {
        if (snapshot.SignalFlowBlocked || snapshot.ExecutionFlowBlocked)
        {
            return snapshot.StateCode == DegradedModeStateCode.Stopped
                ? MonitoringHealthState.Critical
                : MonitoringHealthState.Degraded;
        }

        return MonitoringHealthState.Healthy;
    }

    private static MonitoringHealthState ResolveDependencyHealthState(
        MonitoringTelemetrySnapshot telemetry,
        RedisLatencyProbeResult redisProbeResult)
    {
        if (redisProbeResult.Status == RedisProbeStatus.NotConfigured)
        {
            return MonitoringHealthState.Unknown;
        }

        if (redisProbeResult.Status == RedisProbeStatus.Failed)
        {
            return telemetry.DatabaseLatencyMs is >= 1_000
                ? MonitoringHealthState.Critical
                : MonitoringHealthState.Degraded;
        }

        if (telemetry.DatabaseLatencyMs is null && telemetry.RedisLatencyMs is null)
        {
            return MonitoringHealthState.Unknown;
        }

        var databaseLatencyMs = telemetry.DatabaseLatencyMs ?? 0;
        var redisLatencyMs = telemetry.RedisLatencyMs ?? 0;
        var worstLatencyMs = Math.Max(databaseLatencyMs, redisLatencyMs);

        if (worstLatencyMs >= 1_000)
        {
            return MonitoringHealthState.Critical;
        }

        if (worstLatencyMs >= 500)
        {
            return MonitoringHealthState.Warning;
        }

        return MonitoringHealthState.Healthy;
    }

    private static MonitoringHealthState ResolveJobOrchestrationState(object? heartbeat, DateTime utcNow)
    {
        if (heartbeat is null)
        {
            return MonitoringHealthState.Unknown;
        }

        var heartbeatAtUtc = (DateTime)heartbeat.GetType().GetProperty("LastHeartbeatAtUtc")!.GetValue(heartbeat)!;
        var ageSeconds = Math.Max(0, (int)Math.Round((utcNow - heartbeatAtUtc).TotalSeconds, MidpointRounding.AwayFromZero));

        return ageSeconds >= 300
            ? MonitoringHealthState.Critical
            : ageSeconds >= 60
                ? MonitoringHealthState.Warning
                : MonitoringHealthState.Healthy;
    }

    private static MonitoringHealthState ResolveExchangeStreamState(
        IReadOnlyCollection<ExchangePrivateStreamHeartbeatProjection> heartbeats,
        DateTime utcNow)
    {
        if (heartbeats.Count == 0)
        {
            return MonitoringHealthState.Unknown;
        }

        return heartbeats
            .Select(heartbeat => GetExchangeStreamSeverity(heartbeat.PrivateStreamConnectionState, utcNow, heartbeat.HeartbeatAtUtc))
            .Max();
    }

    private static CircuitBreakerStateCode MapCircuitBreakerState(DegradedModeSnapshot snapshot)
    {
        if (snapshot.IsNormal)
        {
            return CircuitBreakerStateCode.Closed;
        }

        return snapshot.ReasonCode switch
        {
            DegradedModeReasonCode.MarketDataLatencyBreached => CircuitBreakerStateCode.Retrying,
            DegradedModeReasonCode.CandleDataGapDetected or DegradedModeReasonCode.CandleDataDuplicateDetected or DegradedModeReasonCode.CandleDataOutOfOrderDetected => CircuitBreakerStateCode.HalfOpen,
            DegradedModeReasonCode.MarketDataUnavailable or DegradedModeReasonCode.ClockDriftExceeded or DegradedModeReasonCode.MarketDataLatencyCritical => CircuitBreakerStateCode.Cooldown,
            _ when snapshot.StateCode == DegradedModeStateCode.Degraded => CircuitBreakerStateCode.Degraded,
            _ => CircuitBreakerStateCode.Degraded
        };
    }

    private static MonitoringFreshnessTier ResolveFreshnessTier(int ageSeconds)
    {
        if (ageSeconds <= 5)
        {
            return MonitoringFreshnessTier.Hot;
        }

        if (ageSeconds <= 15)
        {
            return MonitoringFreshnessTier.Warm;
        }

        if (ageSeconds <= 300)
        {
            return MonitoringFreshnessTier.Cold;
        }

        return MonitoringFreshnessTier.Stale;
    }

    private static int ToMilliseconds(double value)
    {
        if (value <= 0)
        {
            return 0;
        }

        if (value >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private static string BuildMarketWatchdogDetail(DegradedModeSnapshot snapshot)
    {
        return
            $"State={snapshot.StateCode}; Reason={snapshot.ReasonCode}; DataAgeMs={snapshot.LatestDataAgeMilliseconds?.ToString() ?? "missing"}; ClockDriftMs={snapshot.LatestClockDriftMilliseconds?.ToString() ?? "missing"}";
    }

    private static string BuildClockDriftDetail(
        int? clockDriftMilliseconds,
        DateTime localClockUtc,
        DateTime? exchangeServerTimeUtc)
    {
        return $"ClockDriftMs={clockDriftMilliseconds?.ToString() ?? "missing"}; LocalClockUtc={localClockUtc:O}; ExchangeServerTimeUtc={exchangeServerTimeUtc?.ToString("O") ?? "missing"}; Probe={(exchangeServerTimeUtc is null ? "Unavailable" : "Succeeded")}";
    }

    private static string BuildMarketDataDriftDetail(DegradedModeSnapshot snapshot)
    {
        return $"DataAgeMs={snapshot.LatestDataAgeMilliseconds?.ToString() ?? "missing"}; Reason={snapshot.ReasonCode}";
    }

    private static string BuildStaleDataGuardDetail(DegradedModeSnapshot snapshot)
    {
        return $"SignalBlocked={snapshot.SignalFlowBlocked}; ExecutionBlocked={snapshot.ExecutionFlowBlocked}; Reason={snapshot.ReasonCode}";
    }

    private static string BuildDependencyHealthDetail(
        MonitoringTelemetrySnapshot telemetry,
        RedisLatencyProbeResult redisProbeResult)
    {
        return $"DbLatencyMs={telemetry.DatabaseLatencyMs?.ToString() ?? "missing"}; RedisLatencyMs={telemetry.RedisLatencyMs?.ToString() ?? "missing"}; RedisProbe={redisProbeResult.Status}; RedisEndpoint={redisProbeResult.Endpoint ?? "missing"}; RedisFailureCode={redisProbeResult.FailureCode ?? "none"}; SignalRConnections={telemetry.SignalRActiveConnectionCount}";
    }

    private static string BuildWorkerHeartbeatDetail(string workerName, MonitoringHealthState healthState, int ageSeconds)
    {
        return $"{workerName}; State={healthState}; AgeSeconds={ageSeconds}";
    }

    private async Task TrySendWorkerHeartbeatAlertAsync(
        WorkerHeartbeatUpdateResult result,
        string eventType,
        CancellationToken cancellationToken)
    {
        if (alertDispatchCoordinator is null ||
            result.CurrentHealthState is MonitoringHealthState.Healthy or MonitoringHealthState.Unknown ||
            result.PreviousHealthState == result.CurrentHealthState &&
            string.Equals(result.PreviousErrorCode, result.CurrentErrorCode, StringComparison.Ordinal))
        {
            if (result.PreviousHealthState != result.CurrentHealthState)
            {
                userOperationsStreamHub?.Publish(
                    new UserOperationsUpdate(
                        "*",
                        "WorkerHealthChanged",
                        null,
                        null,
                        result.CurrentHealthState.ToString(),
                        result.CurrentErrorCode,
                        timeProvider.GetUtcNow().UtcDateTime));
            }

            return;
        }

        await alertDispatchCoordinator.SendAsync(
            new CoinBot.Application.Abstractions.Alerts.AlertNotification(
                Code: $"{eventType.ToUpperInvariant()}_{result.CurrentHealthState.ToString().ToUpperInvariant()}",
                Severity: result.CurrentHealthState == MonitoringHealthState.Critical
                    ? CoinBot.Application.Abstractions.Alerts.AlertSeverity.Critical
                    : CoinBot.Application.Abstractions.Alerts.AlertSeverity.Warning,
                Title: eventType,
                Message:
                    $"EventType={eventType}; Worker={result.WorkerName}; State={result.CurrentHealthState}; FailureCode={result.CurrentErrorCode ?? "none"}; Reason={result.LastErrorMessage ?? "none"}; TimestampUtc={timeProvider.GetUtcNow().UtcDateTime:O}; Environment={ResolveEnvironmentLabel()}",
                CorrelationId: null),
            $"{eventType}:{result.WorkerKey}:{result.CurrentHealthState}:{result.CurrentErrorCode ?? "none"}",
            TimeSpan.FromMinutes(5),
            cancellationToken);
        userOperationsStreamHub?.Publish(
            new UserOperationsUpdate(
                "*",
                "WorkerHealthChanged",
                null,
                null,
                result.CurrentHealthState.ToString(),
                result.CurrentErrorCode,
                timeProvider.GetUtcNow().UtcDateTime));
    }

    private string ResolveEnvironmentLabel()
    {
        var runtimeLabel = hostEnvironment?.EnvironmentName ?? "Unknown";
        var planeLabel = hostEnvironment?.IsDevelopment() == true
            ? "Testnet"
            : "Live";

        return $"{runtimeLabel}/{planeLabel}";
    }

    private static DateTime ResolveExchangePrivateStreamHeartbeatAt(
        DateTime? lastPrivateStreamEventAtUtc,
        DateTime? lastListenKeyRenewedAtUtc,
        DateTime? lastListenKeyStartedAtUtc,
        DateTime? lastBalanceSyncedAtUtc,
        DateTime? lastPositionSyncedAtUtc,
        DateTime? lastStateReconciledAtUtc,
        DateTime utcNow)
    {
        var latest = MaxUtc(lastPrivateStreamEventAtUtc, lastListenKeyRenewedAtUtc);
        latest = MaxUtc(latest, lastListenKeyStartedAtUtc);
        latest = MaxUtc(latest, lastBalanceSyncedAtUtc);
        latest = MaxUtc(latest, lastPositionSyncedAtUtc);
        latest = MaxUtc(latest, lastStateReconciledAtUtc);

        return latest ?? utcNow;
    }

    private static DateTime? MaxUtc(DateTime? current, DateTime? candidate)
    {
        if (!candidate.HasValue)
        {
            return current;
        }

        if (!current.HasValue || candidate.Value > current.Value)
        {
            return candidate.Value;
        }

        return current;
    }

    private static MonitoringHealthState GetExchangeStreamSeverity(
        ExchangePrivateStreamConnectionState connectionState,
        DateTime utcNow,
        DateTime heartbeatAtUtc)
    {
        var ageSeconds = Math.Max(0, (int)Math.Round((utcNow - heartbeatAtUtc).TotalSeconds, MidpointRounding.AwayFromZero));

        if (connectionState is ExchangePrivateStreamConnectionState.Disconnected or ExchangePrivateStreamConnectionState.ListenKeyExpired)
        {
            return MonitoringHealthState.Critical;
        }

        if (connectionState is ExchangePrivateStreamConnectionState.Reconnecting or ExchangePrivateStreamConnectionState.Connecting)
        {
            return MonitoringHealthState.Degraded;
        }

        return ageSeconds >= 300
            ? MonitoringHealthState.Critical
            : ageSeconds >= 60
                ? MonitoringHealthState.Warning
                : MonitoringHealthState.Healthy;
    }

    private static string BuildExchangeStreamHeartbeatDetail(IReadOnlyCollection<ExchangePrivateStreamHeartbeatProjection> heartbeats)
    {
        if (heartbeats.Count == 0)
        {
            return "No private stream heartbeat available.";
        }

        return string.Join(
            "; ",
            heartbeats
                .OrderBy(entity => entity.Plane)
                .Select(entity =>
                    $"{entity.Plane}:{entity.PrivateStreamConnectionState}@{entity.HeartbeatAtUtc:O}{(string.IsNullOrWhiteSpace(entity.LastErrorCode) ? string.Empty : $"/{entity.LastErrorCode}")}"));
    }

    private sealed record WorkerHeartbeatUpdateResult(
        string WorkerKey,
        string WorkerName,
        MonitoringHealthState? PreviousHealthState,
        string? PreviousErrorCode,
        MonitoringHealthState CurrentHealthState,
        string? CurrentErrorCode,
        string? LastErrorMessage);

    private sealed record ExchangePrivateStreamHeartbeatProjection(
        Guid ExchangeAccountId,
        ExchangeDataPlane Plane,
        ExchangePrivateStreamConnectionState PrivateStreamConnectionState,
        DateTime HeartbeatAtUtc,
        int ConsecutiveStreamFailureCount,
        string? LastErrorCode);
}
