using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.DemoPortfolio;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.DemoPortfolio;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.DemoPortfolio;

public sealed class DemoSessionServiceTests
{
    [Fact]
    public async Task EnsureActiveSessionAsync_BootstrapsExistingWalletState_WithoutCreatingDrift()
    {
        await using var harness = CreateHarness();
        harness.DbContext.DemoWallets.Add(new DemoWallet
        {
            OwnerUserId = "user-bootstrap",
            Asset = "USDT",
            AvailableBalance = 1000m,
            ReservedBalance = 0m,
            LastActivityAtUtc = At(0)
        });
        await harness.DbContext.SaveChangesAsync();

        var session = await harness.Service.EnsureActiveSessionAsync("user-bootstrap");
        var checkedSession = await harness.Service.RunConsistencyCheckAsync("user-bootstrap");
        var transaction = await harness.DbContext.DemoLedgerTransactions.SingleAsync(entity => entity.OwnerUserId == "user-bootstrap");

        Assert.Equal(1, session.SequenceNumber);
        Assert.Equal(DemoConsistencyStatus.InSync, checkedSession!.ConsistencyStatus);
        Assert.Equal(DemoLedgerTransactionType.SessionBootstrapped, transaction.TransactionType);
    }

    [Fact]
    public async Task RunConsistencyCheckAsync_DetectsWalletDrift_AndStoresSummary()
    {
        await using var harness = CreateHarness();
        harness.DbContext.DemoSessions.Add(new DemoSession
        {
            OwnerUserId = "user-drift",
            SequenceNumber = 1,
            SeedAsset = "USDT",
            SeedAmount = 1000m,
            State = DemoSessionState.Active,
            ConsistencyStatus = DemoConsistencyStatus.Unknown,
            StartedAtUtc = At(0)
        });
        harness.DbContext.DemoWallets.Add(new DemoWallet
        {
            OwnerUserId = "user-drift",
            Asset = "USDT",
            AvailableBalance = 1000m,
            ReservedBalance = 0m,
            LastActivityAtUtc = At(1)
        });
        await harness.DbContext.SaveChangesAsync();

        var session = await harness.Service.RunConsistencyCheckAsync("user-drift");
        var auditLog = await harness.DbContext.AuditLogs.SingleAsync(entity => entity.Action == "DemoSession.DriftDetected");

        Assert.NotNull(session);
        Assert.Equal(DemoConsistencyStatus.DriftDetected, session!.ConsistencyStatus);
        Assert.Contains("WalletMismatches=1", session.LastDriftSummary, StringComparison.Ordinal);
        Assert.Equal("Detected", auditLog.Outcome);
    }

