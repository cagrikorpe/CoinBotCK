using CoinBot.Application.Abstractions.Bots;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Jobs;

public sealed class BotManagementServiceTests
{
    [Fact]
    public async Task CreateAsync_PersistsPilotFields_AndStrategyAssignment()
    {
        await using var context = CreateContext();
        var ownerUserId = "user-bot-create";
        var exchangeAccountId = await SeedStrategyAndExchangeAccountAsync(context, ownerUserId, "pilot-create");
        var service = CreateService(context);

        var result = await service.CreateAsync(
            ownerUserId,
            new BotManagementSaveCommand(
                "Pilot Create",
                "pilot-create",
                "ETHUSDT",
                0.001m,
                exchangeAccountId,
                1m,
                "ISOLATED",
                false),
            $"user:{ownerUserId}");

        var bot = await context.TradingBots.SingleAsync();

        Assert.True(result.IsSuccessful);
        Assert.Equal("Pilot Create", bot.Name);
        Assert.Equal("pilot-create", bot.StrategyKey);
        Assert.Equal("ETHUSDT", bot.Symbol);
        Assert.Equal(0.001m, bot.Quantity);
        Assert.Equal(exchangeAccountId, bot.ExchangeAccountId);
        Assert.Equal(1m, bot.Leverage);
        Assert.Equal("ISOLATED", bot.MarginType);
        Assert.False(bot.IsEnabled);
    }

