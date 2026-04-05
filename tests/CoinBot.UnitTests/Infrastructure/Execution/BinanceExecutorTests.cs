using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoinBot.UnitTests.Infrastructure.Execution;

public sealed class BinanceExecutorTests
{
    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenQuantityIsBelowMinQuantity()
    {
        await using var harness = await CreateHarnessAsync();

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                Quantity = 0.0005m,
                Price = 65000m
            },
            CancellationToken.None));

        Assert.Equal("OrderQuantityBelowMinimum", exception.ReasonCode);
        Assert.Contains("minimum quantity", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenQuantityViolatesStepSize()
    {
        await using var harness = await CreateHarnessAsync();

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                Quantity = 0.0015m,
                Price = 65000m
            },
            CancellationToken.None));

        Assert.Equal("OrderQuantityStepSizeMismatch", exception.ReasonCode);
        Assert.Contains("step size", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenQuantityViolatesQuantityPrecision()
    {
        await using var harness = await CreateHarnessAsync(
            metadata: CreateMetadata(stepSize: 0.00001m, quantityPrecision: 3));

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                Quantity = 0.00155m,
                Price = 65000m
            },
            CancellationToken.None));

        Assert.Equal("OrderQuantityPrecisionExceeded", exception.ReasonCode);
        Assert.Contains("quantity precision", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenNotionalIsBelowMinimum()
    {
        await using var harness = await CreateHarnessAsync();

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                Quantity = 0.001m,
                Price = 65000m
            },
            CancellationToken.None));

        Assert.Equal("OrderNotionalBelowMinimum", exception.ReasonCode);
        Assert.Contains("minimum notional", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenLimitPriceViolatesTickSize()
    {
        await using var harness = await CreateHarnessAsync();

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                OrderType = ExecutionOrderType.Limit,
                Quantity = 0.002m,
                Price = 65000.15m
            },
            CancellationToken.None));

        Assert.Equal("LimitPriceTickSizeMismatch", exception.ReasonCode);
        Assert.Contains("tick size", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenLimitPriceViolatesPricePrecision()
    {
        await using var harness = await CreateHarnessAsync(
            metadata: CreateMetadata(tickSize: 0.001m, pricePrecision: 2));

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                OrderType = ExecutionOrderType.Limit,
                Quantity = 0.002m,
                Price = 65000.123m
            },
            CancellationToken.None));

        Assert.Equal("LimitPricePrecisionExceeded", exception.ReasonCode);
        Assert.Contains("price precision", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenTradingIsDisabled()
    {
        await using var harness = await CreateHarnessAsync(
            metadata: CreateMetadata(isTradingEnabled: false, tradingStatus: "BREAK"));

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                Quantity = 0.002m,
                Price = 65000m
            },
            CancellationToken.None));

        Assert.Equal("SymbolTradingDisabled", exception.ReasonCode);
        Assert.Contains("not trading-enabled", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenMetadataIsUnavailable()
    {
        await using var harness = await CreateHarnessAsync(
            marketDataService: new NullMarketDataService(),
            exchangeInfoClient: new FakeExchangeInfoClient(null));

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                Quantity = 0.002m,
                Price = 65000m
            },
            CancellationToken.None));

        Assert.Equal("SymbolMetadataUnavailable", exception.ReasonCode);
        Assert.Contains("metadata", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, harness.ExchangeInfoClient.CallCount);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_PlacesOrder_WhenMetadataIsValid()
    {
        await using var harness = await CreateHarnessAsync(
            marketDataService: new NullMarketDataService(),
            exchangeInfoClient: new FakeExchangeInfoClient(CreateMetadata()));

        var result = await harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                Quantity = 0.002m,
                Price = 65000m
            },
            CancellationToken.None);

        Assert.Equal(1, harness.ExchangeInfoClient.CallCount);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal("binance-order-1", result.ExternalOrderId);
    }

    private static async Task<TestHarness> CreateHarnessAsync(
        SymbolMetadataSnapshot? metadata = null,
        IMarketDataService? marketDataService = null,
        FakeExchangeInfoClient? exchangeInfoClient = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var exchangeAccountId = Guid.NewGuid();

        dbContext.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = "user-exec",
            ExchangeName = "Binance",
            DisplayName = "Binance Futures",
            IsReadOnly = false,
            CredentialStatus = ExchangeCredentialStatus.Active
        });

        await dbContext.SaveChangesAsync();

        var order = new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-exec",
            ExchangeAccountId = exchangeAccountId,
            StrategyKey = "pilot-core",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.002m,
            Price = 65000m,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            IdempotencyKey = "exec-test",
            RootCorrelationId = "corr-exec-test",
            LastStateChangedAtUtc = new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc)
        };

        var resolvedMetadata = metadata ?? CreateMetadata();
        var resolvedMarketDataService = marketDataService ?? new FakeMarketDataService(resolvedMetadata);
        var resolvedExchangeInfoClient = exchangeInfoClient ?? new FakeExchangeInfoClient(resolvedMetadata);
        var privateRestClient = new FakePrivateRestClient();
        var executor = new BinanceExecutor(
            dbContext,
            new FakeExchangeCredentialService(),
            privateRestClient,
            NullLogger<BinanceExecutor>.Instance,
            marketDataService: resolvedMarketDataService,
            exchangeInfoClient: resolvedExchangeInfoClient);

        return new TestHarness(
            dbContext,
            executor,
            privateRestClient,
            resolvedExchangeInfoClient,
            exchangeAccountId,
            order);
    }

    private static SymbolMetadataSnapshot CreateMetadata(
        decimal tickSize = 0.1m,
        decimal stepSize = 0.001m,
        decimal? minQuantity = 0.001m,
        decimal? minNotional = 100m,
        int? pricePrecision = 1,
        int? quantityPrecision = 3,
        bool isTradingEnabled = true,
        string tradingStatus = "TRADING")
    {
        return new SymbolMetadataSnapshot(
            "BTCUSDT",
            "Binance",
            "BTC",
            "USDT",
            tickSize,
            stepSize,
            tradingStatus,
            isTradingEnabled,
            new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc))
        {
            MinQuantity = minQuantity,
            MinNotional = minNotional,
            PricePrecision = pricePrecision,
            QuantityPrecision = quantityPrecision
        };
    }

    private static ExecutionCommand CreateCommand(Guid exchangeAccountId)
    {
        return new ExecutionCommand(
            Actor: "system:bot-worker",
            OwnerUserId: "user-exec",
            TradingStrategyId: Guid.NewGuid(),
            TradingStrategyVersionId: Guid.NewGuid(),
            StrategySignalId: Guid.NewGuid(),
            SignalType: StrategySignalType.Entry,
            StrategyKey: "pilot-core",
            Symbol: "BTCUSDT",
            Timeframe: "1m",
            BaseAsset: "BTC",
            QuoteAsset: "USDT",
            Side: ExecutionOrderSide.Buy,
            OrderType: ExecutionOrderType.Market,
            Quantity: 0.002m,
            Price: 65000m,
            ExchangeAccountId: exchangeAccountId,
            IsDemo: false,
            CorrelationId: "corr-exec-test",
            Context: "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1");
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
            return Task.FromResult(
                new ExchangeCredentialAccessResult(
                    "api-key",
                    "api-secret",
                    new ExchangeCredentialStateSnapshot(
                        request.ExchangeAccountId,
                        ExchangeCredentialStatus.Active,
                        "fingerprint",
                        "v1",
                        StoredAtUtc: new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
                        LastValidatedAtUtc: new DateTime(2026, 4, 2, 11, 0, 0, DateTimeKind.Utc),
                        LastAccessedAtUtc: new DateTime(2026, 4, 2, 11, 5, 0, DateTimeKind.Utc),
                        LastRotatedAtUtc: new DateTime(2026, 4, 2, 9, 0, 0, DateTimeKind.Utc),
                        RevalidateAfterUtc: new DateTime(2026, 5, 2, 11, 0, 0, DateTimeKind.Utc),
                        RotateAfterUtc: new DateTime(2026, 7, 2, 11, 0, 0, DateTimeKind.Utc))));
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

    private sealed class FakePrivateRestClient : IBinancePrivateRestClient
    {
        public int PlaceOrderCalls { get; private set; }

        public Task EnsureMarginTypeAsync(
            Guid exchangeAccountId,
            string symbol,
            string marginType,
            string apiKey,
            string apiSecret,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task EnsureLeverageAsync(
            Guid exchangeAccountId,
            string symbol,
            decimal leverage,
            string apiKey,
            string apiSecret,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<BinanceOrderPlacementResult> PlaceOrderAsync(
            BinanceOrderPlacementRequest request,
            CancellationToken cancellationToken = default)
        {
            PlaceOrderCalls++;
            return Task.FromResult(new BinanceOrderPlacementResult($"binance-order-{PlaceOrderCalls}", request.ClientOrderId, DateTime.UtcNow));
        }

        public Task<BinanceOrderStatusSnapshot> CancelOrderAsync(BinanceOrderCancelRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<BinanceOrderStatusSnapshot> GetOrderAsync(BinanceOrderQueryRequest request, CancellationToken cancellationToken = default)
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

    private sealed class FakeMarketDataService(SymbolMetadataSnapshot metadata) : IMarketDataService
    {
        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<MarketPriceSnapshot?>(new MarketPriceSnapshot(
                "BTCUSDT",
                65000m,
                DateTime.UtcNow,
                DateTime.UtcNow,
                "UnitTest"));
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(metadata);
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }
    }

    private sealed class NullMarketDataService : IMarketDataService
    {
        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<MarketPriceSnapshot?>(null);

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<SymbolMetadataSnapshot?>(null);

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }
    }

    private sealed class FakeExchangeInfoClient(SymbolMetadataSnapshot? metadata) : IBinanceExchangeInfoClient
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyCollection<SymbolMetadataSnapshot>> GetSymbolMetadataAsync(
            IReadOnlyCollection<string> symbols,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyCollection<SymbolMetadataSnapshot>>(
                metadata is null ? [] : [metadata]);
        }

        public Task<DateTime?> GetServerTimeUtcAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<DateTime?>(DateTime.UtcNow);
        }
    }

    private sealed class TestDataScopeContext : CoinBot.Application.Abstractions.DataScope.IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class TestHarness(
        ApplicationDbContext dbContext,
        BinanceExecutor executor,
        FakePrivateRestClient privateRestClient,
        FakeExchangeInfoClient exchangeInfoClient,
        Guid exchangeAccountId,
        ExecutionOrder order) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public BinanceExecutor Executor { get; } = executor;

        public FakePrivateRestClient PrivateRestClient { get; } = privateRestClient;

        public FakeExchangeInfoClient ExchangeInfoClient { get; } = exchangeInfoClient;

        public Guid ExchangeAccountId { get; } = exchangeAccountId;

        public ExecutionOrder Order { get; } = order;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }
    }
}
