using CoinBot.Application.Abstractions.Autonomy;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Autonomy;

public sealed class WorkerRetryCoordinator(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<WorkerRetryCoordinator> logger) : IWorkerRetryCoordinator
{
    public async Task<int> RetryAsync(
        string? jobKey,
        string reason,
        CancellationToken cancellationToken = default)
    {
        _ = reason;

        var normalizedJobKey = NormalizeOptional(jobKey, 256);
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var query = dbContext.BackgroundJobStates
            .IgnoreQueryFilters()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.Status != BackgroundJobStatus.Running &&
                (entity.Status == BackgroundJobStatus.Failed ||
                 entity.Status == BackgroundJobStatus.RetryPending ||
                 entity.Status == BackgroundJobStatus.TimedOut));

        if (!string.IsNullOrWhiteSpace(normalizedJobKey))
        {
            query = query.Where(entity => entity.JobKey == normalizedJobKey);
        }

        var states = await query.ToListAsync(cancellationToken);

        if (states.Count == 0)
        {
            return 0;
        }

        foreach (var state in states)
        {
            state.Status = BackgroundJobStatus.RetryPending;
            state.NextRunAtUtc = nowUtc;
            state.LastHeartbeatAtUtc = nowUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Worker retry coordinator re-armed {StateCount} background jobs.",
            states.Count);

        return states.Count;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return null;
        }

        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : normalizedValue[..maxLength];
    }
}
