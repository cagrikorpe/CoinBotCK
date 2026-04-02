using CoinBot.Application.Abstractions.Bots;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Jobs;

public sealed class BotPilotControlServiceTests
{
    [Fact]
    public async Task SetEnabledAsync_EnablesBot_WhenPilotConstraintsAreSatisfied()
    {
        await using var context = CreateContext();
        var bot = await SeedPilotBotAsync(context, isEnabled: false);
        var timeProvider = new FakeTimeProvider();
        var service = new BotPilotControlService(context, timeProvider, Options.Create(CreatePilotOptions()));

        var result = await service.SetEnabledAsync(
            bot.OwnerUserId,
            bot.Id,
            isEnabled: true,
            actor: $"user:{bot.OwnerUserId}");

        var persistedBot = await context.TradingBots.SingleAsync(entity => entity.Id == bot.Id);

        Assert.True(result.IsSuccessful);
        Assert.True(persistedBot.IsEnabled);
    }

    [Fact]
    public async Task SetEnabledAsync_DisablesBot_WhenOwnedBotExists()
    {
        await using var context = CreateContext();
        var bot = await SeedPilotBotAsync(context, isEnabled: true);
        var timeProvider = new FakeTimeProvider();
        var service = new BotPilotControlService(context, timeProvider, Options.Create(CreatePilotOptions()));

        var result = await service.SetEnabledAsync(
            bot.OwnerUserId,
            bot.Id,
            isEnabled: false,
            actor: $"user:{bot.OwnerUserId}");

        var persistedBot = await context.TradingBots.SingleAsync(entity => entity.Id == bot.Id);

        Assert.True(result.IsSuccessful);
        Assert.False(persistedBot.IsEnabled);
    }

    [Fact]
    public async Task SetEnabledAsync_BlocksEnable_WhenAnotherEnabledBotUsesSameSymbol_ForSameOwner()
    {
        await using var context = CreateContext();
        var firstBot = await SeedPilotBotAsync(context, ownerUserId: "user-bot-1", isEnabled: true);
        var secondBot = await SeedPilotBotAsync(context, ownerUserId: "user-bot-1", isEnabled: false);
        var service = new BotPilotControlService(context, new FakeTimeProvider(), Options.Create(CreatePilotOptions()));

        var result = await service.SetEnabledAsync(
            secondBot.OwnerUserId,
            secondBot.Id,
            isEnabled: true,
            actor: $"user:{secondBot.OwnerUserId}");

        var persistedSecondBot = await context.TradingBots.SingleAsync(entity => entity.Id == secondBot.Id);

        Assert.False(result.IsSuccessful);
        Assert.Equal("PilotSymbolConflictMultipleEnabledBots", result.FailureCode);
        Assert.False(persistedSecondBot.IsEnabled);
        Assert.True(firstBot.IsEnabled);
    }

    [Fact]
    public async Task SetEnabledAsync_AllowsEnable_WhenAnotherEnabledBotUsesDifferentSymbol()
    {
        await using var context = CreateContext();
        _ = await SeedPilotBotAsync(context, ownerUserId: "user-bot-3", isEnabled: true, symbol: "BTCUSDT");
        var secondBot = await SeedPilotBotAsync(context, ownerUserId: "user-bot-3", isEnabled: false, symbol: "ETHUSDT");
        var service = new BotPilotControlService(context, new FakeTimeProvider(), Options.Create(CreatePilotOptions()));

        var result = await service.SetEnabledAsync(
            secondBot.OwnerUserId,
            secondBot.Id,
            isEnabled: true,
            actor: $"user:{secondBot.OwnerUserId}");

        Assert.True(result.IsSuccessful);
    }

