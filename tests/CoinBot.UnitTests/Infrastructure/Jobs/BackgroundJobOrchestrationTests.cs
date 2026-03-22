using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Jobs;

public sealed class BackgroundJobOrchestrationTests
{
    [Fact]
    public async Task DistributedLock_BlocksSecondWorkerUntilLeaseExpires()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var databaseRoot = new InMemoryDatabaseRoot();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        var options = Options.Create(CreateOptions());
        await using var firstContext = CreateContext(databaseName, databaseRoot);
        await using var secondContext = CreateContext(databaseName, databaseRoot);
        var firstManager = new DistributedJobLockManager(
            firstContext,
            new FakeWorkerInstanceAccessor("worker-a"),
            options,
            timeProvider,
            NullLogger<DistributedJobLockManager>.Instance);
        var secondManager = new DistributedJobLockManager(
            secondContext,
            new FakeWorkerInstanceAccessor("worker-b"),
            options,
            timeProvider,
            NullLogger<DistributedJobLockManager>.Instance);

        Assert.True(await firstManager.TryAcquireAsync("bot-execution:test-bot", BackgroundJobTypes.BotExecution));
        Assert.False(await secondManager.TryAcquireAsync("bot-execution:test-bot", BackgroundJobTypes.BotExecution));

        timeProvider.Advance(TimeSpan.FromSeconds(31));

