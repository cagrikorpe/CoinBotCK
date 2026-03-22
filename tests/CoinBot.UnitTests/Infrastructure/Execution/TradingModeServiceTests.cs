using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.UnitTests.Infrastructure.Execution;

public sealed class TradingModeServiceTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsBotOverride_WhenBotOverrideIsExplicitlyApproved()
    {
        await using var harness = CreateHarness();
        var user = CreateUser("user-1");

        harness.DbContext.Users.Add(user);
        harness.DbContext.TradingBots.Add(new TradingBot
        {
            OwnerUserId = user.Id,
            Name = "Momentum Runner",
            StrategyKey = "momentum-core",
            IsEnabled = true,
            TradingModeOverride = ExecutionEnvironment.Live,
            TradingModeApprovedAtUtc = DateTime.UtcNow,
            TradingModeApprovalReference = "bot-live-001"
        });
        harness.DbContext.TradingStrategies.Add(new TradingStrategy
        {
            OwnerUserId = user.Id,
            StrategyKey = "momentum-core",
            DisplayName = "Momentum Core",
            PromotionState = StrategyPromotionState.LivePublished,
            PublishedMode = ExecutionEnvironment.Live,
            PublishedAtUtc = DateTime.UtcNow,
            LivePromotionApprovedAtUtc = DateTime.UtcNow,
            LivePromotionApprovalReference = "str-live-001"
        });

        await harness.DbContext.SaveChangesAsync();

        var botId = await harness.DbContext.TradingBots
            .Select(entity => entity.Id)
            .SingleAsync();
        var resolution = await harness.TradingModeResolver.ResolveAsync(
            new TradingModeResolutionRequest(BotId: botId));

        Assert.Equal(ExecutionEnvironment.Live, resolution.EffectiveMode);
        Assert.Equal(TradingModeResolutionSource.BotOverride, resolution.ResolutionSource);
        Assert.True(resolution.HasExplicitLiveApproval);
        Assert.Contains("Bot override resolves to Live", resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_ClampsToDemo_WhenStrategyIsNotPromotedToLive()
    {
        await using var harness = CreateHarness();
        var user = CreateUser("user-2");

        harness.DbContext.Users.Add(user);
        harness.DbContext.TradingBots.Add(new TradingBot
        {
            OwnerUserId = user.Id,
            Name = "Breakout Runner",
            StrategyKey = "breakout-core",
            IsEnabled = true
        });
        harness.DbContext.TradingStrategies.Add(new TradingStrategy
        {
            OwnerUserId = user.Id,
            StrategyKey = "breakout-core",
            DisplayName = "Breakout Core",
            PromotionState = StrategyPromotionState.DemoPublished,
            PublishedMode = ExecutionEnvironment.Demo,
            PublishedAtUtc = DateTime.UtcNow
        });

        await harness.DbContext.SaveChangesAsync();
        await harness.SwitchService.SetDemoModeAsync(
            isEnabled: false,
            actor: "admin-01",
            liveApproval: new TradingModeLiveApproval("global-live-001"),
            context: "Global live enabled",
            correlationId: "corr-001");

        var botId = await harness.DbContext.TradingBots
            .Select(entity => entity.Id)
            .SingleAsync();
        var resolution = await harness.TradingModeResolver.ResolveAsync(
            new TradingModeResolutionRequest(BotId: botId));

        Assert.Equal(ExecutionEnvironment.Demo, resolution.EffectiveMode);
        Assert.Equal(TradingModeResolutionSource.StrategyPromotionGuard, resolution.ResolutionSource);
        Assert.True(resolution.HasExplicitLiveApproval);
        Assert.Contains("not promoted to Live", resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetUserTradingModeOverrideAsync_RejectsLiveOverrideWithoutApproval()
    {
        await using var harness = CreateHarness();
        var user = CreateUser("user-3");

        harness.DbContext.Users.Add(user);
        await harness.DbContext.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.TradingModeService.SetUserTradingModeOverrideAsync(
                user.Id,
                ExecutionEnvironment.Live,
                actor: "admin-02",
                context: "Attempt live user override",
                correlationId: "corr-002"));

        var auditLog = await harness.DbContext.AuditLogs.SingleAsync();
        var persistedUser = await harness.DbContext.Users.SingleAsync(entity => entity.Id == user.Id);

        Assert.Contains("Explicit live approval is required", exception.Message, StringComparison.Ordinal);
        Assert.Equal("Blocked:LiveApprovalRequired", auditLog.Outcome);
        Assert.Null(persistedUser.TradingModeOverride);
    }

    [Fact]
    public async Task SetBotTradingModeOverrideAsync_RejectsModeChange_WhenOpenExposureExists()
    {
        await using var harness = CreateHarness();
        var user = CreateUser("user-4");

        harness.DbContext.Users.Add(user);
        harness.DbContext.TradingBots.Add(new TradingBot
        {
            OwnerUserId = user.Id,
            Name = "Exposure Runner",
            StrategyKey = "exposure-core",
            IsEnabled = true,
            OpenOrderCount = 1
        });

        await harness.DbContext.SaveChangesAsync();

        var botId = await harness.DbContext.TradingBots
            .Select(entity => entity.Id)
            .SingleAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.TradingModeService.SetBotTradingModeOverrideAsync(
                botId,
                ExecutionEnvironment.Live,
                actor: "admin-03",
                liveApproval: new TradingModeLiveApproval("bot-live-003"),
                context: "Attempt bot live override",
                correlationId: "corr-003"));

        var auditLog = await harness.DbContext.AuditLogs.SingleAsync();
        var persistedBot = await harness.DbContext.TradingBots.SingleAsync(entity => entity.Id == botId);

        Assert.Contains("open orders or positions", exception.Message, StringComparison.Ordinal);
        Assert.Equal("Blocked:OpenExposurePresent", auditLog.Outcome);
        Assert.Null(persistedBot.TradingModeOverride);
    }

    [Fact]
    public async Task PublishStrategyAsync_RejectsLivePromotionWithoutDemoIsolationConfirmation()
    {
        await using var harness = CreateHarness();
        var user = CreateUser("user-5");
        var strategy = new TradingStrategy
        {
            OwnerUserId = user.Id,
            StrategyKey = "swing-core",
            DisplayName = "Swing Core",
            PromotionState = StrategyPromotionState.DemoPublished,
            PublishedMode = ExecutionEnvironment.Demo,
            PublishedAtUtc = DateTime.UtcNow
        };

        harness.DbContext.Users.Add(user);
        harness.DbContext.TradingStrategies.Add(strategy);
        await harness.DbContext.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.TradingModeService.PublishStrategyAsync(
                strategy.Id,
                ExecutionEnvironment.Live,
                actor: "admin-04",
                liveApproval: new TradingModeLiveApproval("str-live-004"),
                context: "Attempt live promotion",
                correlationId: "corr-004"));

        var auditLog = await harness.DbContext.AuditLogs.SingleAsync();

        Assert.Contains("demo-only artifacts", exception.Message, StringComparison.Ordinal);
        Assert.Equal("Blocked:DemoDataIsolationUnconfirmed", auditLog.Outcome);
    }

    [Fact]
    public async Task PublishStrategyAsync_PromotesStrategyToLive_WhenApprovalAndIsolationAreProvided()
    {
        await using var harness = CreateHarness();
        var user = CreateUser("user-6");
        var strategy = new TradingStrategy
        {
            OwnerUserId = user.Id,
            StrategyKey = "hedge-core",
            DisplayName = "Hedge Core",
            PromotionState = StrategyPromotionState.DemoPublished,
            PublishedMode = ExecutionEnvironment.Demo,
            PublishedAtUtc = DateTime.UtcNow
        };

        harness.DbContext.Users.Add(user);
        harness.DbContext.TradingStrategies.Add(strategy);
        await harness.DbContext.SaveChangesAsync();

        var result = await harness.TradingModeService.PublishStrategyAsync(
            strategy.Id,
            ExecutionEnvironment.Live,
            actor: "admin-05",
            liveApproval: new TradingModeLiveApproval(
                ApprovalReference: "str-live-005",
                ConfirmedDemoDataIsolation: true),
            context: "Promote to live",
            correlationId: "corr-005");

        var auditLog = await harness.DbContext.AuditLogs.SingleAsync();
        var persistedStrategy = await harness.DbContext.TradingStrategies.SingleAsync(entity => entity.Id == strategy.Id);

        Assert.Equal(ExecutionEnvironment.Live, result.PublishedMode);
        Assert.Equal(StrategyPromotionState.LivePublished, result.PromotionState);
        Assert.True(result.HasExplicitLiveApproval);
        Assert.Equal(ExecutionEnvironment.Live, persistedStrategy.PublishedMode);
        Assert.NotNull(persistedStrategy.LivePromotionApprovedAtUtc);
        Assert.Equal("Applied", auditLog.Outcome);
        Assert.Equal(nameof(ExecutionEnvironment.Live), auditLog.Environment);
    }

    private static TestHarness CreateHarness()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var auditLogService = new AuditLogService(dbContext, new CorrelationContextAccessor());
        var switchService = new GlobalExecutionSwitchService(dbContext, auditLogService);
        var tradingModeService = new TradingModeService(dbContext, auditLogService);

        return new TestHarness(dbContext, switchService, tradingModeService, tradingModeService);
    }

    private static ApplicationUser CreateUser(string userId)
    {
        return new ApplicationUser
        {
            Id = userId,
            UserName = $"{userId}@coinbot.local",
            NormalizedUserName = $"{userId}@COINBOT.LOCAL",
            Email = $"{userId}@coinbot.local",
            NormalizedEmail = $"{userId}@COINBOT.LOCAL"
        };
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class TestHarness(
        ApplicationDbContext dbContext,
        IGlobalExecutionSwitchService switchService,
        ITradingModeService tradingModeService,
        ITradingModeResolver tradingModeResolver) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public IGlobalExecutionSwitchService SwitchService { get; } = switchService;

        public ITradingModeService TradingModeService { get; } = tradingModeService;

        public ITradingModeResolver TradingModeResolver { get; } = tradingModeResolver;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }
    }
}
