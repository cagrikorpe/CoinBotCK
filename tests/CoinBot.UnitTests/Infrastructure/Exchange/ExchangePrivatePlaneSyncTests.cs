using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
}