        Assert.True(await secondManager.TryAcquireAsync("bot-execution:test-bot", BackgroundJobTypes.BotExecution));
    }

    [Fact]
    public async Task Scheduler_AppliesRetryBackoff_AndReusesIdempotencyKeyAcrossRetry()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        await using var context = CreateContext();
        var bot = new TradingBot
        {
            OwnerUserId = "user-001",
            Name = "Momentum",
            StrategyKey = "momentum-core",
            IsEnabled = true
        };
        context.TradingBots.Add(bot);
        await context.SaveChangesAsync();

        var processor = new FakeBotWorkerJobProcessor(
        [
            BackgroundJobProcessResult.RetryableFailure("TransientFailure"),
            BackgroundJobProcessResult.Success()
        ]);
        var scheduler = new BotJobSchedulerService(
            context,
            new DistributedJobLockManager(
                context,
                new FakeWorkerInstanceAccessor("worker-main"),
                Options.Create(CreateOptions()),
                timeProvider,
                NullLogger<DistributedJobLockManager>.Instance),
            processor,
            new ActiveBackgroundJobRegistry(),
            Options.Create(CreateOptions()),
            timeProvider,
            NullLogger<BotJobSchedulerService>.Instance);

        var firstTriggeredCount = await scheduler.RunDueJobsAsync();
        var firstState = await context.BackgroundJobStates.SingleAsync();
        var firstIdempotencyKey = firstState.IdempotencyKey;

        var immediateTriggeredCount = await scheduler.RunDueJobsAsync();

        timeProvider.Advance(TimeSpan.FromSeconds(5));

        var secondTriggeredCount = await scheduler.RunDueJobsAsync();
        var secondState = await context.BackgroundJobStates.SingleAsync();

        Assert.Equal(1, firstTriggeredCount);
        Assert.Equal(0, immediateTriggeredCount);
        Assert.Equal(1, secondTriggeredCount);
        Assert.Equal(2, processor.Invocations.Count);
        Assert.Equal(firstIdempotencyKey, processor.Invocations[0].IdempotencyKey);
        Assert.Equal(firstIdempotencyKey, processor.Invocations[1].IdempotencyKey);
        Assert.Equal(BackgroundJobStatus.Succeeded, secondState.Status);
        Assert.Null(secondState.IdempotencyKey);
        Assert.Equal(0, secondState.ConsecutiveFailureCount);
    }

    [Fact]
    public async Task KeepAlive_RenewsLeaseAndUpdatesHeartbeat()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        var options = Options.Create(CreateOptions());
        await using var context = CreateContext();
        var jobKey = "bot-execution:keepalive";
        var registry = new ActiveBackgroundJobRegistry();
        var lockManager = new DistributedJobLockManager(
            context,
            new FakeWorkerInstanceAccessor("worker-keepalive"),
            options,
            timeProvider,
            NullLogger<DistributedJobLockManager>.Instance);

        context.BackgroundJobStates.Add(new BackgroundJobState
        {
            JobKey = jobKey,
            JobType = BackgroundJobTypes.BotExecution,
            Status = BackgroundJobStatus.Running,
            NextRunAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            LastStartedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            LastHeartbeatAtUtc = timeProvider.GetUtcNow().UtcDateTime
        });
        await context.SaveChangesAsync();
        Assert.True(await lockManager.TryAcquireAsync(jobKey, BackgroundJobTypes.BotExecution));
        registry.Register(jobKey, new CancellationTokenSource());

        timeProvider.Advance(TimeSpan.FromSeconds(5));

        var service = new BackgroundJobKeepAliveService(
            context,
            lockManager,
            registry,
            timeProvider,
            NullLogger<BackgroundJobKeepAliveService>.Instance);

        await service.RunAsync();

        var state = await context.BackgroundJobStates.SingleAsync(entity => entity.JobKey == jobKey);
        var lease = await context.BackgroundJobLocks.SingleAsync(entity => entity.JobKey == jobKey);

        Assert.Equal(timeProvider.GetUtcNow().UtcDateTime, state.LastHeartbeatAtUtc);
        Assert.True(lease.LeaseExpiresAtUtc > timeProvider.GetUtcNow().UtcDateTime);

        registry.Unregister(jobKey);
    }

    [Fact]
    public async Task Cleanup_NormalizesDisabledBotStateAndExpiredLock()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        await using var context = CreateContext();
        var bot = new TradingBot
        {
            OwnerUserId = "user-002",
            Name = "Disabled Bot",
            StrategyKey = "disabled-core",
            IsEnabled = false
        };
        context.TradingBots.Add(bot);
        await context.SaveChangesAsync();

        context.BackgroundJobStates.Add(new BackgroundJobState
        {
            JobKey = $"bot-execution:{bot.Id:N}",
            JobType = BackgroundJobTypes.BotExecution,
            BotId = bot.Id,
            Status = BackgroundJobStatus.RetryPending,
            NextRunAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            IdempotencyKey = "retry-key"
        });
        context.BackgroundJobLocks.Add(new BackgroundJobLock
        {
            JobKey = $"bot-execution:{bot.Id:N}",
            JobType = BackgroundJobTypes.BotExecution,
            WorkerInstanceId = "worker-cleanup",
            AcquiredAtUtc = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-10),
            LastKeepAliveAtUtc = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-10),
            LeaseExpiresAtUtc = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-6)
        });
        await context.SaveChangesAsync();

        var service = new BackgroundJobCleanupService(
            context,
            Options.Create(CreateOptions()),
            timeProvider,
            NullLogger<BackgroundJobCleanupService>.Instance);

        await service.RunAsync();

        var state = await context.BackgroundJobStates.SingleAsync();
        var lease = await context.BackgroundJobLocks.SingleAsync();

        Assert.Equal(BackgroundJobStatus.Failed, state.Status);
        Assert.Equal(DateTime.MaxValue, state.NextRunAtUtc);
        Assert.Null(state.IdempotencyKey);
        Assert.Equal("BotDisabled", state.LastErrorCode);
        Assert.Equal(string.Empty, lease.WorkerInstanceId);
    }

    [Fact]
    public async Task Watchdog_TimesOutExpiredRunningJob_AndPreservesIdempotencyKeyForRetry()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        await using var context = CreateContext();
        var jobKey = "bot-execution:watchdog";

        context.BackgroundJobStates.Add(new BackgroundJobState
        {
            JobKey = jobKey,
            JobType = BackgroundJobTypes.BotExecution,
            Status = BackgroundJobStatus.Running,
            NextRunAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            LastStartedAtUtc = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-2),
            LastHeartbeatAtUtc = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-2),
            IdempotencyKey = "watchdog-key"
        });
        context.BackgroundJobLocks.Add(new BackgroundJobLock
        {
            JobKey = jobKey,
            JobType = BackgroundJobTypes.BotExecution,
            WorkerInstanceId = "worker-watchdog",
            AcquiredAtUtc = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-2),
            LastKeepAliveAtUtc = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-2),
            LeaseExpiresAtUtc = timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-1)
        });
        await context.SaveChangesAsync();

        var service = new BackgroundJobWatchdogService(
            context,
            Options.Create(CreateOptions()),
            timeProvider,
            NullLogger<BackgroundJobWatchdogService>.Instance);

        await service.RunAsync();

        var state = await context.BackgroundJobStates.SingleAsync(entity => entity.JobKey == jobKey);

        Assert.Equal(BackgroundJobStatus.TimedOut, state.Status);
        Assert.Equal("watchdog-key", state.IdempotencyKey);
        Assert.Equal("WatchdogTimeout", state.LastErrorCode);
        Assert.Equal(1, state.ConsecutiveFailureCount);
        Assert.Equal(timeProvider.GetUtcNow().UtcDateTime.AddSeconds(5), state.NextRunAtUtc);
    }

    private static JobOrchestrationOptions CreateOptions()
    {
        return new JobOrchestrationOptions
        {
            Enabled = true,
            SchedulerPollIntervalSeconds = 5,
            BotExecutionIntervalSeconds = 30,
            LeaseDurationSeconds = 30,
            KeepAliveIntervalSeconds = 10,
            CleanupIntervalSeconds = 60,
            WatchdogIntervalSeconds = 30,
            WatchdogTimeoutSeconds = 90,
            CleanupGracePeriodSeconds = 300,
            MaxRetryAttempts = 3,
            InitialRetryDelaySeconds = 5,
            MaxRetryDelaySeconds = 60,
            MaxBotsPerCycle = 100
        };
    }

    private static ApplicationDbContext CreateContext(
        string? databaseName = null,
        InMemoryDatabaseRoot? databaseRoot = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();

        if (databaseRoot is null)
        {
            optionsBuilder.UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString("N"));
        }
        else
        {
            optionsBuilder.UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString("N"), databaseRoot);
        }

        return new ApplicationDbContext(optionsBuilder.Options, new TestDataScopeContext());
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeWorkerInstanceAccessor(string workerInstanceId) : IWorkerInstanceAccessor
    {
        public string WorkerInstanceId { get; } = workerInstanceId;
    }

    private sealed class FakeBotWorkerJobProcessor(IReadOnlyCollection<BackgroundJobProcessResult> results) : IBotWorkerJobProcessor
    {
        private readonly Queue<BackgroundJobProcessResult> remainingResults = new(results);

        public List<Invocation> Invocations { get; } = [];

        public Task<BackgroundJobProcessResult> ProcessAsync(
            TradingBot bot,
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Invocations.Add(new Invocation(bot.Id, idempotencyKey));

            return Task.FromResult(remainingResults.Count > 0
                ? remainingResults.Dequeue()
                : BackgroundJobProcessResult.Success());
        }
    }

    private sealed record Invocation(Guid BotId, string IdempotencyKey);
}