    [Fact]
    public async Task UpdateAsync_PersistsEditedFields_AndResetsSchedulerState_WhenBotRemainsEnabled()
    {
        await using var context = CreateContext();
        var ownerUserId = "user-bot-update";
        var exchangeAccountId = await SeedStrategyAndExchangeAccountAsync(context, ownerUserId, "pilot-update");
        var bot = await SeedBotAsync(context, ownerUserId, exchangeAccountId, isEnabled: true);
        var now = new DateTimeOffset(2026, 4, 2, 13, 0, 0, TimeSpan.Zero);
        context.BackgroundJobStates.Add(new BackgroundJobState
        {
            JobKey = $"bot-execution:{bot.Id:N}",
            JobType = BackgroundJobTypes.BotExecution,
            BotId = bot.Id,
            Status = BackgroundJobStatus.Failed,
            NextRunAtUtc = now.UtcDateTime.AddMinutes(10),
            LastErrorCode = "StrategyFailed",
            IdempotencyKey = "retry-key"
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, new FakeTimeProvider(now));

        var result = await service.UpdateAsync(
            ownerUserId,
            bot.Id,
            new BotManagementSaveCommand(
                "Pilot Update 2",
                "pilot-update",
                "BTCUSDT",
                0.002m,
                exchangeAccountId,
                1m,
                "ISOLATED",
                true),
            $"user:{ownerUserId}");

        var persistedBot = await context.TradingBots.SingleAsync(entity => entity.Id == bot.Id);
        var state = await context.BackgroundJobStates.SingleAsync(entity => entity.BotId == bot.Id);

        Assert.True(result.IsSuccessful);
        Assert.True(persistedBot.IsEnabled);
        Assert.Equal("Pilot Update 2", persistedBot.Name);
        Assert.Equal(0.002m, persistedBot.Quantity);
        Assert.Equal(BackgroundJobStatus.Pending, state.Status);
        Assert.Equal(now.UtcDateTime, state.NextRunAtUtc);
        Assert.Null(state.LastErrorCode);
        Assert.Null(state.IdempotencyKey);
    }

    [Fact]
    public async Task UpdateAsync_ReturnsNotFound_WhenBotBelongsToAnotherUser()
    {
        await using var context = CreateContext();
        var exchangeAccountId = await SeedStrategyAndExchangeAccountAsync(context, "owner-a", "pilot-owner");
        var bot = await SeedBotAsync(context, "owner-a", exchangeAccountId, isEnabled: false);
        var service = CreateService(context);

        var result = await service.UpdateAsync(
            "owner-b",
            bot.Id,
            new BotManagementSaveCommand(
                "Pilot",
                "pilot-owner",
                "BTCUSDT",
                null,
                exchangeAccountId,
                1m,
                "ISOLATED",
                false),
            "user:owner-b");

        Assert.False(result.IsSuccessful);
        Assert.Equal("BotNotFound", result.FailureCode);
    }

    [Fact]
    public async Task GetPageAsync_ReturnsOwnedBots_WithOperationalState()
    {
        await using var context = CreateContext();
        var exchangeAccountId = await SeedStrategyAndExchangeAccountAsync(context, "user-page", "pilot-page");
        var bot = await SeedBotAsync(context, "user-page", exchangeAccountId, isEnabled: true);
        context.BackgroundJobStates.Add(new BackgroundJobState
        {
            JobKey = $"bot-execution:{bot.Id:N}",
            JobType = BackgroundJobTypes.BotExecution,
            BotId = bot.Id,
            Status = BackgroundJobStatus.RetryPending,
            NextRunAtUtc = DateTime.UtcNow.AddMinutes(1),
            LastErrorCode = "ReferencePriceUnavailable"
        });
        context.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = bot.OwnerUserId,
            BotId = bot.Id,
            StrategyKey = bot.StrategyKey,
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.001m,
            Price = 60000m,
            State = ExecutionOrderState.Rejected,
            FailureCode = "TradeMasterDisarmed",
            FailureDetail = "Execution blocked because TradeMaster is disarmed.",
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            CreatedDate = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var snapshot = await service.GetPageAsync("user-page");
        var row = Assert.Single(snapshot.Bots);

        Assert.Equal(bot.Id, row.BotId);
        Assert.Equal("RetryPending", row.LastJobStatus);
        Assert.Equal("ReferencePriceUnavailable", row.LastJobErrorCode);
        Assert.Equal("Rejected", row.LastExecutionState);
        Assert.Equal("TradeMasterDisarmed", row.LastExecutionFailureCode);
    }

    private static BotManagementService CreateService(ApplicationDbContext context, TimeProvider? timeProvider = null)
    {
        return new BotManagementService(
            context,
            new BotPilotControlService(
                context,
                timeProvider ?? new FakeTimeProvider(),
                Options.Create(CreatePilotOptions())),
            timeProvider ?? new FakeTimeProvider(),
            Options.Create(CreatePilotOptions()));
    }

    private static async Task<Guid> SeedStrategyAndExchangeAccountAsync(ApplicationDbContext context, string ownerUserId, string strategyKey)
    {
        var strategy = new TradingStrategy
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            StrategyKey = strategyKey,
            DisplayName = $"{strategyKey}-display"
        };
        var exchangeAccountId = Guid.NewGuid();

        context.TradingStrategies.Add(strategy);
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
        context.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = ownerUserId,
            ExchangeName = "Binance",
            DisplayName = "Pilot Futures",
            IsReadOnly = false,
            CredentialStatus = ExchangeCredentialStatus.Active
        });
        await context.SaveChangesAsync();

        return exchangeAccountId;
    }

    private static async Task<TradingBot> SeedBotAsync(ApplicationDbContext context, string ownerUserId, Guid exchangeAccountId, bool isEnabled)
    {
        var bot = new TradingBot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = "Pilot Bot",
            StrategyKey = context.TradingStrategies.Single(entity => entity.OwnerUserId == ownerUserId).StrategyKey,
            Symbol = "BTCUSDT",
            ExchangeAccountId = exchangeAccountId,
            Leverage = 1m,
            MarginType = "ISOLATED",
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
        private readonly DateTimeOffset current = utcNow ?? new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero);

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
            AllowedSymbols = ["BTCUSDT", "ETHUSDT", "SOLUSDT"],
            DefaultLeverage = 1m,
            DefaultMarginType = "ISOLATED"
        };
    }
}
