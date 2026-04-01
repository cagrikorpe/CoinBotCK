using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Dashboard;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CoinBot.UnitTests.Infrastructure.Dashboard;

public sealed class UserDashboardPortfolioReadModelServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_ReturnsBalancesPositionsAndSyncSummary_ForActiveBinanceAccount()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var context = CreateContext(databaseRoot);
        var exchangeAccountId = Guid.NewGuid();
        var syncedAtUtc = new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

        context.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = "user-dashboard-01",
            ExchangeName = "Binance",
            DisplayName = "Primary",
            CredentialStatus = ExchangeCredentialStatus.Active,
            ApiKeyCiphertext = "cipher-api-key",
            ApiSecretCiphertext = "cipher-api-secret"
        });
        context.ExchangeBalances.Add(new ExchangeBalance
        {
            OwnerUserId = "user-dashboard-01",
            ExchangeAccountId = exchangeAccountId,
            Asset = "USDT",
            WalletBalance = 1250m,
            CrossWalletBalance = 1240m,
            AvailableBalance = 1100m,
            MaxWithdrawAmount = 1000m,
            ExchangeUpdatedAtUtc = syncedAtUtc,
            SyncedAtUtc = syncedAtUtc
        });
        context.ExchangePositions.Add(new ExchangePosition
        {
            OwnerUserId = "user-dashboard-01",
            ExchangeAccountId = exchangeAccountId,
            Symbol = "BTCUSDT",
            PositionSide = "LONG",
            Quantity = 0.25m,
            EntryPrice = 65000m,
            BreakEvenPrice = 65010m,
            UnrealizedProfit = 35m,
            MarginType = "cross",
            IsolatedWallet = 0m,
            ExchangeUpdatedAtUtc = syncedAtUtc,
            SyncedAtUtc = syncedAtUtc
        });
        context.ExchangeAccountSyncStates.Add(new ExchangeAccountSyncState
        {
            OwnerUserId = "user-dashboard-01",
            ExchangeAccountId = exchangeAccountId,
            PrivateStreamConnectionState = ExchangePrivateStreamConnectionState.Connected,
            DriftStatus = ExchangeStateDriftStatus.InSync,
            LastPrivateStreamEventAtUtc = syncedAtUtc,
            LastBalanceSyncedAtUtc = syncedAtUtc,
            LastPositionSyncedAtUtc = syncedAtUtc
        });
        await context.SaveChangesAsync();

        var service = new UserDashboardPortfolioReadModelService(context);

        var snapshot = await service.GetSnapshotAsync("user-dashboard-01");

        Assert.Equal(1, snapshot.ActiveAccountCount);
        Assert.Equal("Canli senkron bagli", snapshot.SyncStatusLabel);
        Assert.Equal("positive", snapshot.SyncStatusTone);
        Assert.Equal(syncedAtUtc, snapshot.LastSynchronizedAtUtc);
        Assert.Single(snapshot.Balances);
        Assert.Single(snapshot.Positions);
        Assert.Equal("USDT", snapshot.Balances.Single().Asset);
        Assert.Equal("BTCUSDT", snapshot.Positions.Single().Symbol);
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
}
