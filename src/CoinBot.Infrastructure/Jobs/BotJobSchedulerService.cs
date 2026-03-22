using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Jobs;

public sealed class BotJobSchedulerService(
    ApplicationDbContext dbContext,
    IDistributedJobLockManager distributedJobLockManager,
    IBotWorkerJobProcessor processor,
    ActiveBackgroundJobRegistry activeJobRegistry,
    IOptions<JobOrchestrationOptions> options,
    TimeProvider timeProvider,
    ILogger<BotJobSchedulerService> logger)
{
    private readonly JobOrchestrationOptions optionsValue = options.Value;

    public async Task<int> RunDueJobsAsync(CancellationToken cancellationToken = default)
    {
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var bots = await dbContext.TradingBots
            .IgnoreQueryFilters()
            .Where(entity => entity.IsEnabled && !entity.IsDeleted)
            .OrderBy(entity => entity.Id)
            .Take(optionsValue.MaxBotsPerCycle)
            .ToListAsync(cancellationToken);

        if (bots.Count == 0)
        {
            return 0;
        }

        var statesByBotId = await EnsureStatesAsync(bots, utcNow, cancellationToken);
        var triggeredCount = 0;

        foreach (var bot in bots)
        {
            var state = statesByBotId[bot.Id];

            if (state.NextRunAtUtc > utcNow || state.Status == BackgroundJobStatus.Running)
            {
                continue;
            }

            if (!await distributedJobLockManager.TryAcquireAsync(state.JobKey, state.JobType, cancellationToken))
            {
                continue;
            }

            state.Status = BackgroundJobStatus.Running;
            state.LastStartedAtUtc = utcNow;
            state.LastHeartbeatAtUtc = utcNow;
            state.LastErrorCode = null;
            state.IdempotencyKey ??= CreateIdempotencyKey(state.JobKey, state.NextRunAtUtc);
            await dbContext.SaveChangesAsync(cancellationToken);

            triggeredCount++;

            using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            activeJobRegistry.Register(state.JobKey, linkedCancellationTokenSource);

            try
            {
                var result = await processor.ProcessAsync(
                    bot,
                    state.IdempotencyKey,
                    linkedCancellationTokenSource.Token);
                var completedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

                ApplyProcessResult(state, result, completedAtUtc);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var canceledAtUtc = timeProvider.GetUtcNow().UtcDateTime;
                ApplyProcessResult(
                    state,
                    BackgroundJobProcessResult.RetryableFailure("LeaseRenewalLost"),
                    canceledAtUtc);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                var failedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

                logger.LogWarning(
                    exception,
                    "Bot orchestration cycle failed for BotId {BotId}.",
                    bot.Id);

                ApplyProcessResult(
                    state,
                    BackgroundJobProcessResult.RetryableFailure("UnhandledException"),
                    failedAtUtc);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            finally
            {
                activeJobRegistry.Unregister(state.JobKey);
                await distributedJobLockManager.ReleaseAsync(state.JobKey, CancellationToken.None);
            }
        }

        return triggeredCount;
    }

    private async Task<Dictionary<Guid, BackgroundJobState>> EnsureStatesAsync(
        IReadOnlyCollection<TradingBot> bots,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var botIds = bots.Select(bot => bot.Id).ToArray();
        var statesByBotId = await dbContext.BackgroundJobStates
            .Where(entity =>
                !entity.IsDeleted &&
                entity.JobType == BackgroundJobTypes.BotExecution &&
                entity.BotId.HasValue &&
                botIds.Contains(entity.BotId.Value))
            .ToDictionaryAsync(entity => entity.BotId!.Value, cancellationToken);

        foreach (var bot in bots)
        {
            if (statesByBotId.ContainsKey(bot.Id))
            {
                continue;
            }

            var state = new BackgroundJobState
            {
                JobKey = CreateBotJobKey(bot.Id),
                JobType = BackgroundJobTypes.BotExecution,
                BotId = bot.Id,
                Status = BackgroundJobStatus.Pending,
                NextRunAtUtc = utcNow
            };

            dbContext.BackgroundJobStates.Add(state);
            statesByBotId[bot.Id] = state;
        }

        if (dbContext.ChangeTracker.Entries<BackgroundJobState>().Any(entry => entry.State == EntityState.Added))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return statesByBotId;
    }

    private void ApplyProcessResult(BackgroundJobState state, BackgroundJobProcessResult result, DateTime utcNow)
    {
        if (result.IsSuccessful)
        {
            state.Status = BackgroundJobStatus.Succeeded;
            state.LastCompletedAtUtc = utcNow;
            state.LastHeartbeatAtUtc = utcNow;
            state.LastErrorCode = null;
            state.ConsecutiveFailureCount = 0;
            state.IdempotencyKey = null;
            state.NextRunAtUtc = utcNow.AddSeconds(optionsValue.BotExecutionIntervalSeconds);
            return;
        }

        state.LastFailedAtUtc = utcNow;
        state.LastHeartbeatAtUtc = utcNow;
        state.LastErrorCode = NormalizeFailureCode(result.ErrorCode);
        state.ConsecutiveFailureCount++;

        if (result.IsRetryableFailure && state.ConsecutiveFailureCount <= optionsValue.MaxRetryAttempts)
        {
            state.Status = BackgroundJobStatus.RetryPending;
            state.NextRunAtUtc = utcNow.Add(CalculateRetryDelay(state.ConsecutiveFailureCount));
            return;
        }

        state.Status = BackgroundJobStatus.Failed;
        state.IdempotencyKey = null;
        state.NextRunAtUtc = utcNow.AddSeconds(optionsValue.BotExecutionIntervalSeconds);
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

    private static string CreateBotJobKey(Guid botId)
    {
        return $"bot-execution:{botId:N}";
    }

    private static string CreateIdempotencyKey(string jobKey, DateTime scheduledRunAtUtc)
    {
        return $"{jobKey}:{scheduledRunAtUtc:yyyyMMddHHmmss}";
    }

    private static string NormalizeFailureCode(string? errorCode)
    {
        var normalizedErrorCode = errorCode?.Trim();

        return string.IsNullOrWhiteSpace(normalizedErrorCode)
            ? "UnknownFailure"
            : normalizedErrorCode.Length <= 64
                ? normalizedErrorCode
                : normalizedErrorCode[..64];
    }
}
