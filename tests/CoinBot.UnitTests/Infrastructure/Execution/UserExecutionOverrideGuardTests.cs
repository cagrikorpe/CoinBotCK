using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Risk;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Execution;

public sealed class UserExecutionOverrideGuardTests
{
    [Fact]
    public async Task EvaluateAsync_BlocksWhenGlobalPolicyBlocks_RegardlessOfUserOverride()
    {
        await using var dbContext = CreateDbContext();
        dbContext.UserExecutionOverrides.Add(new UserExecutionOverride
        {
            UserId = "user-01",
            AllowedSymbolsCsv = "BTCUSDT",
            DeniedSymbolsCsv = string.Empty,
            MaxOrderSize = 1_000_000m,
            MaxDailyTrades = 20,
            ReduceOnly = false,
            SessionDisabled = false
        });
        await dbContext.SaveChangesAsync();

        var guard = new UserExecutionOverrideGuard(
            dbContext,
            new FakeTradingModeResolver(),
            new FakeGlobalPolicyEngine(
                new GlobalPolicyEvaluationResult(
                    true,
                    "GlobalPolicyObserveOnly",
                    "Global policy is in ObserveOnly mode and execution is blocked.",
                    9,
                    null,
                    AutonomyPolicyMode.ObserveOnly)),
            NullLogger<UserExecutionOverrideGuard>.Instance);

        var result = await guard.EvaluateAsync(
            new UserExecutionOverrideEvaluationRequest(
                "user-01",
                "BTCUSDT",
                ExecutionEnvironment.Demo,
                ExecutionOrderSide.Buy,
                1m,
                100m,
                BotId: null,
                StrategyKey: "core"),
            CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Equal("GlobalPolicyObserveOnly", result.BlockCode);
        Assert.Contains("ObserveOnly", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_AllowsDevelopmentFuturesPilotOverride_WhenResolvedModeRemainsDemo()
    {
        await using var dbContext = CreateDbContext();
        var guard = new UserExecutionOverrideGuard(
            dbContext,
            new FakeTradingModeResolver(),
            logger: NullLogger<UserExecutionOverrideGuard>.Instance,
            hostEnvironment: new TestHostEnvironment(Environments.Development));

        var result = await guard.EvaluateAsync(
            new UserExecutionOverrideEvaluationRequest(
                "user-pilot",
                "BTCUSDT",
                ExecutionEnvironment.Live,
                ExecutionOrderSide.Buy,
                0.002m,
                65000m,
                BotId: Guid.NewGuid(),
                StrategyKey: "pilot-core",
                Context: "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1"),
            CancellationToken.None);

        Assert.False(result.IsBlocked);
        Assert.Null(result.BlockCode);
    }

    [Fact]
    public async Task EvaluateAsync_BlocksWhenBotCooldownIsActive()
    {
        await using var dbContext = CreateDbContext();
        var botId = Guid.NewGuid();
        dbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-cooldown",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            StrategyKey = "cooldown-core",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.01m,
            Price = 65000m,
            BotId = botId,
            ExecutionEnvironment = ExecutionEnvironment.Demo,
            ExecutorKind = ExecutionOrderExecutorKind.Virtual,
            State = ExecutionOrderState.Submitted,
            CooldownApplied = true,
            IdempotencyKey = "cooldown-order",
            RootCorrelationId = "cooldown-root",
            CreatedDate = DateTime.UtcNow.AddSeconds(-30),
            LastStateChangedAtUtc = DateTime.UtcNow.AddSeconds(-30)
        });
        await dbContext.SaveChangesAsync();

        var guard = new UserExecutionOverrideGuard(
            dbContext,
            new FakeTradingModeResolver(),
            logger: NullLogger<UserExecutionOverrideGuard>.Instance,
            botExecutionPilotOptions: Options.Create(new BotExecutionPilotOptions
            {
                PerBotCooldownSeconds = 120
            }));

        var result = await guard.EvaluateAsync(
            new UserExecutionOverrideEvaluationRequest(
                "user-cooldown",
                "BTCUSDT",
                ExecutionEnvironment.Demo,
                ExecutionOrderSide.Buy,
                0.01m,
                65000m,
                BotId: botId,
                StrategyKey: "cooldown-core"),
            CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Equal("UserExecutionBotCooldownActive", result.BlockCode);
    }

    [Fact]
    public async Task EvaluateAsync_BlocksWhenSymbolCooldownIsActive()
    {
        await using var dbContext = CreateDbContext();
        dbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-symbol-cooldown",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            StrategyKey = "cooldown-core",
            Symbol = "ETHUSDT",
            Timeframe = "1m",
            BaseAsset = "ETH",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.1m,
            Price = 3000m,
            ExecutionEnvironment = ExecutionEnvironment.Demo,
            ExecutorKind = ExecutionOrderExecutorKind.Virtual,
            State = ExecutionOrderState.Submitted,
            CooldownApplied = true,
            IdempotencyKey = "symbol-cooldown-order",
            RootCorrelationId = "symbol-cooldown-root",
            CreatedDate = DateTime.UtcNow.AddSeconds(-30),
            LastStateChangedAtUtc = DateTime.UtcNow.AddSeconds(-30)
        });
        await dbContext.SaveChangesAsync();

        var guard = new UserExecutionOverrideGuard(
            dbContext,
            new FakeTradingModeResolver(),
            logger: NullLogger<UserExecutionOverrideGuard>.Instance,
            botExecutionPilotOptions: Options.Create(new BotExecutionPilotOptions
            {
                PerSymbolCooldownSeconds = 60
            }));

        var result = await guard.EvaluateAsync(
            new UserExecutionOverrideEvaluationRequest(
                "user-symbol-cooldown",
                "ETHUSDT",
                ExecutionEnvironment.Demo,
                ExecutionOrderSide.Buy,
                0.1m,
                3000m,
                StrategyKey: "cooldown-core"),
            CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Equal("UserExecutionSymbolCooldownActive", result.BlockCode);
    }

    [Fact]
    public async Task EvaluateAsync_AllowsWhenBotCooldownHasExpired()
    {
        await using var dbContext = CreateDbContext();
        var botId = Guid.NewGuid();
        var existingOrder = new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-cooldown-expired",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            StrategyKey = "cooldown-core",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.01m,
            Price = 65000m,
            BotId = botId,
            ExecutionEnvironment = ExecutionEnvironment.Demo,
            ExecutorKind = ExecutionOrderExecutorKind.Virtual,
            State = ExecutionOrderState.Submitted,
            IdempotencyKey = "cooldown-expired-order",
            RootCorrelationId = "cooldown-expired-root",
            CreatedDate = DateTime.UtcNow,
            LastStateChangedAtUtc = DateTime.UtcNow
        };
        dbContext.ExecutionOrders.Add(existingOrder);
        await dbContext.SaveChangesAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        var guard = new UserExecutionOverrideGuard(
            dbContext,
            new FakeTradingModeResolver(),
            logger: NullLogger<UserExecutionOverrideGuard>.Instance,
            botExecutionPilotOptions: Options.Create(new BotExecutionPilotOptions
            {
                PerBotCooldownSeconds = 1
            }));

        var result = await guard.EvaluateAsync(
            new UserExecutionOverrideEvaluationRequest(
                "user-cooldown-expired",
                "BTCUSDT",
                ExecutionEnvironment.Demo,
                ExecutionOrderSide.Buy,
                0.01m,
                65000m,
                BotId: botId,
                StrategyKey: "cooldown-core"),
            CancellationToken.None);

        Assert.False(result.IsBlocked);
        Assert.Null(result.BlockCode);
    }

