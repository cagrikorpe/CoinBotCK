using CoinBot.Domain.Entities;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Jobs;

public sealed class DistributedJobLockManager(
    ApplicationDbContext dbContext,
    IWorkerInstanceAccessor workerInstanceAccessor,
    IOptions<JobOrchestrationOptions> options,
    TimeProvider timeProvider,
    ILogger<DistributedJobLockManager> logger) : IDistributedJobLockManager
{
    private readonly JobOrchestrationOptions optionsValue = options.Value;

    public async Task<bool> TryAcquireAsync(string jobKey, string jobType, CancellationToken cancellationToken = default)
    {
        var normalizedJobKey = NormalizeRequired(jobKey, nameof(jobKey), 256);
        var normalizedJobType = NormalizeRequired(jobType, nameof(jobType), 64);
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var leaseExpiresAtUtc = utcNow.AddSeconds(optionsValue.LeaseDurationSeconds);
        var existingLock = await dbContext.BackgroundJobLocks
            .SingleOrDefaultAsync(entity => entity.JobKey == normalizedJobKey && !entity.IsDeleted, cancellationToken);

        if (existingLock is null)
        {
            dbContext.BackgroundJobLocks.Add(new BackgroundJobLock
            {
                JobKey = normalizedJobKey,
                JobType = normalizedJobType,
                WorkerInstanceId = workerInstanceAccessor.WorkerInstanceId,
                AcquiredAtUtc = utcNow,
                LastKeepAliveAtUtc = utcNow,
                LeaseExpiresAtUtc = leaseExpiresAtUtc
            });

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException)
            {
                logger.LogDebug(
                    "Distributed job lock acquisition raced for {JobType} {JobKey}.",
                    normalizedJobType,
                    normalizedJobKey);

                return false;
            }
        }

        if (existingLock.LeaseExpiresAtUtc > utcNow)
        {
            return false;
        }

        existingLock.JobType = normalizedJobType;
        existingLock.WorkerInstanceId = workerInstanceAccessor.WorkerInstanceId;
        existingLock.AcquiredAtUtc = utcNow;
        existingLock.LastKeepAliveAtUtc = utcNow;
        existingLock.LeaseExpiresAtUtc = leaseExpiresAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> RenewAsync(string jobKey, CancellationToken cancellationToken = default)
    {
        var normalizedJobKey = NormalizeRequired(jobKey, nameof(jobKey), 256);
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var existingLock = await dbContext.BackgroundJobLocks
            .SingleOrDefaultAsync(entity => entity.JobKey == normalizedJobKey && !entity.IsDeleted, cancellationToken);

        if (existingLock is null ||
            existingLock.WorkerInstanceId != workerInstanceAccessor.WorkerInstanceId ||
            existingLock.LeaseExpiresAtUtc <= utcNow)
        {
            return false;
        }

        existingLock.LastKeepAliveAtUtc = utcNow;
        existingLock.LeaseExpiresAtUtc = utcNow.AddSeconds(optionsValue.LeaseDurationSeconds);
        await dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> ReleaseAsync(string jobKey, CancellationToken cancellationToken = default)
    {
        var normalizedJobKey = NormalizeRequired(jobKey, nameof(jobKey), 256);
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var existingLock = await dbContext.BackgroundJobLocks
            .SingleOrDefaultAsync(entity => entity.JobKey == normalizedJobKey && !entity.IsDeleted, cancellationToken);

        if (existingLock is null || existingLock.WorkerInstanceId != workerInstanceAccessor.WorkerInstanceId)
        {
            return false;
        }

        existingLock.LeaseExpiresAtUtc = utcNow;
        existingLock.LastKeepAliveAtUtc = utcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    private static string NormalizeRequired(string? value, string parameterName, int maxLength)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        if (normalizedValue.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"The value cannot exceed {maxLength} characters.");
        }

        return normalizedValue;
    }
}
