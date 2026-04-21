using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Alerts;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoinBot.UnitTests.Infrastructure.Exchange;

public sealed class ExchangePrivatePlaneSyncTests
{
    [Fact]
    public async Task BalanceAndPositionSyncServices_ReplaceCurrentState_AndMarkMissingRowsDeleted()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var context = CreateContext(databaseRoot);
        var exchangeAccountId = Guid.NewGuid();
        var snapshot = new ExchangeAccountSnapshot(
            exchangeAccountId,
            "user-sync",
            "Binance",
            [
                new ExchangeBalanceSnapshot("USDT", 150m, 145m, 140m, 140m, DateTime.UtcNow),
                new ExchangeBalanceSnapshot("BTC", 1.5m, 1.5m, 1.5m, 1.5m, DateTime.UtcNow)
            ],
            [
                new ExchangePositionSnapshot("BTCUSDT", "LONG", 2m, 52000m, 52000m, 25m, "cross", 0m, DateTime.UtcNow)
            ],
            DateTime.UtcNow,
            DateTime.UtcNow,
            "Binance.PrivateRest.Account");

        context.ExchangeBalances.Add(new ExchangeBalance
        {
            OwnerUserId = "user-sync",
            ExchangeAccountId = exchangeAccountId,
            Asset = "BNB",
            WalletBalance = 10m,
            CrossWalletBalance = 10m
        });
        context.ExchangePositions.Add(new ExchangePosition
        {
            OwnerUserId = "user-sync",
            ExchangeAccountId = exchangeAccountId,
            Symbol = "ETHUSDT",
            PositionSide = "SHORT",
            Quantity = 1m,
            EntryPrice = 3000m,
            BreakEvenPrice = 3000m,
            UnrealizedProfit = 5m,
            MarginType = "cross",
            IsolatedWallet = 0m
        });
        await context.SaveChangesAsync();

        var balanceSyncService = new ExchangeBalanceSyncService(context, NullLogger<ExchangeBalanceSyncService>.Instance);
        var positionSyncService = new ExchangePositionSyncService(context, NullLogger<ExchangePositionSyncService>.Instance);

        await balanceSyncService.ApplyAsync(snapshot);
        await positionSyncService.ApplyAsync(snapshot);

        var activeBalances = await context.ExchangeBalances
            .Where(entity => entity.ExchangeAccountId == exchangeAccountId && !entity.IsDeleted)
            .OrderBy(entity => entity.Asset)
            .ToListAsync();
        var deletedBalance = await context.ExchangeBalances.SingleAsync(entity => entity.Asset == "BNB");
        var activePositions = await context.ExchangePositions
            .Where(entity => entity.ExchangeAccountId == exchangeAccountId && !entity.IsDeleted)
            .ToListAsync();
        var deletedPosition = await context.ExchangePositions.SingleAsync(entity => entity.Symbol == "ETHUSDT");

