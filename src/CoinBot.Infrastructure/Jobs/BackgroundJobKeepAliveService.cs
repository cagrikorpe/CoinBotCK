using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Jobs;

public sealed class BackgroundJobKeepAliveService(
    ApplicationDbContext dbContext,
    IDistributedJobLockManager distributedJobLockManager,
    ActiveBackgroundJobRegistry activeJobRegistry,
    TimeProvider timeProvider,
    ILogger<BackgroundJobKeepAliveService> logger)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var activeJobKeys = activeJobRegistry.GetJobKeys();

        if (activeJobKeys.Count == 0)
        {
            return 0;
        }

        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var statesByJobKey = await dbContext.BackgroundJobStates
            .Where(entity => !entity.IsDeleted && activeJobKeys.Contains(entity.JobKey))
            .ToDictionaryAsync(entity => entity.JobKey, cancellationToken);
        var renewedCount = 0;

        foreach (var jobKey in activeJobKeys)
        {
            if (await distributedJobLockManager.RenewAsync(jobKey, cancellationToken))
            {
                renewedCount++;

                if (statesByJobKey.TryGetValue(jobKey, out var state))
                {
                    state.LastHeartbeatAtUtc = utcNow;
                }

                continue;
            }

            logger.LogWarning(
                "Background job keepalive could not renew lease for {JobKey}; the local execution is being canceled.",
                jobKey);

            activeJobRegistry.Cancel(jobKey);
        }

        if (renewedCount > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return renewedCount;
    }
}
