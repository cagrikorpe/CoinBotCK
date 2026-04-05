using CoinBot.Application.Abstractions.Bots;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Mfa;
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
        var executionOrderId = Guid.NewGuid();
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
            Id = executionOrderId,
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
            FailureCode = "InvalidOperationException",
            FailureDetail = "Cache entry must specify a value for Size when SizeLimit is set.",
            RejectionStage = ExecutionRejectionStage.PreSubmit,
            SubmittedToBroker = false,
            RetryEligible = false,
            CooldownApplied = true,
            ReduceOnly = true,
            StopLossPrice = 59000m,
            DuplicateSuppressed = true,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            CreatedDate = DateTime.UtcNow.AddSeconds(-30),
            UpdatedDate = DateTime.UtcNow.AddSeconds(-29),
            LastStateChangedAtUtc = DateTime.UtcNow.AddSeconds(-29)
        });
        context.ExecutionOrderTransitions.Add(new ExecutionOrderTransition
        {
            Id = Guid.NewGuid(),
            OwnerUserId = bot.OwnerUserId,
            ExecutionOrderId = executionOrderId,
            SequenceNumber = 4,
            State = ExecutionOrderState.Rejected,
            EventCode = "UserExecutionOverrideBlocked",
            Detail = "Execution blocked because the bot cooldown is still active.\r\n",
            CorrelationId = "corr-user-page",
            OccurredAtUtc = DateTime.UtcNow.AddSeconds(-29)
        });
        await context.SaveChangesAsync();
        var persistedOrder = await context.ExecutionOrders
            .AsNoTracking()
            .SingleAsync(entity => entity.Id == executionOrderId);
        var utcNow = DateTime.SpecifyKind(
            persistedOrder.CreatedDate.AddSeconds(30),
            DateTimeKind.Utc);
        var service = CreateService(context, new FakeTimeProvider(new DateTimeOffset(utcNow)));

        var snapshot = await service.GetPageAsync("user-page");
        var row = Assert.Single(snapshot.Bots);

        Assert.Equal(bot.Id, row.BotId);
        Assert.Equal("RetryPending", row.LastJobStatus);
        Assert.Equal("ReferencePriceUnavailable", row.LastJobErrorCode);
        Assert.Equal("Rejected", row.LastExecutionState);
        Assert.Equal("InvalidOperationException", row.LastExecutionFailureCode);
        Assert.Equal("Execution blocked because the bot cooldown is still active.", row.LastExecutionBlockDetail);
        Assert.Equal("PreSubmit", row.LastExecutionRejectionStage);
        Assert.False(row.LastExecutionSubmittedToBroker);
        Assert.False(row.LastExecutionRetryEligible);
        Assert.True(row.LastExecutionCooldownApplied);
        Assert.True(row.LastExecutionReduceOnly);
        Assert.True(row.LastExecutionStopLossAttached);
        Assert.False(row.LastExecutionTakeProfitAttached);
        Assert.True(row.LastExecutionDuplicateSuppressed);
        Assert.Equal("UserExecutionOverrideBlocked", row.LastExecutionTransitionCode);
        Assert.Equal("corr-user-page", row.LastExecutionTransitionCorrelationId);
        Assert.Equal(DateTime.SpecifyKind(persistedOrder.CreatedDate.AddSeconds(120), DateTimeKind.Utc), row.CooldownBlockedUntilUtc);
        Assert.Equal(90, row.CooldownRemainingSeconds);
    }

    [Fact]
    public async Task GetPageAsync_ProjectsSanitizedMarketDataDiagnostics_FromLatestTransitionDetail()
    {
        await using var context = CreateContext();
        var exchangeAccountId = await SeedStrategyAndExchangeAccountAsync(context, "user-market-diag", "pilot-market-diag");
        var bot = await SeedBotAsync(context, "user-market-diag", exchangeAccountId, isEnabled: true);
        var executionOrderId = Guid.NewGuid();
        var orderCreatedAtUtc = new DateTime(2026, 4, 2, 23, 52, 20, DateTimeKind.Utc);

        context.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = executionOrderId,
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
            FailureCode = "ClockDriftExceeded",
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            CreatedDate = orderCreatedAtUtc,
            UpdatedDate = orderCreatedAtUtc.AddSeconds(1),
            LastStateChangedAtUtc = orderCreatedAtUtc.AddSeconds(1)
        });
        context.ExecutionOrderTransitions.Add(new ExecutionOrderTransition
        {
            Id = Guid.NewGuid(),
            OwnerUserId = bot.OwnerUserId,
            ExecutionOrderId = executionOrderId,
            SequenceNumber = 2,
            State = ExecutionOrderState.Rejected,
            EventCode = "GateRejected",
            Detail = "Execution blocked because clock drift exceeded the safety threshold. LatencyReason=ClockDriftExceeded; HeartbeatSource=binance:rest-backfill; Symbol=BTCUSDT; Timeframe=1m; LastCandleAtUtc=2026-04-02T23:51:59.9990000Z; ExpectedOpenTimeUtc=2026-04-02T23:52:00.0000000Z; DataAgeMs=20265; ClockDriftMs=19724; ContinuityGapCount=0; DecisionSourceLayer=heartbeat-watchdog; DecisionMethodName=ExecutionGate.EvaluateDataLatencyAsync.",
            CorrelationId = "corr-market-diag",
            OccurredAtUtc = orderCreatedAtUtc.AddSeconds(1),
            CreatedDate = orderCreatedAtUtc.AddSeconds(1),
            UpdatedDate = orderCreatedAtUtc.AddSeconds(1)
        });
        context.DegradedModeStates.Add(new DegradedModeState
        {
            Id = DegradedModeDefaults.ResolveStateId("BTCUSDT", "1m"),
            StateCode = DegradedModeStateCode.Normal,
            ReasonCode = DegradedModeReasonCode.None,
            SignalFlowBlocked = false,
            ExecutionFlowBlocked = false,
            LatestSymbol = "BTCUSDT",
            LatestTimeframe = "1m",
            LatestDataTimestampAtUtc = new DateTime(2026, 4, 2, 23, 51, 59, 999, DateTimeKind.Utc),
            LatestExpectedOpenTimeUtc = new DateTime(2026, 4, 2, 23, 52, 0, DateTimeKind.Utc),
            LatestContinuityGapCount = 0,
            LatestContinuityGapStartedAtUtc = new DateTime(2026, 4, 2, 23, 45, 0, DateTimeKind.Utc),
            LatestContinuityGapLastSeenAtUtc = new DateTime(2026, 4, 2, 23, 50, 0, DateTimeKind.Utc),
            LatestContinuityRecoveredAtUtc = new DateTime(2026, 4, 2, 23, 50, 30, DateTimeKind.Utc)
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, new FakeTimeProvider(new DateTimeOffset(orderCreatedAtUtc.AddMinutes(5))));

        var snapshot = await service.GetPageAsync("user-market-diag");
        var row = Assert.Single(snapshot.Bots);

        Assert.Equal("Execution blocked because clock drift exceeded the safety threshold.", row.LastExecutionBlockDetail);
        Assert.Equal(new DateTime(2026, 4, 2, 23, 51, 59, 999, DateTimeKind.Utc), row.LastExecutionLastCandleAtUtc);
        Assert.Equal(20265, row.LastExecutionDataAgeMilliseconds);
        Assert.Equal("Continuity OK", row.LastExecutionContinuityState);
        Assert.Equal(0, row.LastExecutionContinuityGapCount);
        Assert.Equal("Clock drift exceeded", row.LastExecutionStaleReason);
        Assert.Equal("BTCUSDT", row.LastExecutionAffectedSymbol);
        Assert.Equal("1m", row.LastExecutionAffectedTimeframe);
        Assert.Equal("Block", row.LastExecutionDecisionOutcome);
        Assert.Equal("StaleData", row.LastExecutionDecisionReasonType);
        Assert.Equal("ClockDriftExceeded", row.LastExecutionDecisionReasonCode);
        Assert.Equal("Execution blocked because clock drift exceeded the safety threshold.", row.LastExecutionDecisionSummary);
        Assert.Equal(3000, row.LastExecutionStaleThresholdMilliseconds);
        Assert.Equal(new DateTime(2026, 4, 2, 23, 45, 0, DateTimeKind.Utc), row.LastExecutionContinuityGapStartedAtUtc);
        Assert.Equal(new DateTime(2026, 4, 2, 23, 50, 0, DateTimeKind.Utc), row.LastExecutionContinuityGapLastSeenAtUtc);
        Assert.Equal(new DateTime(2026, 4, 2, 23, 50, 30, DateTimeKind.Utc), row.LastExecutionContinuityRecoveredAtUtc);
    }

    [Fact]
    public async Task GetPageAsync_RejectsRequestedOwnerOutsideCurrentScope()
    {
        await using var context = CreateContext(currentUserId: "user-scope-a", hasIsolationBypass: false);
        var service = CreateService(context);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetPageAsync("user-scope-b"));

        Assert.Contains("outside the authenticated isolation boundary", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_FailsClosed_WhenCriticalOperationAuthorizerRejectsRequest()
    {
        await using var context = CreateContext();
        var ownerUserId = "user-bot-mfa";
        var exchangeAccountId = await SeedStrategyAndExchangeAccountAsync(context, ownerUserId, "pilot-mfa");
        var service = CreateService(
            context,
            criticalUserOperationAuthorizer: new FakeCriticalUserOperationAuthorizer
            {
                Result = new CriticalUserOperationAuthorizationResult(
                    false,
                    "MfaRequired",
                    "Bu islem icin MFA zorunludur.")
            });

        var result = await service.CreateAsync(
            ownerUserId,
            new BotManagementSaveCommand(
                "Pilot Mfa",
                "pilot-mfa",
                "BTCUSDT",
                0.001m,
                exchangeAccountId,
                1m,
                "ISOLATED",
                false),
            $"user:{ownerUserId}");

        Assert.False(result.IsSuccessful);
        Assert.Equal("MfaRequired", result.FailureCode);
        Assert.Empty(context.TradingBots.Where(entity => entity.OwnerUserId == ownerUserId));
    }

    private static BotManagementService CreateService(
        ApplicationDbContext context,
        TimeProvider? timeProvider = null,
        ICriticalUserOperationAuthorizer? criticalUserOperationAuthorizer = null)
    {
        return new BotManagementService(
            context,
            new BotPilotControlService(
                context,
                criticalUserOperationAuthorizer ?? new FakeCriticalUserOperationAuthorizer(),
                timeProvider ?? new FakeTimeProvider(),
                Options.Create(CreatePilotOptions())),
            criticalUserOperationAuthorizer ?? new FakeCriticalUserOperationAuthorizer(),
            timeProvider ?? new FakeTimeProvider(),
            Options.Create(CreatePilotOptions()),
            Options.Create(new DataLatencyGuardOptions()));
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

    private static ApplicationDbContext CreateContext(string? currentUserId = null, bool hasIsolationBypass = true)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext(currentUserId, hasIsolationBypass));
    }

    private sealed class TestDataScopeContext(string? userId, bool hasIsolationBypass) : IDataScopeContext
    {
        public string? UserId => userId;

        public bool HasIsolationBypass => hasIsolationBypass;
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
            DefaultMarginType = "ISOLATED",
            PerBotCooldownSeconds = 120,
            PerSymbolCooldownSeconds = 60
        };
    }

    private sealed class FakeCriticalUserOperationAuthorizer : ICriticalUserOperationAuthorizer
    {
        public CriticalUserOperationAuthorizationResult Result { get; set; } =
            new(true, null, null);

        public Task<CriticalUserOperationAuthorizationResult> AuthorizeAsync(
            CriticalUserOperationAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result);
        }
    }
}