    [Fact]
    public async Task EvaluateAsync_BlocksWhenMaxOpenPositionsLimitIsReached()
    {
        await using var dbContext = CreateDbContext();
        dbContext.DemoPositions.AddRange(
            new DemoPosition
            {
                Id = Guid.NewGuid(),
                OwnerUserId = "user-positions",
                PositionScopeKey = "scope-btc",
                Symbol = "BTCUSDT",
                BaseAsset = "BTC",
                QuoteAsset = "USDT",
                PositionKind = DemoPositionKind.Futures,
                MarginMode = DemoMarginMode.Isolated,
                Leverage = 1m,
                Quantity = 0.1m,
                CostBasis = 6500m,
                AverageEntryPrice = 65000m,
                UnrealizedPnl = 10m,
                LastMarkPrice = 65100m,
                MarginBalance = 110m
            },
            new DemoPosition
            {
                Id = Guid.NewGuid(),
                OwnerUserId = "user-positions",
                PositionScopeKey = "scope-eth",
                Symbol = "ETHUSDT",
                BaseAsset = "ETH",
                QuoteAsset = "USDT",
                PositionKind = DemoPositionKind.Futures,
                MarginMode = DemoMarginMode.Isolated,
                Leverage = 1m,
                Quantity = 1m,
                CostBasis = 3000m,
                AverageEntryPrice = 3000m,
                UnrealizedPnl = 15m,
                LastMarkPrice = 3015m,
                MarginBalance = 95m
            });
        await dbContext.SaveChangesAsync();

        var guard = new UserExecutionOverrideGuard(
            dbContext,
            new FakeTradingModeResolver(),
            logger: NullLogger<UserExecutionOverrideGuard>.Instance,
            botExecutionPilotOptions: Options.Create(new BotExecutionPilotOptions
            {
                MaxOpenPositionsPerUser = 2
            }));

        var result = await guard.EvaluateAsync(
            new UserExecutionOverrideEvaluationRequest(
                "user-positions",
                "SOLUSDT",
                ExecutionEnvironment.Demo,
                ExecutionOrderSide.Buy,
                1m,
                100m,
                StrategyKey: "positions-core"),
            CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Equal("UserExecutionMaxOpenPositionsExceeded", result.BlockCode);
    }

    [Fact]
    public async Task EvaluateAsync_BlocksWhenRiskPolicyVetoesDailyLoss()
    {
        await using var dbContext = CreateDbContext();
        dbContext.RiskProfiles.Add(new RiskProfile
        {
            OwnerUserId = "user-risk-block",
            ProfileName = "Pilot",
            MaxDailyLossPercentage = 5m,
            MaxPositionSizePercentage = 100m,
            MaxLeverage = 2m
        });
        dbContext.DemoWallets.Add(new DemoWallet
        {
            OwnerUserId = "user-risk-block",
            Asset = "USDT",
            AvailableBalance = 10000m,
            ReservedBalance = 0m,
            LastActivityAtUtc = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc)
        });
        dbContext.DemoLedgerTransactions.Add(new DemoLedgerTransaction
        {
            OwnerUserId = "user-risk-block",
            OperationId = Guid.NewGuid().ToString("N"),
            TransactionType = DemoLedgerTransactionType.FillApplied,
            PositionScopeKey = "risk-position",
            Symbol = "BTCUSDT",
            QuoteAsset = "USDT",
            RealizedPnlDelta = -750m,
            OccurredAtUtc = new DateTime(2026, 3, 22, 11, 0, 0, DateTimeKind.Utc)
        });
        await dbContext.SaveChangesAsync();

        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        var guard = new UserExecutionOverrideGuard(
            dbContext,
            new FakeTradingModeResolver(),
            logger: NullLogger<UserExecutionOverrideGuard>.Instance,
            riskPolicyEvaluator: new RiskPolicyEvaluator(
                dbContext,
                timeProvider,
                NullLogger<RiskPolicyEvaluator>.Instance),
            botExecutionPilotOptions: Options.Create(new BotExecutionPilotOptions()));

        var result = await guard.EvaluateAsync(
            new UserExecutionOverrideEvaluationRequest(
                "user-risk-block",
                "BTCUSDT",
                ExecutionEnvironment.Demo,
                ExecutionOrderSide.Buy,
                0.01m,
                65000m,
                StrategyKey: "risk-core",
                TradingStrategyId: Guid.NewGuid(),
                TradingStrategyVersionId: Guid.NewGuid(),
                Timeframe: "1m"),
            CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Equal("UserExecutionRiskDailyLossLimitBreached", result.BlockCode);
        Assert.Contains("Reason=DailyLossLimitBreached", result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.RiskEvaluation);
        Assert.Equal(RiskVetoReasonCode.DailyLossLimitBreached, result.RiskEvaluation!.ReasonCode);
        Assert.Equal(7.5m, result.RiskEvaluation.Snapshot.CurrentDailyLossPercentage);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private sealed class FakeTradingModeResolver : ITradingModeResolver
    {
        public Task<TradingModeResolution> ResolveAsync(TradingModeResolutionRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TradingModeResolution(
                ExecutionEnvironment.Demo,
                null,
                null,
                null,
                ExecutionEnvironment.Demo,
                TradingModeResolutionSource.GlobalDefault,
                "Demo",
                false));
        }
    }

    private sealed class FakeGlobalPolicyEngine : IGlobalPolicyEngine
    {
        private readonly GlobalPolicyEvaluationResult evaluationResult;

        public FakeGlobalPolicyEngine(GlobalPolicyEvaluationResult evaluationResult)
        {
            this.evaluationResult = evaluationResult;
        }

        public Task<GlobalPolicySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(GlobalPolicySnapshot.CreateDefault(DateTime.UtcNow));
        }

        public Task<GlobalPolicyEvaluationResult> EvaluateAsync(GlobalPolicyEvaluationRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(evaluationResult);
        }

        public Task<GlobalPolicySnapshot> UpdateAsync(GlobalPolicyUpdateRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GlobalPolicySnapshot> RollbackAsync(GlobalPolicyRollbackRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class TestDataScopeContext : CoinBot.Application.Abstractions.DataScope.IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "CoinBot.UnitTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
