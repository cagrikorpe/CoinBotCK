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
    public async Task ExchangeAppStateSyncService_ProjectsMissingOpenPosition_FromFilledExecutionOrders()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        var exchangeAccountId = Guid.NewGuid();
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

        await using var enumerator = snapshotHub.SubscribeAsync().GetAsyncEnumerator();
        var moveNextTask = enumerator.MoveNextAsync().AsTask();

        await service.RunOnceAsync();

        Assert.True(await moveNextTask);
        var publishedSnapshot = enumerator.Current;
        var projectedPosition = Assert.Single(publishedSnapshot.Positions);
        Assert.Equal("SOLUSDT", projectedPosition.Symbol);
        Assert.Equal("BOTH", projectedPosition.PositionSide);
        Assert.Equal(0.06m, projectedPosition.Quantity);
        Assert.Equal(83.54m, projectedPosition.EntryPrice);
        Assert.Contains("Fallback=LocalExecutionProjection", publishedSnapshot.Source, StringComparison.Ordinal);
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

