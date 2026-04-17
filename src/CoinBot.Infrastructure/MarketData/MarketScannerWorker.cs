using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.MarketData;

public sealed class MarketScannerWorker(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<MarketScannerOptions> options,
    TimeProvider timeProvider,
    ILogger<MarketScannerWorker> logger,
    IHostEnvironment? hostEnvironment = null) : BackgroundService
{
    private const string ScannerCycleJobKey = "market-scanner:cycle";
    private const string ScannerCycleJobType = "MarketScanner";
    private readonly MarketScannerOptions optionsValue = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!optionsValue.Enabled)
        {
            logger.LogInformation("Market scanner worker is disabled by configuration.");
            return;
        }

        if (!ShouldExecuteOnCurrentHost())
        {
            logger.LogInformation(
                "Market scanner worker is assigned to host {AssignedHost} and will not run on {CurrentHost}.",
                NormalizeHostLabel(optionsValue.ExecutionHost) ?? "any",
                ResolveCurrentHostLabel(hostEnvironment?.ApplicationName));
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var cycleStartedAtUtc = timeProvider.GetUtcNow();
            ScannerExecutionLeaseDecision leaseDecision;

            try
            {
                leaseDecision = await AcquireExecutionLeaseAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (!leaseDecision.ShouldRun)
            {
                await DelayAsync(leaseDecision.Delay, stoppingToken);
                continue;
            }

            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Market scanner worker cycle failed.");
                await RecordFailureAsync(exception, stoppingToken);
            }
            finally
            {
                if (leaseDecision.LockAcquired)
                {
                    await ReleaseExecutionLeaseAsync(stoppingToken);
                }
            }

            await DelayAsync(ResolveCycleDelay(cycleStartedAtUtc), stoppingToken);
        }
    }

    internal bool ShouldExecuteOnCurrentHost()
    {
        var assignedHost = NormalizeHostLabel(optionsValue.ExecutionHost);
        return assignedHost is null or "any" ||
               string.Equals(assignedHost, ResolveCurrentHostLabel(hostEnvironment?.ApplicationName), StringComparison.OrdinalIgnoreCase);
    }

    internal TimeSpan ResolveCycleDelay(DateTimeOffset cycleStartedAtUtc)
    {
        var nextDueAtUtc = cycleStartedAtUtc.AddSeconds(optionsValue.ScanIntervalSeconds);
        var remaining = nextDueAtUtc - timeProvider.GetUtcNow();
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    internal async Task<ScannerExecutionLeaseDecision> AcquireExecutionLeaseAsync(CancellationToken cancellationToken = default)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var lockManager = scope.ServiceProvider.GetService<IDistributedJobLockManager>();
        if (lockManager is null)
        {
            return ScannerExecutionLeaseDecision.RunNow(TimeSpan.Zero, lockAcquired: false);
        }

        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var existingLease = await dbContext.BackgroundJobLocks
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => !entity.IsDeleted && entity.JobKey == ScannerCycleJobKey)
            .Select(entity => new ScannerCycleLeaseSnapshot(entity.AcquiredAtUtc, entity.LeaseExpiresAtUtc))
            .SingleOrDefaultAsync(cancellationToken);

        var dueDelay = ResolveDueDelay(existingLease, utcNow);
        if (dueDelay > TimeSpan.Zero)
        {
            return ScannerExecutionLeaseDecision.Skip(dueDelay);
        }

        var lockAcquired = await lockManager.TryAcquireAsync(ScannerCycleJobKey, ScannerCycleJobType, cancellationToken);
        if (lockAcquired)
        {
            return ScannerExecutionLeaseDecision.RunNow(TimeSpan.Zero, lockAcquired: true);
        }

        var racedLease = await dbContext.BackgroundJobLocks
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => !entity.IsDeleted && entity.JobKey == ScannerCycleJobKey)
            .Select(entity => new ScannerCycleLeaseSnapshot(entity.AcquiredAtUtc, entity.LeaseExpiresAtUtc))
            .SingleOrDefaultAsync(cancellationToken);

        var retryDelay = ResolveDueDelay(racedLease, utcNow);
        return ScannerExecutionLeaseDecision.Skip(retryDelay > TimeSpan.Zero
            ? retryDelay
            : TimeSpan.FromSeconds(optionsValue.ScanIntervalSeconds));
    }

    internal async Task ReleaseExecutionLeaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var lockManager = scope.ServiceProvider.GetService<IDistributedJobLockManager>();
            if (lockManager is null)
            {
                return;
            }

            await lockManager.ReleaseAsync(ScannerCycleJobKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Market scanner worker could not release execution lease cleanly.");
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<MarketScannerService>();
        await service.RunOnceAsync(cancellationToken);
    }

    internal async Task RecordFailureAsync(Exception exception, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
            var entity = await dbContext.WorkerHeartbeats
                .SingleOrDefaultAsync(item => item.WorkerKey == MarketScannerService.WorkerKey, cancellationToken);

            if (entity is null)
            {
                entity = new WorkerHeartbeat
                {
                    Id = Guid.NewGuid(),
                    WorkerKey = MarketScannerService.WorkerKey
                };
                dbContext.WorkerHeartbeats.Add(entity);
            }

            var failureCode = ResolveFailureCode(exception);
            var failureDetail = ResolveFailureDetail(exception, failureCode);

            entity.WorkerName = MarketScannerService.WorkerName;
            entity.HealthState = MonitoringHealthState.Critical;
            entity.FreshnessTier = MonitoringFreshnessTier.Hot;
            entity.CircuitBreakerState = CircuitBreakerStateCode.Cooldown;
            entity.LastHeartbeatAtUtc = nowUtc;
            entity.LastUpdatedAtUtc = nowUtc;
            entity.ConsecutiveFailureCount += 1;
            entity.LastErrorCode = failureCode;
            entity.LastErrorMessage = Truncate(exception.Message, 1024);
            entity.SnapshotAgeSeconds = 0;
            entity.Detail = Truncate(failureDetail, 2048);

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception heartbeatException)
        {
            logger.LogWarning(heartbeatException, "Market scanner worker could not persist failure heartbeat.");
        }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        await Task.Delay(delay, cancellationToken);
    }

    private TimeSpan ResolveDueDelay(ScannerCycleLeaseSnapshot? leaseSnapshot, DateTime utcNow)
    {
        if (leaseSnapshot is null)
        {
            return TimeSpan.Zero;
        }

        var nextDueAtUtc = leaseSnapshot.AcquiredAtUtc.AddSeconds(optionsValue.ScanIntervalSeconds);
        var earliestRunAtUtc = leaseSnapshot.LeaseExpiresAtUtc > nextDueAtUtc
            ? leaseSnapshot.LeaseExpiresAtUtc
            : nextDueAtUtc;

        return earliestRunAtUtc > utcNow
            ? earliestRunAtUtc - utcNow
            : TimeSpan.Zero;
    }

    private static string ResolveFailureCode(Exception exception)
    {
        return exception switch
        {
            MarketScannerNumericOverflowException overflowException => overflowException.ErrorCode,
            MarketScannerPoisonedCandleAuditException poisonedCandleException => poisonedCandleException.ErrorCode,
            _ => exception.GetType().Name
        };
    }

    private static string ResolveFailureDetail(Exception exception, string failureCode)
    {
        return exception switch
        {
            MarketScannerNumericOverflowException overflowException => overflowException.Detail,
            MarketScannerPoisonedCandleAuditException poisonedCandleException => poisonedCandleException.Detail,
            _ => $"Worker={MarketScannerService.WorkerName}; Failure={failureCode}; Message={exception.Message}"
        };
    }

    internal static string ResolveCurrentHostLabel(string? applicationName)
    {
        var normalizedApplicationName = applicationName?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedApplicationName))
        {
            return "unknown";
        }

        if (normalizedApplicationName.Contains("Worker", StringComparison.OrdinalIgnoreCase))
        {
            return "worker";
        }

        if (normalizedApplicationName.Contains("Web", StringComparison.OrdinalIgnoreCase))
        {
            return "web";
        }

        return normalizedApplicationName;
    }

    internal static string? NormalizeHostLabel(string? hostLabel)
    {
        var normalizedHostLabel = hostLabel?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedHostLabel))
        {
            return null;
        }

        if (normalizedHostLabel.Equals("*", StringComparison.OrdinalIgnoreCase) ||
            normalizedHostLabel.Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            return "any";
        }

        if (normalizedHostLabel.Contains("Worker", StringComparison.OrdinalIgnoreCase))
        {
            return "worker";
        }

        if (normalizedHostLabel.Contains("Web", StringComparison.OrdinalIgnoreCase))
        {
            return "web";
        }

        return normalizedHostLabel;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }

    internal sealed record ScannerExecutionLeaseDecision(bool ShouldRun, TimeSpan Delay, bool LockAcquired)
    {
        public static ScannerExecutionLeaseDecision RunNow(TimeSpan delay, bool lockAcquired)
            => new(true, delay, lockAcquired);

        public static ScannerExecutionLeaseDecision Skip(TimeSpan delay)
            => new(false, delay, false);
    }

    internal sealed record ScannerCycleLeaseSnapshot(DateTime AcquiredAtUtc, DateTime LeaseExpiresAtUtc);
}