    [Fact]
    public async Task SetEnabledAsync_BlocksEnable_WhenNoPublishedStrategyExists()
    {
        await using var context = CreateContext();
        var bot = await SeedPilotBotAsync(context, isEnabled: false, publishStrategy: false);
        var service = new BotPilotControlService(context, new FakeTimeProvider(), Options.Create(CreatePilotOptions()));

        var result = await service.SetEnabledAsync(
            bot.OwnerUserId,
            bot.Id,
            isEnabled: true,
            actor: $"user:{bot.OwnerUserId}");

        Assert.False(result.IsSuccessful);
        Assert.Equal("PublishedStrategyVersionMissing", result.FailureCode);
    }

    [Fact]
    public async Task SetEnabledAsync_ResetsExistingSchedulerState_ForImmediateNextCycle()
    {
        await using var context = CreateContext();
        var bot = await SeedPilotBotAsync(context, isEnabled: false);
        var now = new DateTimeOffset(2026, 4, 2, 12, 30, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        context.BackgroundJobStates.Add(new BackgroundJobState
        {
            JobKey = $"bot-execution:{bot.Id:N}",
            JobType = BackgroundJobTypes.BotExecution,
            BotId = bot.Id,
            Status = BackgroundJobStatus.Failed,
            NextRunAtUtc = now.UtcDateTime.AddMinutes(5),
            LastErrorCode = "StaleMarketData",
            IdempotencyKey = "retry-key"
        });
        await context.SaveChangesAsync();
        var service = new BotPilotControlService(context, timeProvider, Options.Create(CreatePilotOptions()));

        var result = await service.SetEnabledAsync(
            bot.OwnerUserId,
            bot.Id,
            isEnabled: true,
            actor: $"user:{bot.OwnerUserId}");

        var state = await context.BackgroundJobStates.SingleAsync(entity => entity.BotId == bot.Id);

        Assert.True(result.IsSuccessful);
        Assert.Equal(BackgroundJobStatus.Pending, state.Status);
        Assert.Equal(now.UtcDateTime, state.NextRunAtUtc);
        Assert.Null(state.LastErrorCode);
        Assert.Null(state.IdempotencyKey);
    }

    private static async Task<TradingBot> SeedPilotBotAsync(
        ApplicationDbContext context,
        string ownerUserId = "user-bot-pilot",
        bool isEnabled = false,
        bool publishStrategy = true,
        string symbol = "BTCUSDT")
    {
        var strategy = new TradingStrategy
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            StrategyKey = $"pilot-{ownerUserId}",
            DisplayName = "Pilot Strategy"
        };

        context.TradingStrategies.Add(strategy);

        if (publishStrategy)
        {
            context.TradingStrategyVersions.Add(new TradingStrategyVersion
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                TradingStrategyId = strategy.Id,
                VersionNumber = 1,
                SchemaVersion = 1,
                Status = StrategyVersionStatus.Published,
                DefinitionJson = "{}",
                PublishedAtUtc = DateTime.UtcNow
            });
        }

        var exchangeAccountId = Guid.NewGuid();
        context.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = ownerUserId,
            ExchangeName = "Binance",
            DisplayName = "Pilot Futures",
            IsReadOnly = false,
            CredentialStatus = ExchangeCredentialStatus.Active
        });

        var bot = new TradingBot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = "Pilot Bot",
            StrategyKey = strategy.StrategyKey,
            Symbol = symbol,
            ExchangeAccountId = exchangeAccountId,
            IsEnabled = isEnabled
        };

        context.TradingBots.Add(bot);
        await context.SaveChangesAsync();

        return bot;
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeTimeProvider(DateTimeOffset? utcNow = null) : TimeProvider
    {
        private DateTimeOffset current = utcNow ?? new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            return current;
        }
    }

    private static BotExecutionPilotOptions CreatePilotOptions()
    {
        return new BotExecutionPilotOptions
        {
            Enabled = true,
            DefaultSymbol = "BTCUSDT",
            AllowedSymbols = ["BTCUSDT", "ETHUSDT", "SOLUSDT"]
        };
    }
}
