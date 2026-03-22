using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Jobs;

public sealed class BackgroundJobCleanupService(
    ApplicationDbContext dbContext,
    IOptions<JobOrchestrationOptions> options,
    TimeProvider timeProvider,
    ILogger<BackgroundJobCleanupService> logger)
{
    private readonly JobOrchestrationOptions optionsValue = options.Value;

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var cleanupCutoffUtc = utcNow.AddSeconds(-optionsValue.CleanupGracePeriodSeconds);
        var activeBotIds = await dbContext.TradingBots
            .IgnoreQueryFilters()
            .Where(entity => entity.IsEnabled && !entity.IsDeleted)
            .Select(entity => entity.Id)
            .ToListAsync(cancellationToken);
        var activeBotIdSet = activeBotIds.ToHashSet();
        var cleanedCount = 0;

        var staleStates = await dbContext.BackgroundJobStates
            .Where(entity =>
                !entity.IsDeleted &&
                entity.JobType == BackgroundJobTypes.BotExecution &&
                entity.BotId.HasValue &&
                !activeBotIdSet.Contains(entity.BotId.Value) &&
                entity.Status != BackgroundJobStatus.Running)
            .ToListAsync(cancellationToken);

        foreach (var state in staleStates)
        {
            state.Status = BackgroundJobStatus.Failed;
            state.IdempotencyKey = null;
            state.LastErrorCode = "BotDisabled";
            state.NextRunAtUtc = DateTime.MaxValue;
            cleanedCount++;
        }

        var staleLocks = await dbContext.BackgroundJobLocks
            .Where(entity =>
                !entity.IsDeleted &&
                entity.LeaseExpiresAtUtc <= cleanupCutoffUtc &&
                !string.IsNullOrWhiteSpace(entity.WorkerInstanceId))
            .ToListAsync(cancellationToken);

        foreach (var staleLock in staleLocks)
        {
            staleLock.WorkerInstanceId = string.Empty;
            staleLock.LastKeepAliveAtUtc = staleLock.LeaseExpiresAtUtc;
            cleanedCount++;
        }

        if (cleanedCount > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Background job cleanup normalized {CleanupCount} stale records.",
                cleanedCount);
        }

        return cleanedCount;
    }
}
