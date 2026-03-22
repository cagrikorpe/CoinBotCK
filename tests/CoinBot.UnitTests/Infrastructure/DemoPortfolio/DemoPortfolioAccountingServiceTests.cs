using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.DemoPortfolio;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.DemoPortfolio;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoinBot.UnitTests.Infrastructure.DemoPortfolio;

public sealed class DemoPortfolioAccountingServiceTests
{
    [Fact]
    public async Task ReserveFillReleaseAndMarkUpdate_KeepWalletPositionAndLedgerConsistent()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var harness = CreateHarness(databaseRoot, "user-demo");
        var bot = await harness.AddBotAsync();

        await harness.Service.SeedWalletAsync(
            new DemoWalletSeedRequest(
                "user-demo",
                ExecutionEnvironment.Demo,
                "seed-1",
                "USDT",
                10000m,
                At(0)));
        await harness.Service.ReserveFundsAsync(
            new DemoFundsReservationRequest(
                "user-demo",
                ExecutionEnvironment.Demo,
                "reserve-buy-1",
                "USDT",
                3000m,
                "ord-1",
                At(1)));
        await harness.Service.ApplyFillAsync(
            new DemoFillAccountingRequest(
                "user-demo",
                ExecutionEnvironment.Demo,
                "fill-buy-1",
                "BTCUSDT",
                "BTC",
                "USDT",
                DemoTradeSide.Buy,
                0.05m,
                50000m,
                2502.5m,
                bot.Id,
                "ord-1",
                "fill-1",
                "USDT",
                2.5m,
                FeeAmountInQuote: null,
                MarkPrice: 50000m,
                OccurredAtUtc: At(2)));
        var release = await harness.Service.ReleaseFundsAsync(
            new DemoFundsReleaseRequest(
                "user-demo",
                ExecutionEnvironment.Demo,
                "release-buy-1",
                "USDT",
                497.5m,
                "ord-1",
                At(3)));
        var mark = await harness.Service.UpdateMarkPriceAsync(
            new DemoMarkPriceUpdateRequest(
                "user-demo",
                ExecutionEnvironment.Demo,
                "mark-1",
                "BTCUSDT",
                "BTC",
                "USDT",
                51000m,
                bot.Id,
                At(4)));
        await harness.Service.ReserveFundsAsync(
            new DemoFundsReservationRequest(
                "user-demo",
                ExecutionEnvironment.Demo,
                "reserve-sell-1",
                "BTC",
                0.02m,
                "ord-2",
                At(5)));
        var sell = await harness.Service.ApplyFillAsync(
            new DemoFillAccountingRequest(
                "user-demo",
                ExecutionEnvironment.Demo,
                "fill-sell-1",
                "BTCUSDT",
                "BTC",
                "USDT",
                DemoTradeSide.Sell,
                0.02m,
                51000m,
                0.02m,
                bot.Id,
                "ord-2",
                "fill-2",
                "USDT",
                1.02m,
                FeeAmountInQuote: null,
                MarkPrice: 51000m,
                OccurredAtUtc: At(6)));

        var usdtWallet = await harness.Context.DemoWallets.SingleAsync(entity => entity.Asset == "USDT");
        var btcWallet = await harness.Context.DemoWallets.SingleAsync(entity => entity.Asset == "BTC");
        var position = await harness.Context.DemoPositions.SingleAsync(entity => entity.Symbol == "BTCUSDT");
        var botEntity = await harness.Context.TradingBots.SingleAsync(entity => entity.Id == bot.Id);
        var ledgerCount = await harness.Context.DemoLedgerTransactions.CountAsync();
        var portfolioProfit = usdtWallet.AvailableBalance + (btcWallet.AvailableBalance * 51000m) - 10000m;

