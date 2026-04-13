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
        var botId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        var evaluatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        dbContext.RiskProfiles.Add(new RiskProfile
        {
            OwnerUserId = "user-pilot",
            ProfileName = "Pilot",
            MaxDailyLossPercentage = 5m,
            MaxPositionSizePercentage = 100m,
            MaxLeverage = 2m,
            MaxConcurrentPositions = 1
        });
        dbContext.ExchangeBalances.Add(new ExchangeBalance
        {
            ExchangeAccountId = exchangeAccountId,
            OwnerUserId = "user-pilot",
            Plane = ExchangeDataPlane.Futures,
            Asset = "USDT",
            WalletBalance = 1000m,
            CrossWalletBalance = 1000m,
            AvailableBalance = 1000m,
            MaxWithdrawAmount = 1000m,
            ExchangeUpdatedAtUtc = evaluatedAtUtc
        });
        await dbContext.SaveChangesAsync();

        var guard = new UserExecutionOverrideGuard(
            dbContext,
            new FakeTradingModeResolver(),
            logger: NullLogger<UserExecutionOverrideGuard>.Instance,
            hostEnvironment: new TestHostEnvironment(Environments.Development),
            riskPolicyEvaluator: new RiskPolicyEvaluator(
                dbContext,
                timeProvider,
                NullLogger<RiskPolicyEvaluator>.Instance),
            botExecutionPilotOptions: Options.Create(new BotExecutionPilotOptions
            {
                Enabled = true,
                AllowedUserIds = ["user-pilot"],
                AllowedBotIds = [botId.ToString("N")],
                AllowedSymbols = ["BTCUSDT"],
                MaxOpenPositionsPerUser = 1,
                PerBotCooldownSeconds = 300,
                PerSymbolCooldownSeconds = 300,
                MaxOrderNotional = 200m,
                MaxDailyLossPercentage = 5m
            }));

        var result = await guard.EvaluateAsync(
            new UserExecutionOverrideEvaluationRequest(
                "user-pilot",
                "BTCUSDT",
                ExecutionEnvironment.Live,
                ExecutionOrderSide.Buy,
                0.002m,
                65000m,
                BotId: botId,
                StrategyKey: "pilot-core",
                Context: "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                TradingStrategyId: Guid.NewGuid(),
                TradingStrategyVersionId: Guid.NewGuid(),
                Timeframe: "1m"),
            CancellationToken.None);

        Assert.False(result.IsBlocked);
        Assert.Null(result.BlockCode);
        Assert.Contains("PilotUserId=user-pilot", result.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("Plane=Futures", result.GuardSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_AllowsPilotWhenAllowListsAreEmpty()
    {
        await using var dbContext = CreateDbContext();
        var botId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        var evaluatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        dbContext.RiskProfiles.Add(new RiskProfile
        {
            OwnerUserId = "user-open-scope",
            ProfileName = "Pilot",
            MaxDailyLossPercentage = 5m,
            MaxPositionSizePercentage = 100m,
            MaxLeverage = 2m,
            MaxConcurrentPositions = 1
        });
        dbContext.ExchangeBalances.Add(new ExchangeBalance
        {
            ExchangeAccountId = exchangeAccountId,
            OwnerUserId = "user-open-scope",
            Plane = ExchangeDataPlane.Futures,
            Asset = "USDT",
            WalletBalance = 1000m,
            CrossWalletBalance = 1000m,
            AvailableBalance = 1000m,
            MaxWithdrawAmount = 1000m,
            ExchangeUpdatedAtUtc = evaluatedAtUtc
        });
        await dbContext.SaveChangesAsync();

        var guard = new UserExecutionOverrideGuard(
            dbContext,
            new FakeTradingModeResolver(),
            logger: NullLogger<UserExecutionOverrideGuard>.Instance,
            hostEnvironment: new TestHostEnvironment(Environments.Development),
            riskPolicyEvaluator: new RiskPolicyEvaluator(
                dbContext,
                timeProvider,
                NullLogger<RiskPolicyEvaluator>.Instance),
            botExecutionPilotOptions: Options.Create(new BotExecutionPilotOptions
            {
                Enabled = true,
                AllowedUserIds = [],
                AllowedBotIds = [],
                AllowedSymbols = ["BTCUSDT"],
                MaxOpenPositionsPerUser = 1,
                PerBotCooldownSeconds = 300,
                PerSymbolCooldownSeconds = 300,
                MaxOrderNotional = 200m,
                MaxDailyLossPercentage = 5m
            }));

        var result = await guard.EvaluateAsync(
            new UserExecutionOverrideEvaluationRequest(
                "user-open-scope",
                "BTCUSDT",
                ExecutionEnvironment.Live,
                ExecutionOrderSide.Buy,
                0.002m,
                65000m,
                BotId: botId,
                StrategyKey: "pilot-core",
                Context: "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                TradingStrategyId: Guid.NewGuid(),
                TradingStrategyVersionId: Guid.NewGuid(),
                Timeframe: "1m"),
            CancellationToken.None);

        Assert.False(result.IsBlocked);
        Assert.Null(result.BlockCode);
    }

    [Fact]
    public async Task EvaluateAsync_AllowsPilotWhenMultipleAllowedSymbolsContainRequestedSymbol()
    {
        await using var dbContext = CreateDbContext();
        var botId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        var evaluatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        dbContext.RiskProfiles.Add(new RiskProfile
        {
            OwnerUserId = "user-multi-symbol",
            ProfileName = "Pilot",
            MaxDailyLossPercentage = 5m,
            MaxPositionSizePercentage = 100m,
            MaxLeverage = 2m,
            MaxConcurrentPositions = 1
        });
        dbContext.ExchangeBalances.Add(new ExchangeBalance
        {
            ExchangeAccountId = exchangeAccountId,
            OwnerUserId = "user-multi-symbol",
            Plane = ExchangeDataPlane.Futures,
            Asset = "USDT",
            WalletBalance = 1000m,
            CrossWalletBalance = 1000m,
            AvailableBalance = 1000m,
            MaxWithdrawAmount = 1000m,
            ExchangeUpdatedAtUtc = evaluatedAtUtc
        });
        await dbContext.SaveChangesAsync();

        var guard = new UserExecutionOverrideGuard(
            dbContext,
            new FakeTradingModeResolver(),
            logger: NullLogger<UserExecutionOverrideGuard>.Instance,
            hostEnvironment: new TestHostEnvironment(Environments.Development),
            riskPolicyEvaluator: new RiskPolicyEvaluator(
                dbContext,
                timeProvider,
                NullLogger<RiskPolicyEvaluator>.Instance),
            botExecutionPilotOptions: Options.Create(new BotExecutionPilotOptions
            {
                Enabled = true,
                AllowedUserIds = ["user-multi-symbol"],
                AllowedBotIds = [botId.ToString("N")],
                AllowedSymbols = ["BTCUSDT", "ETHUSDT", "SOLUSDT"],
                MaxOpenPositionsPerUser = 1,
                PerBotCooldownSeconds = 300,
                PerSymbolCooldownSeconds = 300,
                MaxOrderNotional = 200m,
                MaxDailyLossPercentage = 5m
            }));

        var result = await guard.EvaluateAsync(
            new UserExecutionOverrideEvaluationRequest(
                "user-multi-symbol",
                "BTCUSDT",
                ExecutionEnvironment.Live,
                ExecutionOrderSide.Buy,
                0.002m,
                65000m,
                BotId: botId,
                StrategyKey: "pilot-core",
                Context: "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                TradingStrategyId: Guid.NewGuid(),
                TradingStrategyVersionId: Guid.NewGuid(),
                Timeframe: "1m"),
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

    [Fact]
    public async Task EvaluateAsync_BlocksPilotWhenScopeViolationsProduceMultipleReasons()
    {
        await using var dbContext = CreateDbContext();
        var allowedBotId = Guid.NewGuid();
        var requestedBotId = Guid.NewGuid();
        var guard = new UserExecutionOverrideGuard(
            dbContext,
            new FakeTradingModeResolver(),
            logger: NullLogger<UserExecutionOverrideGuard>.Instance,
            hostEnvironment: new TestHostEnvironment(Environments.Development),
            botExecutionPilotOptions: Options.Create(new BotExecutionPilotOptions
            {
                Enabled = true,
                AllowedUserIds = ["user-allowed"],
                AllowedBotIds = [allowedBotId.ToString("N")],
                AllowedSymbols = ["BTCUSDT"],
                MaxOpenPositionsPerUser = 1,
                PerBotCooldownSeconds = 300,
                PerSymbolCooldownSeconds = 300,
                MaxOrderNotional = 100m,
                MaxDailyLossPercentage = 5m
            }));

        var result = await guard.EvaluateAsync(
            new UserExecutionOverrideEvaluationRequest(
                "user-other",
                "ETHUSDT",
                ExecutionEnvironment.Live,
                ExecutionOrderSide.Buy,
                0.01m,
                20000m,
                BotId: requestedBotId,
                StrategyKey: "pilot-core",
                Context: "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                Plane: ExchangeDataPlane.Futures),
            CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Equal("UserExecutionPilotUserNotAllowed", result.BlockCode);
        Assert.NotNull(result.BlockReasons);
        Assert.Contains("UserExecutionPilotUserNotAllowed", result.BlockReasons!, StringComparer.Ordinal);
        Assert.Contains("UserExecutionPilotBotNotAllowed", result.BlockReasons!, StringComparer.Ordinal);
        Assert.Contains("UserExecutionPilotSymbolNotAllowed", result.BlockReasons!, StringComparer.Ordinal);
        Assert.Contains("UserExecutionPilotNotionalHardCapExceeded", result.BlockReasons!, StringComparer.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_AllowsPilotWhenRequestedNotionalIsWithinConfiguredHardCap()
    {
        await using var dbContext = CreateDbContext();
        var botId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        var evaluatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        dbContext.RiskProfiles.Add(new RiskProfile
        {
            OwnerUserId = "user-pilot-cap",
            ProfileName = "Pilot",
            MaxDailyLossPercentage = 5m,
            MaxPositionSizePercentage = 100m,
            MaxLeverage = 2m,
            MaxConcurrentPositions = 1
        });
        dbContext.ExchangeBalances.Add(new ExchangeBalance
        {
            ExchangeAccountId = exchangeAccountId,
            OwnerUserId = "user-pilot-cap",
            Plane = ExchangeDataPlane.Futures,
            Asset = "USDT",
            WalletBalance = 1000m,
            CrossWalletBalance = 1000m,
            AvailableBalance = 1000m,
            MaxWithdrawAmount = 1000m,
            ExchangeUpdatedAtUtc = evaluatedAtUtc
        });
        await dbContext.SaveChangesAsync();

        var guard = new UserExecutionOverrideGuard(
            dbContext,
            new FakeTradingModeResolver(),
            logger: NullLogger<UserExecutionOverrideGuard>.Instance,
            hostEnvironment: new TestHostEnvironment(Environments.Development),
            riskPolicyEvaluator: new RiskPolicyEvaluator(
                dbContext,
                timeProvider,
                NullLogger<RiskPolicyEvaluator>.Instance),
            botExecutionPilotOptions: Options.Create(new BotExecutionPilotOptions
            {
                Enabled = true,
                AllowedUserIds = ["user-pilot-cap"],
                AllowedBotIds = [botId.ToString("N")],
                AllowedSymbols = ["BTCUSDT"],
                MaxPilotOrderNotional = "150",
                MaxOpenPositionsPerUser = 1,
                PerBotCooldownSeconds = 300,
                PerSymbolCooldownSeconds = 300,
                MaxDailyLossPercentage = 5m
            }));

        var result = await guard.EvaluateAsync(
            new UserExecutionOverrideEvaluationRequest(
                "user-pilot-cap",
                "BTCUSDT",
                ExecutionEnvironment.Live,
                ExecutionOrderSide.Buy,
                0.002m,
                65000m,
                BotId: botId,
                StrategyKey: "pilot-core",
                Context: "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                TradingStrategyId: Guid.NewGuid(),
                TradingStrategyVersionId: Guid.NewGuid(),
                Timeframe: "1m"),
            CancellationToken.None);

        Assert.False(result.IsBlocked);
        Assert.Null(result.BlockCode);
        Assert.Contains("MaxPilotOrderNotional=150", result.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("RequestedNotional=130", result.GuardSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_BlocksPilotWhenRequestedNotionalExceedsConfiguredHardCap()
    {
        await using var dbContext = CreateDbContext();
        var botId = Guid.NewGuid();

        var guard = new UserExecutionOverrideGuard(
            dbContext,
            new FakeTradingModeResolver(),
            logger: NullLogger<UserExecutionOverrideGuard>.Instance,
            hostEnvironment: new TestHostEnvironment(Environments.Development),
            botExecutionPilotOptions: Options.Create(new BotExecutionPilotOptions
            {
                Enabled = true,
                AllowedUserIds = ["user-pilot-cap"],
                AllowedBotIds = [botId.ToString("N")],
                AllowedSymbols = ["BTCUSDT"],
                MaxPilotOrderNotional = "125",
                MaxOpenPositionsPerUser = 1,
                PerBotCooldownSeconds = 300,
                PerSymbolCooldownSeconds = 300,
                MaxDailyLossPercentage = 5m
            }));

        var result = await guard.EvaluateAsync(
            new UserExecutionOverrideEvaluationRequest(
                "user-pilot-cap",
                "BTCUSDT",
                ExecutionEnvironment.Live,
                ExecutionOrderSide.Buy,
                0.002m,
                65000m,
                BotId: botId,
                StrategyKey: "pilot-core",
                Context: "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                Plane: ExchangeDataPlane.Futures),
            CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Equal("UserExecutionPilotNotionalHardCapExceeded", result.BlockCode);
        Assert.Contains("UserExecutionPilotNotionalHardCapExceeded", result.BlockReasons!, StringComparer.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_BlocksPilotWhenNotionalCapConfigurationIsMissing()
    {
        await using var dbContext = CreateDbContext();
        var botId = Guid.NewGuid();

        var guard = new UserExecutionOverrideGuard(
            dbContext,
            new FakeTradingModeResolver(),
            logger: NullLogger<UserExecutionOverrideGuard>.Instance,
            hostEnvironment: new TestHostEnvironment(Environments.Development),
            botExecutionPilotOptions: Options.Create(new BotExecutionPilotOptions
            {
                Enabled = true,
                AllowedUserIds = ["user-pilot-missing"],
                AllowedBotIds = [botId.ToString("N")],
                AllowedSymbols = ["BTCUSDT"],
                MaxOpenPositionsPerUser = 1,
                PerBotCooldownSeconds = 300,
                PerSymbolCooldownSeconds = 300,
                MaxDailyLossPercentage = 5m
            }));

        var result = await guard.EvaluateAsync(
            new UserExecutionOverrideEvaluationRequest(
                "user-pilot-missing",
                "BTCUSDT",
                ExecutionEnvironment.Live,
                ExecutionOrderSide.Buy,
                0.001m,
                65000m,
                BotId: botId,
                StrategyKey: "pilot-core",
                Context: "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                Plane: ExchangeDataPlane.Futures),
            CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Equal("UserExecutionPilotNotionalConfigurationMissing", result.BlockCode);
        Assert.Contains("MaxPilotOrderNotional=missing", result.GuardSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_BlocksPilotWhenNotionalCapConfigurationIsInvalid()
    {
        await using var dbContext = CreateDbContext();
        var botId = Guid.NewGuid();

        var guard = new UserExecutionOverrideGuard(
            dbContext,
            new FakeTradingModeResolver(),
            logger: NullLogger<UserExecutionOverrideGuard>.Instance,
            hostEnvironment: new TestHostEnvironment(Environments.Development),
            botExecutionPilotOptions: Options.Create(new BotExecutionPilotOptions
            {
                Enabled = true,
                AllowedUserIds = ["user-pilot-invalid"],
                AllowedBotIds = [botId.ToString("N")],
                AllowedSymbols = ["BTCUSDT"],
                MaxPilotOrderNotional = "not-a-number",
                MaxOpenPositionsPerUser = 1,
                PerBotCooldownSeconds = 300,
                PerSymbolCooldownSeconds = 300,
                MaxDailyLossPercentage = 5m
            }));

        var result = await guard.EvaluateAsync(
            new UserExecutionOverrideEvaluationRequest(
                "user-pilot-invalid",
                "BTCUSDT",
                ExecutionEnvironment.Live,
                ExecutionOrderSide.Buy,
                0.001m,
                65000m,
                BotId: botId,
                StrategyKey: "pilot-core",
                Context: "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                Plane: ExchangeDataPlane.Futures),
            CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Equal("UserExecutionPilotNotionalConfigurationInvalid", result.BlockCode);
        Assert.Contains("MaxPilotOrderNotional=invalid:not-a-number", result.GuardSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_BlocksPilotWhenNotionalDataIsUnavailable()
    {
        await using var dbContext = CreateDbContext();
        var botId = Guid.NewGuid();

        var guard = new UserExecutionOverrideGuard(
            dbContext,
            new FakeTradingModeResolver(),
            logger: NullLogger<UserExecutionOverrideGuard>.Instance,
            hostEnvironment: new TestHostEnvironment(Environments.Development),
            botExecutionPilotOptions: Options.Create(new BotExecutionPilotOptions
            {
                Enabled = true,
                AllowedUserIds = ["user-pilot-no-price"],
                AllowedBotIds = [botId.ToString("N")],
                AllowedSymbols = ["BTCUSDT"],
                MaxPilotOrderNotional = "125",
                MaxOpenPositionsPerUser = 1,
                PerBotCooldownSeconds = 300,
                PerSymbolCooldownSeconds = 300,
                MaxDailyLossPercentage = 5m
            }));

        var result = await guard.EvaluateAsync(
            new UserExecutionOverrideEvaluationRequest(
                "user-pilot-no-price",
                "BTCUSDT",
                ExecutionEnvironment.Live,
                ExecutionOrderSide.Buy,
                0.001m,
                0m,
                BotId: botId,
                StrategyKey: "pilot-core",
                Context: "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                Plane: ExchangeDataPlane.Futures),
            CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Equal("UserExecutionPilotNotionalDataUnavailable", result.BlockCode);
        Assert.Contains("RequestedNotional=unavailable", result.GuardSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_BlocksPilotWhenRiskEvaluationIsUnavailable()
    {
        await using var dbContext = CreateDbContext();
        var botId = Guid.NewGuid();

        var guard = new UserExecutionOverrideGuard(
            dbContext,
            new FakeTradingModeResolver(),
            logger: NullLogger<UserExecutionOverrideGuard>.Instance,
            hostEnvironment: new TestHostEnvironment(Environments.Development),
            botExecutionPilotOptions: Options.Create(new BotExecutionPilotOptions
            {
                Enabled = true,
                AllowedUserIds = ["user-pilot-loss"],
                AllowedBotIds = [botId.ToString("N")],
                AllowedSymbols = ["BTCUSDT"],
                MaxOpenPositionsPerUser = 1,
                PerBotCooldownSeconds = 300,
                PerSymbolCooldownSeconds = 300,
                MaxOrderNotional = 200m,
                MaxDailyLossPercentage = 5m
            }));

        var result = await guard.EvaluateAsync(
            new UserExecutionOverrideEvaluationRequest(
                "user-pilot-loss",
                "BTCUSDT",
                ExecutionEnvironment.Live,
                ExecutionOrderSide.Buy,
                0.001m,
                65000m,
                BotId: botId,
                StrategyKey: "pilot-core",
                Context: "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                TradingStrategyId: Guid.NewGuid(),
                TradingStrategyVersionId: Guid.NewGuid(),
                Timeframe: "1m",
                Plane: ExchangeDataPlane.Futures),
            CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Equal("UserExecutionPilotRiskEvaluationUnavailable", result.BlockCode);
        Assert.Contains("pilot daily loss evaluation inputs are unavailable", result.Message, StringComparison.OrdinalIgnoreCase);
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





