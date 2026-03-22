using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Jobs;

public sealed class BackgroundJobWatchdogService(
    ApplicationDbContext dbContext,
    IOptions<JobOrchestrationOptions> options,
    TimeProvider timeProvider,
    ILogger<BackgroundJobWatchdogService> logger)
{
    private readonly JobOrchestrationOptions optionsValue = options.Value;

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var heartbeatCutoffUtc = utcNow.AddSeconds(-optionsValue.WatchdogTimeoutSeconds);
        var runningStates = await dbContext.BackgroundJobStates
            .Where(entity =>
                !entity.IsDeleted &&
                entity.Status == BackgroundJobStatus.Running &&
                entity.LastHeartbeatAtUtc.HasValue &&
                entity.LastHeartbeatAtUtc.Value <= heartbeatCutoffUtc)
            .ToListAsync(cancellationToken);

        if (runningStates.Count == 0)
        {
            return 0;
        }

        var jobKeys = runningStates
            .Select(entity => entity.JobKey)
            .ToArray();
        var locksByJobKey = await dbContext.BackgroundJobLocks
            .Where(entity => !entity.IsDeleted && jobKeys.Contains(entity.JobKey))
            .ToDictionaryAsync(entity => entity.JobKey, cancellationToken);
        var timedOutCount = 0;

        foreach (var state in runningStates)
        {
            if (locksByJobKey.TryGetValue(state.JobKey, out var activeLock) &&
                activeLock.LeaseExpiresAtUtc > utcNow)
            {
                continue;
            }

            state.Status = BackgroundJobStatus.TimedOut;
            state.LastFailedAtUtc = utcNow;
            state.LastErrorCode = "WatchdogTimeout";
            state.ConsecutiveFailureCount++;
            state.NextRunAtUtc = utcNow.Add(CalculateRetryDelay(state.ConsecutiveFailureCount));

            timedOutCount++;
        }

        if (timedOutCount > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogWarning(
                "Background job watchdog timed out {TimedOutCount} long-running jobs.",
                timedOutCount);
        }

        return timedOutCount;
    }

    private TimeSpan CalculateRetryDelay(int consecutiveFailureCount)
    {
        var boundedFailureCount = Math.Max(1, consecutiveFailureCount);
        var multiplier = Math.Pow(2, boundedFailureCount - 1);
        var computedDelaySeconds = (int)Math.Round(
            optionsValue.InitialRetryDelaySeconds * multiplier,
            MidpointRounding.AwayFromZero);
        var boundedDelaySeconds = Math.Min(optionsValue.MaxRetryDelaySeconds, computedDelaySeconds);

        return TimeSpan.FromSeconds(boundedDelaySeconds);
    }
}
