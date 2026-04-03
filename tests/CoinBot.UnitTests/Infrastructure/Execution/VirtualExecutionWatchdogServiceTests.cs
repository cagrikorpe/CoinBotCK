using System.Runtime.CompilerServices;
using CoinBot.Application.Abstractions.Alerts;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.DemoPortfolio;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.DemoPortfolio;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Execution;

public sealed class VirtualExecutionWatchdogServiceTests
{
    [Fact]
    public async Task RunOnceAsync_CompletesTriggeredSubmittedVirtualLimitOrder_AndUpdatesBalances()
    {
        await using var harness = CreateHarness();
        await SeedDemoWalletAsync(harness.DbContext, "user-limit-watchdog", "USDT", 1000m);
        harness.MarketDataService.SetLatestPrice("AAVEUSDT", 105m, harness.TimeProvider.GetUtcNow().UtcDateTime, "unit-test");
        harness.MarketDataService.SetSymbolMetadata("AAVEUSDT", "AAVE", "USDT", 0.01m, 0.001m);
        await PrimeFreshMarketDataAsync(harness, "AAVEUSDT", "corr-vw-limit-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-vw-limit",
            context: "Open demo execution",
            correlationId: "corr-vw-limit-2");

        var dispatchResult = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-limit-watchdog",
                strategyKey: "watchdog-limit",
                isDemo: true) with
            {
                Symbol = "AAVEUSDT",
                BaseAsset = "AAVE",
                QuoteAsset = "USDT",
                Quantity = 1m,
                Price = 100m,
                OrderType = ExecutionOrderType.Limit
            },
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Submitted, dispatchResult.Order.State);

        harness.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        harness.MarketDataService.SetLatestPrice("AAVEUSDT", 99m, harness.TimeProvider.GetUtcNow().UtcDateTime, "unit-test");

        var cycleResult = await harness.WatchdogService.RunOnceAsync();
        var order = await harness.DbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.Id == dispatchResult.Order.ExecutionOrderId);
        var usdtWallet = await harness.DbContext.DemoWallets.SingleAsync(entity => entity.OwnerUserId == "user-limit-watchdog" && entity.Asset == "USDT");
        var aaveWallet = await harness.DbContext.DemoWallets.SingleAsync(entity => entity.OwnerUserId == "user-limit-watchdog" && entity.Asset == "AAVE");
        var position = await harness.DbContext.DemoPositions.SingleAsync(entity => entity.OwnerUserId == "user-limit-watchdog" && entity.Symbol == "AAVEUSDT");

        Assert.Equal(1, cycleResult.AdvancedOrderCount);
        Assert.Equal(ExecutionOrderState.Filled, order.State);
        Assert.Equal(1m, order.FilledQuantity);
        Assert.Equal(899.92m, usdtWallet.AvailableBalance);
        Assert.Equal(0m, usdtWallet.ReservedBalance);
        Assert.Equal(1m, aaveWallet.AvailableBalance);
        Assert.Equal(1m, position.Quantity);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task RunOnceAsync_TriggersTakeProfitClose_ForFilledDemoSpotPosition()
    {
        await using var harness = CreateHarness();
        await SeedDemoWalletAsync(harness.DbContext, "user-protect-watchdog", "USDT", 1000m);
        harness.MarketDataService.SetLatestPrice("BTCUSDT", 100m, harness.TimeProvider.GetUtcNow().UtcDateTime, "unit-test");
        harness.MarketDataService.SetSymbolMetadata("BTCUSDT", "BTC", "USDT", 0.01m, 0.001m);
        await PrimeFreshMarketDataAsync(harness, "BTCUSDT", "corr-vw-protect-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-vw-protect",
            context: "Open demo execution",
            correlationId: "corr-vw-protect-2");

        var entryResult = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-protect-watchdog",
                strategyKey: "watchdog-protect",
                isDemo: true) with
            {
                Symbol = "BTCUSDT",
                BaseAsset = "BTC",
                QuoteAsset = "USDT",
                Quantity = 1m,
                Price = 100m,
                StopLossPrice = 95m,
                TakeProfitPrice = 110m
            },
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Filled, entryResult.Order.State);

        harness.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        await PrimeFreshMarketDataAsync(harness, "BTCUSDT", "corr-vw-protect-3");
        harness.MarketDataService.SetLatestPrice("BTCUSDT", 112m, harness.TimeProvider.GetUtcNow().UtcDateTime, "unit-test");

        var cycleResult = await harness.WatchdogService.RunOnceAsync();
        var orders = await harness.DbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .Where(entity => entity.OwnerUserId == "user-protect-watchdog")
            .OrderBy(entity => entity.CreatedDate)
            .ToListAsync();
        var protectiveCloseOrder = Assert.Single(orders, entity => entity.SignalType == StrategySignalType.Exit);
        var usdtWallet = await harness.DbContext.DemoWallets.SingleAsync(entity => entity.OwnerUserId == "user-protect-watchdog" && entity.Asset == "USDT");
        var btcWallet = await harness.DbContext.DemoWallets.SingleAsync(entity => entity.OwnerUserId == "user-protect-watchdog" && entity.Asset == "BTC");
        var position = await harness.DbContext.DemoPositions.SingleAsync(entity => entity.OwnerUserId == "user-protect-watchdog" && entity.Symbol == "BTCUSDT");

        Assert.Equal(1, cycleResult.ProtectiveDispatchCount);
        Assert.Equal(2, orders.Count);
        Assert.Equal(ExecutionEnvironment.Demo, protectiveCloseOrder.ExecutionEnvironment);
        Assert.Equal(ExecutionOrderExecutorKind.Virtual, protectiveCloseOrder.ExecutorKind);
        Assert.Equal(ExecutionOrderSide.Sell, protectiveCloseOrder.Side);
        Assert.Equal(ExecutionOrderState.Filled, protectiveCloseOrder.State);
        Assert.Equal(0m, position.Quantity);
        Assert.Equal(0m, btcWallet.AvailableBalance);
        Assert.Equal(0m, btcWallet.ReservedBalance);
        Assert.True(usdtWallet.AvailableBalance > 1000m);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    private static TestHarness CreateHarness()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 23, 12, 0, 0, TimeSpan.Zero));
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var correlationContextAccessor = new CorrelationContextAccessor();
        var auditLogService = new AuditLogService(dbContext, correlationContextAccessor);
        var switchService = new GlobalExecutionSwitchService(dbContext, auditLogService);
        var globalSystemStateService = new GlobalSystemStateService(dbContext, auditLogService, timeProvider);
        var marketDataService = new FakeMarketDataService();
        var demoWalletValuationService = new DemoWalletValuationService(
            marketDataService,
            timeProvider,
            NullLogger<DemoWalletValuationService>.Instance);
        var circuitBreaker = new DataLatencyCircuitBreaker(
            dbContext,
            new FakeAlertService(),
            Options.Create(new DataLatencyGuardOptions()),
            timeProvider,
            NullLogger<DataLatencyCircuitBreaker>.Instance);
        var tradingModeService = new TradingModeService(dbContext, auditLogService);
        var demoSessionService = new DemoSessionService(
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
        var executionGate = new ExecutionGate(
            demoSessionService,
            globalSystemStateService,
            switchService,
            circuitBreaker,
            tradingModeService,
            auditLogService,
            NullLogger<ExecutionGate>.Instance);
        var demoPortfolioAccountingService = new DemoPortfolioAccountingService(
            dbContext,
            demoSessionService,
            demoWalletValuationService,
            timeProvider,
            NullLogger<DemoPortfolioAccountingService>.Instance);
        var demoFillSimulator = new DemoFillSimulator(
            marketDataService,
            Options.Create(new DemoFillSimulatorOptions()),
            timeProvider,
            NullLogger<DemoFillSimulator>.Instance);
        var traceService = new TraceService(
            dbContext,
            correlationContextAccessor,
            timeProvider);
        var userExecutionOverrideGuard = new UserExecutionOverrideGuard(
            dbContext,
            tradingModeService);
        var credentialService = new FakeExchangeCredentialService();
        var privateRestClient = new FakePrivateRestClient(timeProvider);
        var engine = new ExecutionEngine(
            dbContext,
            executionGate,
            tradingModeService,
            traceService,
            userExecutionOverrideGuard,
            correlationContextAccessor,
            demoPortfolioAccountingService,
            demoFillSimulator,
            new VirtualExecutor(timeProvider, NullLogger<VirtualExecutor>.Instance),
            new BinanceExecutor(
                dbContext,
                credentialService,
                privateRestClient,
                NullLogger<BinanceExecutor>.Instance),
            timeProvider,
            NullLogger<ExecutionEngine>.Instance);
        var watchdogService = new VirtualExecutionWatchdogService(
            dbContext,
            demoPortfolioAccountingService,
            engine,
            marketDataService,
            Options.Create(new DemoFillSimulatorOptions()),
            demoFillSimulator,
            timeProvider,
            NullLogger<VirtualExecutionWatchdogService>.Instance);

        return new TestHarness(
            dbContext,
            switchService,
            circuitBreaker,
            engine,
            watchdogService,
            timeProvider,
            marketDataService,
            privateRestClient);
    }

    private static async Task PrimeFreshMarketDataAsync(TestHarness harness, string symbol, string correlationId)
    {
        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                $"binance-{symbol.Trim().ToLowerInvariant()}",
                harness.TimeProvider.GetUtcNow().UtcDateTime,
                Symbol: symbol,
                Timeframe: "1m"),
            correlationId);
    }

    private static async Task SeedDemoWalletAsync(
        ApplicationDbContext dbContext,
        string ownerUserId,
        string asset,
        decimal availableBalance)
    {
        dbContext.DemoWallets.Add(new DemoWallet
        {
            OwnerUserId = ownerUserId,
            Asset = asset,
            AvailableBalance = availableBalance,
            ReservedBalance = 0m,
            LastActivityAtUtc = new DateTime(2026, 3, 23, 12, 0, 0, DateTimeKind.Utc)
        });

        await dbContext.SaveChangesAsync();
    }

    private static ExecutionCommand CreateCommand(
        string ownerUserId,
        string strategyKey,
        bool? isDemo)
    {
        return new ExecutionCommand(
            Actor: "worker-exec",
            OwnerUserId: ownerUserId,
            TradingStrategyId: Guid.NewGuid(),
            TradingStrategyVersionId: Guid.NewGuid(),
            StrategySignalId: Guid.NewGuid(),
            SignalType: StrategySignalType.Entry,
            StrategyKey: strategyKey,
            Symbol: "BTCUSDT",
            Timeframe: "1m",
            BaseAsset: "BTC",
            QuoteAsset: "USDT",
            Side: ExecutionOrderSide.Buy,
            OrderType: ExecutionOrderType.Market,
            Quantity: 0.05m,
            Price: 65000m,
            BotId: null,
            ExchangeAccountId: null,
            IsDemo: isDemo,
            CorrelationId: "root-correlation-1",
            ParentCorrelationId: "signal-correlation-1",
            Context: "VirtualExecutionWatchdogServiceTests");
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeAlertService : IAlertService
    {
        public Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeExchangeCredentialService : IExchangeCredentialService
    {
        public Task<ExchangeCredentialStateSnapshot> StoreAsync(
            StoreExchangeCredentialsRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExchangeCredentialAccessResult> GetAsync(
            ExchangeCredentialAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExchangeCredentialStateSnapshot> SetValidationStateAsync(
            SetExchangeCredentialValidationStateRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExchangeCredentialStateSnapshot> GetStateAsync(
            Guid exchangeAccountId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakePrivateRestClient(TimeProvider timeProvider) : IBinancePrivateRestClient
    {
        public int PlaceOrderCalls { get; private set; }

        public Task<BinanceOrderPlacementResult> PlaceOrderAsync(
            BinanceOrderPlacementRequest request,
            CancellationToken cancellationToken = default)
        {
            PlaceOrderCalls++;

            return Task.FromResult(
                new BinanceOrderPlacementResult(
                    $"binance-order-{PlaceOrderCalls}",
                    request.ClientOrderId,
                    timeProvider.GetUtcNow().UtcDateTime));
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
            throw new NotSupportedException();
        }
    }

    private sealed class FakeMarketDataService : IMarketDataService
    {
        private readonly Dictionary<string, MarketPriceSnapshot> prices = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SymbolMetadataSnapshot> metadata = new(StringComparer.Ordinal);

        public void SetLatestPrice(string symbol, decimal price, DateTime observedAtUtc, string source)
        {
            prices[symbol] = new MarketPriceSnapshot(symbol, price, observedAtUtc, observedAtUtc, source);
        }

        public void SetSymbolMetadata(string symbol, string baseAsset, string quoteAsset, decimal tickSize, decimal stepSize)
        {
            metadata[symbol] = new SymbolMetadataSnapshot(
                symbol,
                "Binance",
                baseAsset,
                quoteAsset,
                tickSize,
                stepSize,
                "TRADING",
                true,
                DateTime.UtcNow);
        }

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
            prices.TryGetValue(symbol, out var snapshot);
            return ValueTask.FromResult<MarketPriceSnapshot?>(snapshot);
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            metadata.TryGetValue(symbol, out var snapshot);
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(snapshot);
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class TestHarness(
        ApplicationDbContext dbContext,
        IGlobalExecutionSwitchService switchService,
        IDataLatencyCircuitBreaker circuitBreaker,
        IExecutionEngine engine,
        VirtualExecutionWatchdogService watchdogService,
        AdjustableTimeProvider timeProvider,
        FakeMarketDataService marketDataService,
        FakePrivateRestClient privateRestClient) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public IGlobalExecutionSwitchService SwitchService { get; } = switchService;

        public IDataLatencyCircuitBreaker CircuitBreaker { get; } = circuitBreaker;

        public IExecutionEngine Engine { get; } = engine;

        public VirtualExecutionWatchdogService WatchdogService { get; } = watchdogService;

        public AdjustableTimeProvider TimeProvider { get; } = timeProvider;

        public FakeMarketDataService MarketDataService { get; } = marketDataService;

        public FakePrivateRestClient PrivateRestClient { get; } = privateRestClient;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }
    }
}