        Assert.Equal(7497.5m, release.Wallets.Single().AvailableBalance);
        Assert.Equal(0m, release.Wallets.Single().ReservedBalance);
        Assert.Equal(47.5m, mark.Position!.UnrealizedPnl);
        Assert.Equal(17.98m, sell.Transaction.RealizedPnlDelta);
        Assert.Equal(8516.48m, usdtWallet.AvailableBalance);
        Assert.Equal(0m, usdtWallet.ReservedBalance);
        Assert.Equal(0.03m, btcWallet.AvailableBalance);
        Assert.Equal(0m, btcWallet.ReservedBalance);
        Assert.Equal(0.03m, position.Quantity);
        Assert.Equal(1501.5m, position.CostBasis);
        Assert.Equal(50050m, position.AverageEntryPrice);
        Assert.Equal(17.98m, position.RealizedPnl);
        Assert.Equal(28.5m, position.UnrealizedPnl);
        Assert.Equal(3.52m, position.TotalFeesInQuote);
        Assert.Equal(1, botEntity.OpenPositionCount);
        Assert.Equal(7, ledgerCount);
        Assert.Equal(position.RealizedPnl + position.UnrealizedPnl, portfolioProfit);
    }

    [Fact]
    public async Task ApplyFillAsync_IsIdempotent_ByOperationId()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var harness = CreateHarness(databaseRoot, "user-replay");
        var bot = await harness.AddBotAsync();

        await harness.Service.SeedWalletAsync(
            new DemoWalletSeedRequest("user-replay", ExecutionEnvironment.Demo, "seed-1", "USDT", 1000m, At(0)));
        await harness.Service.ReserveFundsAsync(
            new DemoFundsReservationRequest("user-replay", ExecutionEnvironment.Demo, "reserve-1", "USDT", 101m, "ord-1", At(1)));

        var request = new DemoFillAccountingRequest(
            "user-replay",
            ExecutionEnvironment.Demo,
            "fill-dup-1",
            "AAVEUSDT",
            "AAVE",
            "USDT",
            DemoTradeSide.Buy,
            1m,
            100m,
            101m,
            bot.Id,
            "ord-1",
            "fill-1",
            "USDT",
            1m,
            FeeAmountInQuote: null,
            MarkPrice: 100m,
            OccurredAtUtc: At(2));

        var first = await harness.Service.ApplyFillAsync(request);
        var second = await harness.Service.ApplyFillAsync(request);

        var transactionCount = await harness.Context.DemoLedgerTransactions
            .CountAsync(entity => entity.OperationId == "fill-dup-1");
        var usdtWallet = await harness.Context.DemoWallets.SingleAsync(entity => entity.Asset == "USDT");
        var aaveWallet = await harness.Context.DemoWallets.SingleAsync(entity => entity.Asset == "AAVE");

        Assert.False(first.IsReplay);
        Assert.True(second.IsReplay);
        Assert.Equal(first.Transaction.TransactionId, second.Transaction.TransactionId);
        Assert.Equal(1, transactionCount);
        Assert.Equal(899m, usdtWallet.AvailableBalance);
        Assert.Equal(0m, usdtWallet.ReservedBalance);
        Assert.Equal(1m, aaveWallet.AvailableBalance);
    }

    [Fact]
    public async Task SellFill_WithBaseAssetFee_ClosesPositionWithoutDrift()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var harness = CreateHarness(databaseRoot, "user-base-fee");
        var bot = await harness.AddBotAsync();

        await harness.Service.SeedWalletAsync(
            new DemoWalletSeedRequest("user-base-fee", ExecutionEnvironment.Demo, "seed-1", "USDT", 2000m, At(0)));
        await harness.Service.ReserveFundsAsync(
            new DemoFundsReservationRequest("user-base-fee", ExecutionEnvironment.Demo, "reserve-buy-1", "USDT", 1010m, "ord-1", At(1)));
        await harness.Service.ApplyFillAsync(
            new DemoFillAccountingRequest(
                "user-base-fee",
                ExecutionEnvironment.Demo,
                "fill-buy-1",
                "ETHUSDT",
                "ETH",
                "USDT",
                DemoTradeSide.Buy,
                1.01m,
                1000m,
                1010m,
                bot.Id,
                "ord-1",
                "fill-1",
                FeeAsset: null,
                FeeAmount: 0m,
                FeeAmountInQuote: null,
                MarkPrice: 1000m,
                OccurredAtUtc: At(2)));
        await harness.Service.ReserveFundsAsync(
            new DemoFundsReservationRequest("user-base-fee", ExecutionEnvironment.Demo, "reserve-sell-1", "ETH", 1.01m, "ord-2", At(3)));
        await harness.Service.ApplyFillAsync(
            new DemoFillAccountingRequest(
                "user-base-fee",
                ExecutionEnvironment.Demo,
                "fill-sell-1",
                "ETHUSDT",
                "ETH",
                "USDT",
                DemoTradeSide.Sell,
                1m,
                1100m,
                1.01m,
                bot.Id,
                "ord-2",
                "fill-2",
                "ETH",
                0.01m,
                FeeAmountInQuote: null,
                MarkPrice: 1100m,
                OccurredAtUtc: At(4)));

        var usdtWallet = await harness.Context.DemoWallets.SingleAsync(entity => entity.Asset == "USDT");
        var ethWallet = await harness.Context.DemoWallets.SingleAsync(entity => entity.Asset == "ETH");
        var position = await harness.Context.DemoPositions.SingleAsync(entity => entity.Symbol == "ETHUSDT");
        var botEntity = await harness.Context.TradingBots.SingleAsync(entity => entity.Id == bot.Id);

        Assert.Equal(2090m, usdtWallet.AvailableBalance);
        Assert.Equal(0m, ethWallet.AvailableBalance);
        Assert.Equal(0m, ethWallet.ReservedBalance);
        Assert.Equal(0m, position.Quantity);
        Assert.Equal(0m, position.CostBasis);
        Assert.Equal(90m, position.RealizedPnl);
        Assert.Equal(0m, position.UnrealizedPnl);
        Assert.Equal(11m, position.TotalFeesInQuote);
        Assert.Equal(0, botEntity.OpenPositionCount);
    }

    [Fact]
    public async Task SeedWalletAsync_FailsClosed_WhenEnvironmentIsLive()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var harness = CreateHarness(databaseRoot, "user-live");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.SeedWalletAsync(
                new DemoWalletSeedRequest("user-live", ExecutionEnvironment.Live, "seed-live-1", "USDT", 100m, At(0))));

        Assert.Contains("Demo execution environment", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await harness.Context.DemoLedgerTransactions.ToListAsync());
        Assert.Empty(await harness.Context.DemoWallets.ToListAsync());
    }

    private static DateTime At(int minuteOffset)
    {
        return new DateTime(2026, 3, 22, 12, minuteOffset, 0, DateTimeKind.Utc);
    }

    private static TestHarness CreateHarness(InMemoryDatabaseRoot databaseRoot, string ownerUserId)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"), databaseRoot)
            .Options;
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(At(0)));
        var context = new ApplicationDbContext(options, new TestDataScopeContext(ownerUserId));
        var service = new DemoPortfolioAccountingService(
            context,
            timeProvider,
            NullLogger<DemoPortfolioAccountingService>.Instance);

        return new TestHarness(context, service, ownerUserId);
    }

    private sealed class TestDataScopeContext(string ownerUserId) : IDataScopeContext
    {
        public string? UserId => ownerUserId;

        public bool HasIsolationBypass => true;
    }

    private sealed class TestHarness(
        ApplicationDbContext context,
        DemoPortfolioAccountingService service,
        string ownerUserId) : IAsyncDisposable
    {
        public ApplicationDbContext Context { get; } = context;

        public DemoPortfolioAccountingService Service { get; } = service;

        public async Task<TradingBot> AddBotAsync()
        {
            var bot = new TradingBot
            {
                OwnerUserId = ownerUserId,
                Name = "Demo Bot",
                StrategyKey = "demo-strategy",
                IsEnabled = true
            };

            Context.TradingBots.Add(bot);
            await Context.SaveChangesAsync();
            return bot;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
        }
    }
}