    [Fact]
    public async Task ResetAsync_SeedsNewSession_ZeroesState_CancelsOpenOrders_AndWritesAudit()
    {
        await using var harness = CreateHarness();
        var botId = Guid.NewGuid();
        harness.DbContext.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = "user-reset",
            Name = "Demo Bot",
            StrategyKey = "demo-reset",
            IsEnabled = true,
            OpenOrderCount = 1,
            OpenPositionCount = 1
        });
        harness.DbContext.DemoSessions.Add(new DemoSession
        {
            OwnerUserId = "user-reset",
            SequenceNumber = 1,
            SeedAsset = "USDT",
            SeedAmount = 2500m,
            State = DemoSessionState.Active,
            ConsistencyStatus = DemoConsistencyStatus.InSync,
            StartedAtUtc = At(0)
        });
        harness.DbContext.DemoWallets.AddRange(
            new DemoWallet
            {
                OwnerUserId = "user-reset",
                Asset = "USDT",
                AvailableBalance = 400m,
                ReservedBalance = 100m,
                LastActivityAtUtc = At(1)
            },
            new DemoWallet
            {
                OwnerUserId = "user-reset",
                Asset = "BTC",
                AvailableBalance = 0.01m,
                ReservedBalance = 0m,
                LastActivityAtUtc = At(1)
            });
        harness.DbContext.DemoPositions.Add(new DemoPosition
        {
            OwnerUserId = "user-reset",
            BotId = botId,
            PositionScopeKey = "bot:reset",
            Symbol = "BTCUSDT",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Quantity = 0.01m,
            CostBasis = 500m,
            AverageEntryPrice = 50000m,
            RealizedPnl = 10m,
            UnrealizedPnl = 5m,
            TotalFeesInQuote = 1m,
            LastMarkPrice = 50500m
        });
        harness.DbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            OwnerUserId = "user-reset",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            BotId = botId,
            StrategyKey = "demo-reset",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Limit,
            Quantity = 0.01m,
            Price = 50000m,
            ExecutionEnvironment = ExecutionEnvironment.Demo,
            ExecutorKind = ExecutionOrderExecutorKind.Virtual,
            State = ExecutionOrderState.Submitted,
            IdempotencyKey = "reset-open-order",
            RootCorrelationId = "corr-reset-order",
            SubmittedAtUtc = At(1),
            LastStateChangedAtUtc = At(1)
        });
        await harness.DbContext.SaveChangesAsync();

        var session = await harness.Service.ResetAsync(
            new DemoSessionResetRequest(
                "user-reset",
                ExecutionEnvironment.Demo,
                Actor: "admin-reset",
                Reason: "Support reset",
                CorrelationId: "corr-reset-1"));

        var sessions = await harness.DbContext.DemoSessions
            .IgnoreQueryFilters()
            .OrderBy(entity => entity.SequenceNumber)
            .ToListAsync();
        var wallets = await harness.DbContext.DemoWallets
            .IgnoreQueryFilters()
            .Where(entity => entity.OwnerUserId == "user-reset")
            .OrderBy(entity => entity.Asset)
            .ToListAsync();
        var position = await harness.DbContext.DemoPositions
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.OwnerUserId == "user-reset");
        var order = await harness.DbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.OwnerUserId == "user-reset");
        var bot = await harness.DbContext.TradingBots
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.Id == botId);
        var auditLog = await harness.DbContext.AuditLogs.SingleAsync(entity => entity.Action == "DemoSession.ResetApplied");

        Assert.Equal(2, session.SequenceNumber);
        Assert.Equal(DemoConsistencyStatus.InSync, session.ConsistencyStatus);
        Assert.Equal(DemoSessionState.Closed, sessions[0].State);
        Assert.Equal(DemoSessionState.Active, sessions[1].State);
        Assert.Equal(10000m, wallets.Single(wallet => wallet.Asset == "USDT").AvailableBalance);
        Assert.Equal(0m, wallets.Single(wallet => wallet.Asset == "USDT").ReservedBalance);
        Assert.Equal(0m, wallets.Single(wallet => wallet.Asset == "BTC").AvailableBalance);
        Assert.Equal(0m, position.Quantity);
        Assert.Equal(ExecutionOrderState.Cancelled, order.State);
        Assert.Equal("DemoSessionReset", order.FailureCode);
        Assert.Equal(0, bot.OpenOrderCount);
        Assert.Equal(0, bot.OpenPositionCount);
        Assert.Equal("Applied", auditLog.Outcome);
        Assert.Equal(nameof(ExecutionEnvironment.Demo), auditLog.Environment);
    }

    private static DateTime At(int minuteOffset)
    {
        return new DateTime(2026, 3, 22, 12, minuteOffset, 0, DateTimeKind.Utc);
    }

    private static TestHarness CreateHarness()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(At(0)));
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var auditLogService = new AuditLogService(dbContext, new CorrelationContextAccessor());
        var marketDataService = new FakeMarketDataService();
        var demoWalletValuationService = new DemoWalletValuationService(
            marketDataService,
            timeProvider,
            NullLogger<DemoWalletValuationService>.Instance);
        var service = new DemoSessionService(
            dbContext,
            new DemoConsistencyWatchdogService(
                dbContext,
                Options.Create(new DemoSessionOptions()),
                timeProvider,
                NullLogger<DemoConsistencyWatchdogService>.Instance),
            demoWalletValuationService,
            auditLogService,
            Options.Create(new DemoSessionOptions()),
            timeProvider,
            NullLogger<DemoSessionService>.Instance);

        return new TestHarness(dbContext, service, auditLogService);
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeMarketDataService : IMarketDataService
    {
        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<MarketPriceSnapshot?>(null);
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(null);
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class TestHarness(
        ApplicationDbContext dbContext,
        DemoSessionService service,
        IAuditLogService auditLogService) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public DemoSessionService Service { get; } = service;

        public IAuditLogService AuditLogService { get; } = auditLogService;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }
    }
}