        Assert.Equal(["BTC", "USDT"], activeBalances.Select(entity => entity.Asset));
        Assert.True(deletedBalance.IsDeleted);
        Assert.Single(activePositions);
        Assert.Equal("BTCUSDT", activePositions[0].Symbol);
        Assert.True(deletedPosition.IsDeleted);
        Assert.Equal(0m, deletedPosition.Quantity);
        Assert.Equal(0m, deletedPosition.EntryPrice);
    }

    [Fact]
    public async Task PositionSyncService_RefreshesLinkedBotCounters_FromLatestPrivatePlaneTruth()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var context = CreateContext(databaseRoot);
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        var observedAtUtc = new DateTime(2026, 4, 20, 9, 0, 0, DateTimeKind.Utc);
        var snapshot = new ExchangeAccountSnapshot(
            exchangeAccountId,
            "user-counter",
            "Binance",
            [],
            [],
            observedAtUtc,
            observedAtUtc,
            "Binance.PrivateRest.Account");

        context.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = "user-counter",
            Name = "Counter bot",
            StrategyKey = "counter-core",
            Symbol = "SOLUSDT",
            ExchangeAccountId = exchangeAccountId,
            IsEnabled = true,
            OpenOrderCount = 1,
            OpenPositionCount = 1
        });
        context.ExchangePositions.Add(new ExchangePosition
        {
            OwnerUserId = "user-counter",
            ExchangeAccountId = exchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            Symbol = "SOLUSDT",
            PositionSide = "BOTH",
            Quantity = 0.06m,
            EntryPrice = 85m,
            BreakEvenPrice = 85m,
            MarginType = "isolated",
            ExchangeUpdatedAtUtc = observedAtUtc.AddMinutes(-1),
            SyncedAtUtc = observedAtUtc.AddMinutes(-1)
        });
        await context.SaveChangesAsync();

        var positionSyncService = new ExchangePositionSyncService(context, NullLogger<ExchangePositionSyncService>.Instance);

        await positionSyncService.ApplyAsync(snapshot);

        var bot = await context.TradingBots.SingleAsync(entity => entity.Id == botId);
        var position = await context.ExchangePositions.SingleAsync(entity => entity.ExchangeAccountId == exchangeAccountId);

        Assert.True(position.IsDeleted);
        Assert.Equal(0m, position.Quantity);
        Assert.Equal(0, bot.OpenPositionCount);
        Assert.Equal(0, bot.OpenOrderCount);
    }

    [Fact]
    public async Task PositionSyncService_DoesNotTreatSubmittedOrderAsOpenPosition_WhenOrderIsAfterSnapshot()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var context = CreateContext(databaseRoot);
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        var observedAtUtc = new DateTime(2026, 4, 20, 9, 0, 0, DateTimeKind.Utc);
        var snapshot = new ExchangeAccountSnapshot(
            exchangeAccountId,
            "user-counter-open",
            "Binance",
            [],
            [],
            observedAtUtc,
            observedAtUtc,
            "Binance.PrivateRest.Account");

        context.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = "user-counter-open",
            Name = "Counter open bot",
            StrategyKey = "counter-open-core",
            Symbol = "SOLUSDT",
            ExchangeAccountId = exchangeAccountId,
            IsEnabled = true
        });
        context.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-counter-open",
            BotId = botId,
            ExchangeAccountId = exchangeAccountId,
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            StrategyKey = "counter-open-core",
            Symbol = "SOLUSDT",
            Timeframe = "1m",
            BaseAsset = "SOL",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.06m,
            Price = 85m,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            State = ExecutionOrderState.Submitted,
            SubmittedToBroker = true,
            SubmittedAtUtc = observedAtUtc.AddSeconds(5),
            LastStateChangedAtUtc = observedAtUtc.AddSeconds(5),
            IdempotencyKey = "counter-open-order",
            RootCorrelationId = "counter-open-root"
        });
        await context.SaveChangesAsync();

        var positionSyncService = new ExchangePositionSyncService(context, NullLogger<ExchangePositionSyncService>.Instance);

        await positionSyncService.ApplyAsync(snapshot);

        var bot = await context.TradingBots.SingleAsync(entity => entity.Id == botId);

        Assert.Equal(0, bot.OpenPositionCount);
        Assert.Equal(1, bot.OpenOrderCount);
    }

    [Fact]
    public async Task PositionSyncService_RefreshesLinkedBotCounters_ByBotSymbol()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var context = CreateContext(databaseRoot);
        var exchangeAccountId = Guid.NewGuid();
        var solBotId = Guid.NewGuid();
        var ethBotId = Guid.NewGuid();
        var observedAtUtc = new DateTime(2026, 4, 20, 9, 0, 0, DateTimeKind.Utc);
        var snapshot = new ExchangeAccountSnapshot(
            exchangeAccountId,
            "user-counter-symbol",
            "Binance",
            [],
            [
                new ExchangePositionSnapshot("SOLUSDT", "BOTH", 0.06m, 85m, 85m, 0.12m, "isolated", 5.10m, observedAtUtc)
            ],
            observedAtUtc,
            observedAtUtc,
            "Binance.PrivateRest.Account+PositionRisk");

        context.TradingBots.AddRange(
            new TradingBot
            {
                Id = solBotId,
                OwnerUserId = "user-counter-symbol",
                Name = "SOL bot",
                StrategyKey = "counter-symbol-sol",
                Symbol = "SOLUSDT",
                ExchangeAccountId = exchangeAccountId,
                IsEnabled = true
            },
            new TradingBot
            {
                Id = ethBotId,
                OwnerUserId = "user-counter-symbol",
                Name = "ETH bot",
                StrategyKey = "counter-symbol-eth",
                Symbol = "ETHUSDT",
                ExchangeAccountId = exchangeAccountId,
                IsEnabled = true,
                OpenPositionCount = 1
            });
        await context.SaveChangesAsync();

        var positionSyncService = new ExchangePositionSyncService(context, NullLogger<ExchangePositionSyncService>.Instance);

        await positionSyncService.ApplyAsync(snapshot);

        var solBot = await context.TradingBots.SingleAsync(entity => entity.Id == solBotId);
        var ethBot = await context.TradingBots.SingleAsync(entity => entity.Id == ethBotId);

        Assert.Equal(1, solBot.OpenPositionCount);
        Assert.Equal(0, ethBot.OpenPositionCount);
        Assert.Equal(0, solBot.OpenOrderCount);
        Assert.Equal(0, ethBot.OpenOrderCount);
    }

    [Fact]
    public async Task PositionSyncService_RefreshesLinkedBotCounters_FromPersistedBrokerTruth_EvenWhenLaterFilledOrdersExist()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var context = CreateContext(databaseRoot);
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        var observedAtUtc = new DateTime(2026, 4, 20, 9, 0, 0, DateTimeKind.Utc);
        var snapshot = new ExchangeAccountSnapshot(
            exchangeAccountId,
            "user-counter-parity",
            "Binance",
            [],
            [
                new ExchangePositionSnapshot("SOLUSDT", "BOTH", 0.06m, 85m, 85m, 0.12m, "isolated", 5.10m, observedAtUtc)
            ],
            observedAtUtc,
            observedAtUtc,
            "Binance.PrivateRest.Account+PositionRisk");

        context.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = "user-counter-parity",
            Name = "Parity bot",
            StrategyKey = "counter-parity-core",
            Symbol = "SOLUSDT",
            ExchangeAccountId = exchangeAccountId,
            IsEnabled = true
        });
        context.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-counter-parity",
            BotId = botId,
            ExchangeAccountId = exchangeAccountId,
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Exit,
            StrategyKey = "counter-parity-core",
            Symbol = "SOLUSDT",
            Timeframe = "1m",
            BaseAsset = "SOL",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Sell,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.06m,
            FilledQuantity = 0.06m,
            Price = 85m,
            AverageFillPrice = 85m,
            ExecutionEnvironment = ExecutionEnvironment.Demo,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            State = ExecutionOrderState.Filled,
            SubmittedToBroker = true,
            SubmittedAtUtc = observedAtUtc.AddSeconds(5),
            LastFilledAtUtc = observedAtUtc.AddSeconds(5),
            LastStateChangedAtUtc = observedAtUtc.AddSeconds(5),
            IdempotencyKey = "counter-parity-order",
            RootCorrelationId = "counter-parity-root"
        });
        await context.SaveChangesAsync();

        var positionSyncService = new ExchangePositionSyncService(context, NullLogger<ExchangePositionSyncService>.Instance);

        await positionSyncService.ApplyAsync(snapshot);

        var bot = await context.TradingBots.SingleAsync(entity => entity.Id == botId);

        Assert.Equal(1, bot.OpenPositionCount);
        Assert.Equal(0, bot.OpenOrderCount);
    }

    [Fact]
    public async Task PositionSyncService_DoesNotLeakSameSymbolFromAnotherAccount()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var context = CreateContext(databaseRoot);
        var targetExchangeAccountId = Guid.NewGuid();
        var otherExchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        var observedAtUtc = new DateTime(2026, 4, 20, 9, 0, 0, DateTimeKind.Utc);
        var snapshot = new ExchangeAccountSnapshot(
            targetExchangeAccountId,
            "user-counter-account",
            "Binance",
            [],
            [],
            observedAtUtc,
            observedAtUtc,
            "Binance.PrivateRest.Account+PositionRisk");

        context.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = "user-counter-account",
            Name = "Account scoped bot",
            StrategyKey = "counter-account-core",
            Symbol = "SOLUSDT",
            ExchangeAccountId = targetExchangeAccountId,
            IsEnabled = true,
            OpenPositionCount = 1
        });
        context.ExchangePositions.Add(new ExchangePosition
        {
            OwnerUserId = "user-counter-account",
            ExchangeAccountId = otherExchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            Symbol = "SOLUSDT",
            PositionSide = "BOTH",
            Quantity = 0.06m,
            EntryPrice = 85m,
            BreakEvenPrice = 85m,
            MarginType = "isolated",
            ExchangeUpdatedAtUtc = observedAtUtc,
            SyncedAtUtc = observedAtUtc
        });
        await context.SaveChangesAsync();

        var positionSyncService = new ExchangePositionSyncService(context, NullLogger<ExchangePositionSyncService>.Instance);

        await positionSyncService.ApplyAsync(snapshot);

        var bot = await context.TradingBots.SingleAsync(entity => entity.Id == botId);

        Assert.Equal(0, bot.OpenPositionCount);
        Assert.Equal(0, bot.OpenOrderCount);
    }

    [Fact]
    public async Task PositionSyncService_DoesNotZeroBrokerBackedFuturesBotCounter_WhenSpotSnapshotArrivesForSameAccount()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var context = CreateContext(databaseRoot);
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        var observedAtUtc = new DateTime(2026, 4, 20, 9, 0, 0, DateTimeKind.Utc);
        var snapshot = new ExchangeAccountSnapshot(
            exchangeAccountId,
            "user-counter-plane",
            "Binance",
            [],
            [],
            observedAtUtc,
            observedAtUtc,
            "Binance.SpotPrivateRest.Account",
            ExchangeDataPlane.Spot);

        context.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = "user-counter-plane",
            Name = "Plane scoped bot",
            StrategyKey = "counter-plane-core",
            Symbol = "SOLUSDT",
            ExchangeAccountId = exchangeAccountId,
            IsEnabled = true,
            OpenPositionCount = 0
        });
        context.ExchangePositions.Add(new ExchangePosition
        {
            OwnerUserId = "user-counter-plane",
            ExchangeAccountId = exchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            Symbol = "SOLUSDT",
            PositionSide = "BOTH",
            Quantity = 0.06m,
            EntryPrice = 85m,
            BreakEvenPrice = 85m,
            MarginType = "isolated",
            ExchangeUpdatedAtUtc = observedAtUtc.AddSeconds(-30),
            SyncedAtUtc = observedAtUtc.AddSeconds(-30)
        });
        await context.SaveChangesAsync();

        var positionSyncService = new ExchangePositionSyncService(context, NullLogger<ExchangePositionSyncService>.Instance);

        await positionSyncService.ApplyAsync(snapshot);

        var bot = await context.TradingBots.SingleAsync(entity => entity.Id == botId);

        Assert.Equal(1, bot.OpenPositionCount);
        Assert.Equal(0, bot.OpenOrderCount);
    }

    [Fact]
    public async Task PositionSyncService_DoesNotCountFilledInSyncOrderAsOpenOrder()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var context = CreateContext(databaseRoot);
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        var observedAtUtc = new DateTime(2026, 4, 20, 9, 0, 0, DateTimeKind.Utc);
        var snapshot = new ExchangeAccountSnapshot(
            exchangeAccountId,
            "user-counter-filled",
            "Binance",
            [],
            [],
            observedAtUtc,
            observedAtUtc,
            "Binance.PrivateRest.Account");

        context.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = "user-counter-filled",
            Name = "Counter filled bot",
            StrategyKey = "counter-filled-core",
            Symbol = "SOLUSDT",
            ExchangeAccountId = exchangeAccountId,
            IsEnabled = true,
            OpenOrderCount = 1,
            OpenPositionCount = 1
        });
        context.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-counter-filled",
            BotId = botId,
            ExchangeAccountId = exchangeAccountId,
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            StrategyKey = "counter-filled-core",
            Symbol = "SOLUSDT",
            Timeframe = "1m",
            BaseAsset = "SOL",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.06m,
            Price = 85m,
            FilledQuantity = 0.06m,
            AverageFillPrice = 85m,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            State = ExecutionOrderState.Filled,
            SubmittedToBroker = true,
            SubmittedAtUtc = observedAtUtc.AddMinutes(-1),
            LastFilledAtUtc = observedAtUtc.AddMinutes(-1),
            LastStateChangedAtUtc = observedAtUtc.AddMinutes(-1),
            ReconciliationStatus = ExchangeStateDriftStatus.InSync,
            IdempotencyKey = "counter-filled-order",
            RootCorrelationId = "counter-filled-root"
        });
        await context.SaveChangesAsync();

        var positionSyncService = new ExchangePositionSyncService(context, NullLogger<ExchangePositionSyncService>.Instance);

        await positionSyncService.ApplyAsync(snapshot);

        var bot = await context.TradingBots.SingleAsync(entity => entity.Id == botId);

        Assert.Equal(0, bot.OpenPositionCount);
        Assert.Equal(0, bot.OpenOrderCount);
    }

    [Fact]
    public async Task ExchangeAppStateSyncService_FlagsDrift_AndPublishesFreshSnapshot()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        var exchangeAccountId = Guid.NewGuid();
        var observedAtUtc = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc);
        await using var context = CreateContext(databaseRoot);
        context.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = "user-drift",
            ExchangeName = "Binance",
            DisplayName = "Primary",
            ApiKeyCiphertext = "cipher-api-key",
            ApiSecretCiphertext = "cipher-api-secret",
            CredentialStatus = ExchangeCredentialStatus.Active
        });
        context.ExchangeBalances.Add(new ExchangeBalance
        {
            OwnerUserId = "user-drift",
            ExchangeAccountId = exchangeAccountId,
            Asset = "USDT",
            WalletBalance = 100m,
            CrossWalletBalance = 100m
        });
        context.ExchangePositions.Add(new ExchangePosition
        {
            OwnerUserId = "user-drift",
            ExchangeAccountId = exchangeAccountId,
            Symbol = "BTCUSDT",
            PositionSide = "LONG",
            Quantity = 1m,
            EntryPrice = 50000m,
            BreakEvenPrice = 50000m,
            UnrealizedProfit = 10m,
            MarginType = "cross",
            IsolatedWallet = 0m
        });
        await context.SaveChangesAsync();

        var snapshot = new ExchangeAccountSnapshot(
            exchangeAccountId,
            "user-drift",
            "Binance",
            [
                new ExchangeBalanceSnapshot("USDT", 125m, 120m, 120m, 120m, observedAtUtc)
            ],
            [
                new ExchangePositionSnapshot("BTCUSDT", "LONG", 2m, 51000m, 51000m, 15m, "cross", 0m, observedAtUtc)
            ],
            observedAtUtc,
            observedAtUtc,
            "Binance.PrivateRest.Account");
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(observedAtUtc));
        var snapshotHub = new ExchangeAccountSnapshotHub();
        var syncStateService = new ExchangeAccountSyncStateService(context);
        var service = new ExchangeAppStateSyncService(
            context,
            new FakeExchangeCredentialService(exchangeAccountId),
            new FakePrivateRestClient(snapshot),
            snapshotHub,
            syncStateService,
            timeProvider,
            NullLogger<ExchangeAppStateSyncService>.Instance);

        await using var enumerator = snapshotHub.SubscribeAsync().GetAsyncEnumerator();
        var moveNextTask = enumerator.MoveNextAsync().AsTask();

        await service.RunOnceAsync();

        Assert.True(await moveNextTask);
        Assert.Equal(snapshot, enumerator.Current);

        var state = await context.ExchangeAccountSyncStates.SingleAsync(entity => entity.ExchangeAccountId == exchangeAccountId);

        Assert.Equal(ExchangeStateDriftStatus.DriftDetected, state.DriftStatus);
        Assert.Contains("BalanceMismatches=1", state.DriftSummary, StringComparison.Ordinal);
        Assert.Contains("PositionMismatches=1", state.DriftSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangeAppStateSyncService_DoesNotFlagDrift_WhenOnlyVolatilePositionFieldsChange()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        var exchangeAccountId = Guid.NewGuid();
        var observedAtUtc = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);
        await using var context = CreateContext(databaseRoot);
        context.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = "user-volatile-drift",
            ExchangeName = "Binance",
            DisplayName = "Primary",
            ApiKeyCiphertext = "cipher-api-key",
            ApiSecretCiphertext = "cipher-api-secret",
            CredentialStatus = ExchangeCredentialStatus.Active
        });
        context.ExchangePositions.Add(new ExchangePosition
        {
            OwnerUserId = "user-volatile-drift",
            ExchangeAccountId = exchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            Symbol = "SOLUSDT",
            PositionSide = "BOTH",
            Quantity = 0.06m,
            EntryPrice = 85m,
            BreakEvenPrice = 85.02m,
            UnrealizedProfit = 0.10m,
            MarginType = "isolated",
            IsolatedWallet = 5.10m,
            ExchangeUpdatedAtUtc = observedAtUtc.AddSeconds(-5),
            SyncedAtUtc = observedAtUtc.AddSeconds(-5)
        });
        await context.SaveChangesAsync();

        var snapshot = new ExchangeAccountSnapshot(
            exchangeAccountId,
            "user-volatile-drift",
            "Binance",
            [],
            [
                new ExchangePositionSnapshot("SOLUSDT", "BOTH", 0.06m, 85m, 85.02m, 0.15m, "isolated", 5.20m, observedAtUtc)
            ],
            observedAtUtc,
            observedAtUtc,
            "Binance.PrivateRest.Account+PositionRisk");
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(observedAtUtc));
        var service = new ExchangeAppStateSyncService(
            context,
            new FakeExchangeCredentialService(exchangeAccountId),
            new FakePrivateRestClient(snapshot),
            new ExchangeAccountSnapshotHub(),
            new ExchangeAccountSyncStateService(context),
            timeProvider,
            NullLogger<ExchangeAppStateSyncService>.Instance);

        await service.RunOnceAsync();

        var state = await context.ExchangeAccountSyncStates.SingleAsync(entity => entity.ExchangeAccountId == exchangeAccountId);

        Assert.Equal(ExchangeStateDriftStatus.InSync, state.DriftStatus);
        Assert.Contains("PositionMismatches=0", state.DriftSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangeAppStateSyncService_DoesNotFlagDrift_WhenPersistedZeroQuantityPositionMissingFromSnapshot()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        var exchangeAccountId = Guid.NewGuid();
        var observedAtUtc = new DateTime(2026, 4, 20, 10, 15, 0, DateTimeKind.Utc);
        await using var context = CreateContext(databaseRoot);
        context.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = "user-zero-drift",
            ExchangeName = "Binance",
            DisplayName = "Primary",
            ApiKeyCiphertext = "cipher-api-key",
            ApiSecretCiphertext = "cipher-api-secret",
            CredentialStatus = ExchangeCredentialStatus.Active
        });
        context.ExchangePositions.Add(new ExchangePosition
        {
            OwnerUserId = "user-zero-drift",
            ExchangeAccountId = exchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            Symbol = "SOLUSDT",
            PositionSide = "BOTH",
            Quantity = 0m,
            EntryPrice = 85m,
            BreakEvenPrice = 85m,
            UnrealizedProfit = 0m,
            MarginType = "isolated",
            IsolatedWallet = 0m,
            ExchangeUpdatedAtUtc = observedAtUtc.AddSeconds(-5),
            SyncedAtUtc = observedAtUtc.AddSeconds(-5)
        });
        await context.SaveChangesAsync();

        var snapshot = new ExchangeAccountSnapshot(
            exchangeAccountId,
            "user-zero-drift",
            "Binance",
            [],
            [],
            observedAtUtc,
            observedAtUtc,
            "Binance.PrivateRest.Account+PositionRisk");
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(observedAtUtc));
        var service = new ExchangeAppStateSyncService(
            context,
            new FakeExchangeCredentialService(exchangeAccountId),
            new FakePrivateRestClient(snapshot),
            new ExchangeAccountSnapshotHub(),
            new ExchangeAccountSyncStateService(context),
            timeProvider,
            NullLogger<ExchangeAppStateSyncService>.Instance);

        await service.RunOnceAsync();

        var state = await context.ExchangeAccountSyncStates.SingleAsync(entity => entity.ExchangeAccountId == exchangeAccountId);

        Assert.Equal(ExchangeStateDriftStatus.InSync, state.DriftStatus);
        Assert.Contains("PositionMismatches=0", state.DriftSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangeAppStateSyncService_DoesNotRepersistLocalExecutionProjection_WhenBrokerSnapshotIsFlat()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        var exchangeAccountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        var observedAtUtc = new DateTime(2026, 4, 15, 13, 34, 0, DateTimeKind.Utc);
        await using var context = CreateContext(databaseRoot);
        context.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = "user-projection",
            ExchangeName = "Binance",
            DisplayName = "Primary",
            ApiKeyCiphertext = "cipher-api-key",
            ApiSecretCiphertext = "cipher-api-secret",
            CredentialStatus = ExchangeCredentialStatus.Active
        });
        context.ExchangePositions.AddRange(
            new ExchangePosition
            {
                OwnerUserId = "user-projection",
                ExchangeAccountId = exchangeAccountId,
                Plane = ExchangeDataPlane.Futures,
                Symbol = "SOLUSDT",
                PositionSide = "BOTH",
                Quantity = 0.06m,
                EntryPrice = 83.54m,
                BreakEvenPrice = 83.54m,
                UnrealizedProfit = 0.11m,
                MarginType = "cross",
                IsolatedWallet = 0m,
                ExchangeUpdatedAtUtc = observedAtUtc.AddMinutes(-2),
                SyncedAtUtc = observedAtUtc.AddMinutes(-2)
            },
            new ExchangePosition
            {
                OwnerUserId = "user-other",
                ExchangeAccountId = otherAccountId,
                Plane = ExchangeDataPlane.Futures,
                Symbol = "SOLUSDT",
                PositionSide = "BOTH",
                Quantity = 0.25m,
                EntryPrice = 90m,
                BreakEvenPrice = 90m,
                UnrealizedProfit = 1m,
                MarginType = "cross",
                IsolatedWallet = 0m,
                ExchangeUpdatedAtUtc = observedAtUtc.AddMinutes(-2),
                SyncedAtUtc = observedAtUtc.AddMinutes(-2)
            });
        context.ExecutionOrders.AddRange(
            CreateFilledOrder(exchangeAccountId, "user-projection", StrategySignalType.Entry, ExecutionOrderSide.Buy, 0.06m, 83.62m, new DateTime(2026, 4, 15, 13, 24, 22, DateTimeKind.Utc)),
            CreateFilledOrder(exchangeAccountId, "user-projection", StrategySignalType.Exit, ExecutionOrderSide.Sell, 0.06m, 83.65m, new DateTime(2026, 4, 15, 13, 26, 29, DateTimeKind.Utc), reduceOnly: true),
            CreateFilledOrder(exchangeAccountId, "user-projection", StrategySignalType.Entry, ExecutionOrderSide.Buy, 0.06m, 83.54m, new DateTime(2026, 4, 15, 13, 33, 06, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var snapshot = new ExchangeAccountSnapshot(
            exchangeAccountId,
            "user-projection",
            "Binance",
            [],
            [],
            observedAtUtc,
            observedAtUtc,
            "Binance.PrivateRest.Account");
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(observedAtUtc));
        var snapshotHub = new ExchangeAccountSnapshotHub();
        var service = new ExchangeAppStateSyncService(
            context,
            new FakeExchangeCredentialService(exchangeAccountId),
            new FakePrivateRestClient(snapshot),
            snapshotHub,
            new ExchangeAccountSyncStateService(context),
            timeProvider,
            NullLogger<ExchangeAppStateSyncService>.Instance);
        var positionSyncService = new ExchangePositionSyncService(context, NullLogger<ExchangePositionSyncService>.Instance);

        await using var enumerator = snapshotHub.SubscribeAsync().GetAsyncEnumerator();
        var moveNextTask = enumerator.MoveNextAsync().AsTask();

        await service.RunOnceAsync();

        Assert.True(await moveNextTask);
        var publishedSnapshot = enumerator.Current;
        Assert.Empty(publishedSnapshot.Positions);
        Assert.DoesNotContain("Fallback=LocalExecutionProjection", publishedSnapshot.Source, StringComparison.Ordinal);

        await positionSyncService.ApplyAsync(publishedSnapshot);

        var targetPosition = await context.ExchangePositions.SingleAsync(entity =>
            entity.ExchangeAccountId == exchangeAccountId &&
            entity.Symbol == "SOLUSDT");
        var otherUserPosition = await context.ExchangePositions.SingleAsync(entity =>
            entity.ExchangeAccountId == otherAccountId &&
            entity.Symbol == "SOLUSDT");

        Assert.True(targetPosition.IsDeleted);
        Assert.Equal(0m, targetPosition.Quantity);
        Assert.Equal(0m, targetPosition.EntryPrice);
        Assert.Equal(0.25m, otherUserPosition.Quantity);
        Assert.False(otherUserPosition.IsDeleted);

        await service.RunOnceAsync();

        var state = await context.ExchangeAccountSyncStates.SingleAsync(entity => entity.ExchangeAccountId == exchangeAccountId);

        Assert.Equal(ExchangeStateDriftStatus.InSync, state.DriftStatus);
        Assert.Contains("PositionMismatches=0", state.DriftSummary, StringComparison.Ordinal);
    }


    [Fact]
    public async Task ExchangeAppStateSyncService_SendsSyncFailureAlert_WhenCredentialAccessIsBlocked()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        var exchangeAccountId = Guid.NewGuid();
        await using var context = CreateContext(databaseRoot);
        context.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = "user-blocked",
            ExchangeName = "Binance",
            DisplayName = "Primary",
            ApiKeyCiphertext = "cipher-api-key",
            ApiSecretCiphertext = "cipher-api-secret",
            CredentialStatus = ExchangeCredentialStatus.Active
        });
        await context.SaveChangesAsync();

        var alertCoordinator = new RecordingAlertDispatchCoordinator();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        var service = new ExchangeAppStateSyncService(
            context,
            new BlockingExchangeCredentialService(exchangeAccountId),
            new FakePrivateRestClient(new ExchangeAccountSnapshot(
                exchangeAccountId,
                "user-blocked",
                "Binance",
                [],
                [],
                timeProvider.GetUtcNow().UtcDateTime,
                timeProvider.GetUtcNow().UtcDateTime,
                "Binance.PrivateRest.Account")),
            new ExchangeAccountSnapshotHub(),
            new ExchangeAccountSyncStateService(context),
            timeProvider,
            NullLogger<ExchangeAppStateSyncService>.Instance,
            alertCoordinator,
            new TestHostEnvironment(Environments.Development));

        await service.RunOnceAsync();

        var alert = Assert.Single(alertCoordinator.Notifications);
        Assert.Equal("SYNC_FAILED_CREDENTIALACCESSBLOCKED", alert.Code);
        Assert.Contains("EventType=SyncFailed", alert.Message, StringComparison.Ordinal);
        Assert.Contains("FailureCode=CredentialAccessBlocked", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BalanceAndPositionSyncServices_KeepOtherAccountsUntouched_ForSameUserAndDifferentUser()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var context = CreateContext(databaseRoot);
        var targetAccountId = Guid.NewGuid();
        var sameUserOtherAccountId = Guid.NewGuid();
        var otherUserAccountId = Guid.NewGuid();
        var observedAtUtc = new DateTime(2026, 4, 5, 12, 0, 0, DateTimeKind.Utc);
        var snapshot = new ExchangeAccountSnapshot(
            targetAccountId,
            "user-shared",
            "Binance",
            [
                new ExchangeBalanceSnapshot("USDT", 150m, 145m, 140m, 140m, observedAtUtc)
            ],
            [
                new ExchangePositionSnapshot("BTCUSDT", "LONG", 2m, 52000m, 52000m, 25m, "cross", 0m, observedAtUtc)
            ],
            observedAtUtc,
            observedAtUtc,
            "Binance.PrivateRest.Account");

        context.ExchangeBalances.AddRange(
            new ExchangeBalance
            {
                OwnerUserId = "user-shared",
                ExchangeAccountId = targetAccountId,
                Plane = ExchangeDataPlane.Futures,
                Asset = "BNB",
                WalletBalance = 10m,
                CrossWalletBalance = 10m
            },
            new ExchangeBalance
            {
                OwnerUserId = "user-shared",
                ExchangeAccountId = sameUserOtherAccountId,
                Plane = ExchangeDataPlane.Futures,
                Asset = "USDT",
                WalletBalance = 30m,
                CrossWalletBalance = 30m
            },
            new ExchangeBalance
            {
                OwnerUserId = "user-other",
                ExchangeAccountId = otherUserAccountId,
                Plane = ExchangeDataPlane.Futures,
                Asset = "USDT",
                WalletBalance = 45m,
                CrossWalletBalance = 45m
            });
        context.ExchangePositions.AddRange(
            new ExchangePosition
            {
                OwnerUserId = "user-shared",
                ExchangeAccountId = targetAccountId,
                Plane = ExchangeDataPlane.Futures,
                Symbol = "ETHUSDT",
                PositionSide = "SHORT",
                Quantity = 1m,
                EntryPrice = 3000m,
                BreakEvenPrice = 3000m,
                UnrealizedProfit = 5m,
                MarginType = "cross",
                IsolatedWallet = 0m
            },
            new ExchangePosition
            {
                OwnerUserId = "user-shared",
                ExchangeAccountId = sameUserOtherAccountId,
                Plane = ExchangeDataPlane.Futures,
                Symbol = "ETHUSDT",
                PositionSide = "LONG",
                Quantity = 0.5m,
                EntryPrice = 3100m,
                BreakEvenPrice = 3100m,
                UnrealizedProfit = 2m,
                MarginType = "cross",
                IsolatedWallet = 0m
            },
            new ExchangePosition
            {
                OwnerUserId = "user-other",
                ExchangeAccountId = otherUserAccountId,
                Plane = ExchangeDataPlane.Futures,
                Symbol = "SOLUSDT",
                PositionSide = "LONG",
                Quantity = 3m,
                EntryPrice = 120m,
                BreakEvenPrice = 120m,
                UnrealizedProfit = 9m,
                MarginType = "cross",
                IsolatedWallet = 0m
            });
        await context.SaveChangesAsync();

        var balanceSyncService = new ExchangeBalanceSyncService(context, NullLogger<ExchangeBalanceSyncService>.Instance);
        var positionSyncService = new ExchangePositionSyncService(context, NullLogger<ExchangePositionSyncService>.Instance);

        await balanceSyncService.ApplyAsync(snapshot);
        await positionSyncService.ApplyAsync(snapshot);

        var targetBalance = await context.ExchangeBalances.SingleAsync(entity =>
            entity.ExchangeAccountId == targetAccountId &&
            entity.Asset == "USDT");
        var targetPosition = await context.ExchangePositions.SingleAsync(entity =>
            entity.ExchangeAccountId == targetAccountId &&
            entity.Symbol == "BTCUSDT");
        var sameUserOtherBalance = await context.ExchangeBalances.SingleAsync(entity =>
            entity.ExchangeAccountId == sameUserOtherAccountId &&
            entity.Asset == "USDT");
        var otherUserBalance = await context.ExchangeBalances.SingleAsync(entity =>
            entity.ExchangeAccountId == otherUserAccountId &&
            entity.Asset == "USDT");
        var sameUserOtherPosition = await context.ExchangePositions.SingleAsync(entity =>
            entity.ExchangeAccountId == sameUserOtherAccountId &&
            entity.Symbol == "ETHUSDT");
        var otherUserPosition = await context.ExchangePositions.SingleAsync(entity =>
            entity.ExchangeAccountId == otherUserAccountId &&
            entity.Symbol == "SOLUSDT");

        Assert.Equal("user-shared", targetBalance.OwnerUserId);
        Assert.Equal(150m, targetBalance.WalletBalance);
        Assert.Equal(2m, targetPosition.Quantity);
        Assert.False(targetBalance.IsDeleted);
        Assert.False(targetPosition.IsDeleted);
        Assert.Equal(30m, sameUserOtherBalance.WalletBalance);
        Assert.Equal(45m, otherUserBalance.WalletBalance);
        Assert.Equal(0.5m, sameUserOtherPosition.Quantity);
        Assert.Equal(3m, otherUserPosition.Quantity);
        Assert.False(sameUserOtherBalance.IsDeleted);
        Assert.False(otherUserBalance.IsDeleted);
        Assert.False(sameUserOtherPosition.IsDeleted);
        Assert.False(otherUserPosition.IsDeleted);
    }
    private static ApplicationDbContext CreateContext(InMemoryDatabaseRoot databaseRoot)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"), databaseRoot)
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeExchangeCredentialService(Guid exchangeAccountId) : IExchangeCredentialService
    {
        public Task<ExchangeCredentialAccessResult> GetAsync(
            ExchangeCredentialAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(exchangeAccountId, request.ExchangeAccountId);
            Assert.Equal(ExchangeCredentialAccessPurpose.Synchronization, request.Purpose);

            return Task.FromResult(new ExchangeCredentialAccessResult(
                "api-key",
                "api-secret",
                new ExchangeCredentialStateSnapshot(
                    exchangeAccountId,
                    ExchangeCredentialStatus.Active,
                    Fingerprint: "fingerprint",
                    KeyVersion: "credential-v1",
                    StoredAtUtc: null,
                    LastValidatedAtUtc: null,
                    LastAccessedAtUtc: null,
                    LastRotatedAtUtc: null,
                    RevalidateAfterUtc: null,
                    RotateAfterUtc: null)));
        }

        public Task<ExchangeCredentialStateSnapshot> StoreAsync(StoreExchangeCredentialsRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExchangeCredentialStateSnapshot> SetValidationStateAsync(SetExchangeCredentialValidationStateRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExchangeCredentialStateSnapshot> GetStateAsync(Guid exchangeAccountId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class BlockingExchangeCredentialService(Guid exchangeAccountId) : IExchangeCredentialService
    {
        public Task<ExchangeCredentialAccessResult> GetAsync(
            ExchangeCredentialAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(exchangeAccountId, request.ExchangeAccountId);
            throw new InvalidOperationException("Synchronization access blocked.");
        }

        public Task<ExchangeCredentialStateSnapshot> StoreAsync(StoreExchangeCredentialsRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExchangeCredentialStateSnapshot> SetValidationStateAsync(SetExchangeCredentialValidationStateRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExchangeCredentialStateSnapshot> GetStateAsync(Guid exchangeAccountId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingAlertDispatchCoordinator : IAlertDispatchCoordinator
    {
        public List<CoinBot.Application.Abstractions.Alerts.AlertNotification> Notifications { get; } = [];

        public Task SendAsync(
            CoinBot.Application.Abstractions.Alerts.AlertNotification notification,
            string dedupeKey,
            TimeSpan cooldown,
            CancellationToken cancellationToken = default)
        {
            Notifications.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePrivateRestClient(ExchangeAccountSnapshot snapshot) : IBinancePrivateRestClient
    {
        public Task<BinanceOrderPlacementResult> PlaceOrderAsync(
            BinanceOrderPlacementRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<BinanceOrderStatusSnapshot> GetOrderAsync(
            BinanceOrderQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<BinanceOrderStatusSnapshot> CancelOrderAsync(
            BinanceOrderCancelRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string> StartListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task KeepAliveListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task CloseListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExchangeAccountSnapshot> GetAccountSnapshotAsync(
            Guid exchangeAccountId,
            string ownerUserId,
            string exchangeName,
            string apiKey,
            string apiSecret,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(snapshot);
        }
    }

    private static ExecutionOrder CreateFilledOrder(
        Guid exchangeAccountId,
        string ownerUserId,
        StrategySignalType signalType,
        ExecutionOrderSide side,
        decimal filledQuantity,
        decimal averageFillPrice,
        DateTime filledAtUtc,
        bool reduceOnly = false)
    {
        return new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = signalType,
            ExchangeAccountId = exchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            StrategyKey = "projection-test",
            Symbol = "SOLUSDT",
            Timeframe = "1m",
            BaseAsset = "SOL",
            QuoteAsset = "USDT",
            Side = side,
            OrderType = ExecutionOrderType.Market,
            Quantity = filledQuantity,
            Price = averageFillPrice,
            FilledQuantity = filledQuantity,
            AverageFillPrice = averageFillPrice,
            LastFilledAtUtc = filledAtUtc,
            ReduceOnly = reduceOnly,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            State = ExecutionOrderState.Filled,
            IdempotencyKey = $"projection_{Guid.NewGuid():N}",
            RootCorrelationId = "corr-projection-root",
            ExternalOrderId = Guid.NewGuid().ToString("N"),
            SubmittedAtUtc = filledAtUtc,
            SubmittedToBroker = true,
            ReconciliationStatus = ExchangeStateDriftStatus.InSync,
            ReconciliationSummary = "LocalState=Filled; ExchangeState=Filled; Source=Binance.PrivateRest.Order",
            LastStateChangedAtUtc = filledAtUtc,
            CreatedDate = filledAtUtc,
            UpdatedDate = filledAtUtc
        };
    }


    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "CoinBot.UnitTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
