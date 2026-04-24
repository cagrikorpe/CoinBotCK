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

public sealed class BinanceSpotExecutorTests
{
    [Fact]
    public async Task DispatchAsync_PlacesMarketBuy_WhenQuoteBalanceIsAvailable()
    {
        await using var harness = await CreateHarnessAsync();

        var result = await harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                Quantity = 0.01m,
                Price = 65000m
            },
            CancellationToken.None);

        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(ExecutionOrderSide.Buy, harness.PrivateRestClient.LastPlacementRequest!.Side);
        Assert.Equal(ExecutionOrderType.Market, harness.PrivateRestClient.LastPlacementRequest.OrderType);
        Assert.Null(harness.PrivateRestClient.LastPlacementRequest.TimeInForce);
        Assert.Equal(harness.Order.Id.ToString("N"), harness.PrivateRestClient.LastPlacementRequest.ExecutionAttemptId);
        Assert.Equal(ExchangeDataPlane.Spot, result.InitialSnapshot!.Plane);
        Assert.Equal("spot-order-1", result.ExternalOrderId);
    }

    [Fact]
    public async Task DispatchAsync_PlacesMarketSell_WhenBaseBalanceIsAvailable()
    {
        await using var harness = await CreateHarnessAsync();

        var result = await harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                Side = ExecutionOrderSide.Sell,
                Quantity = 0.25m,
                Price = 65000m
            },
            CancellationToken.None);

        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(ExecutionOrderSide.Sell, harness.PrivateRestClient.LastPlacementRequest!.Side);
        Assert.Equal(ExecutionOrderType.Market, harness.PrivateRestClient.LastPlacementRequest.OrderType);
        Assert.Equal("spot-order-1", result.ExternalOrderId);
    }

    [Fact]
    public async Task DispatchAsync_PlacesLimitBuy_WithDefaultGtc()
    {
        await using var harness = await CreateHarnessAsync();

        var result = await harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                OrderType = ExecutionOrderType.Limit,
                Quantity = 0.01m,
                Price = 64000m
            },
            CancellationToken.None);

        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal("GTC", harness.PrivateRestClient.LastPlacementRequest!.TimeInForce);
        Assert.Equal(64000m, harness.PrivateRestClient.LastPlacementRequest.Price);
        Assert.Equal("spot-order-1", result.ExternalOrderId);
    }

    [Fact]
    public async Task DispatchAsync_PlacesLimitSell_WithExplicitTimeInForce()
    {
        await using var harness = await CreateHarnessAsync();

        var result = await harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                Side = ExecutionOrderSide.Sell,
                OrderType = ExecutionOrderType.Limit,
                Quantity = 0.10m,
                Price = 66000m,
                TimeInForce = "IOC"
            },
            CancellationToken.None);

        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal("IOC", harness.PrivateRestClient.LastPlacementRequest!.TimeInForce);
        Assert.Equal(ExecutionOrderSide.Sell, harness.PrivateRestClient.LastPlacementRequest.Side);
        Assert.Equal("spot-order-1", result.ExternalOrderId);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenQuantityViolatesSpotFilter()
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
    public async Task DispatchAsync_FailsClosed_WhenLimitPriceViolatesTickSize()
    {
        await using var harness = await CreateHarnessAsync();

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                OrderType = ExecutionOrderType.Limit,
                Quantity = 0.01m,
                Price = 64000.15m
            },
            CancellationToken.None));

        Assert.Equal("LimitPriceTickSizeMismatch", exception.ReasonCode);
        Assert.Contains("tick size", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenQuoteBalanceIsInsufficient()
    {
        await using var harness = await CreateHarnessAsync(quoteAvailableBalance: 100m);

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                Quantity = 0.01m,
                Price = 65000m
            },
            CancellationToken.None));

        Assert.Equal("SpotInsufficientQuoteBalance", exception.ReasonCode);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenBaseBalanceIsInsufficient()
    {
        await using var harness = await CreateHarnessAsync(baseAvailableBalance: 0.05m);

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                Side = ExecutionOrderSide.Sell,
                Quantity = 0.10m,
                Price = 65000m
            },
            CancellationToken.None));

        Assert.Equal("SpotInsufficientBaseAsset", exception.ReasonCode);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    private static async Task<TestHarness> CreateHarnessAsync(
        decimal quoteAvailableBalance = 1000m,
        decimal baseAvailableBalance = 1m,
        SymbolMetadataSnapshot? metadata = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var exchangeAccountId = Guid.NewGuid();

        dbContext.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = "user-spot-exec",
            ExchangeName = "Binance",
            DisplayName = "Binance Spot",
            IsReadOnly = false,
            CredentialStatus = ExchangeCredentialStatus.Active
        });
        dbContext.ApiCredentialValidations.Add(new ApiCredentialValidation
        {
            Id = Guid.NewGuid(),
            ExchangeAccountId = exchangeAccountId,
            OwnerUserId = "user-spot-exec",
            IsKeyValid = true,
            CanTrade = true,
            SupportsSpot = true,
            SupportsFutures = false,
            ValidationStatus = "Valid",
            PermissionSummary = "Trade=Y; Spot=Y",
            ValidatedAtUtc = new DateTime(2026, 4, 5, 10, 0, 0, DateTimeKind.Utc)
        });
        dbContext.ExchangeBalances.AddRange(
            new ExchangeBalance
            {
                OwnerUserId = "user-spot-exec",
                ExchangeAccountId = exchangeAccountId,
                Plane = ExchangeDataPlane.Spot,
                Asset = "USDT",
                WalletBalance = quoteAvailableBalance + 25m,
                CrossWalletBalance = quoteAvailableBalance + 25m,
                AvailableBalance = quoteAvailableBalance,
                MaxWithdrawAmount = quoteAvailableBalance,
                LockedBalance = 25m,
                ExchangeUpdatedAtUtc = new DateTime(2026, 4, 5, 11, 0, 0, DateTimeKind.Utc),
                SyncedAtUtc = new DateTime(2026, 4, 5, 11, 0, 0, DateTimeKind.Utc)
            },
            new ExchangeBalance
            {
                OwnerUserId = "user-spot-exec",
                ExchangeAccountId = exchangeAccountId,
                Plane = ExchangeDataPlane.Spot,
                Asset = "BTC",
                WalletBalance = baseAvailableBalance + 0.1m,
                CrossWalletBalance = baseAvailableBalance + 0.1m,
                AvailableBalance = baseAvailableBalance,
                MaxWithdrawAmount = baseAvailableBalance,
                LockedBalance = 0.1m,
                ExchangeUpdatedAtUtc = new DateTime(2026, 4, 5, 11, 0, 0, DateTimeKind.Utc),
                SyncedAtUtc = new DateTime(2026, 4, 5, 11, 0, 0, DateTimeKind.Utc)
            });
        await dbContext.SaveChangesAsync();

        var order = new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-spot-exec",
            ExchangeAccountId = exchangeAccountId,
            Plane = ExchangeDataPlane.Spot,
            StrategyKey = "spot-core",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.01m,
            Price = 65000m,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            IdempotencyKey = "spot-exec-test",
            RootCorrelationId = "corr-spot-exec-test",
            LastStateChangedAtUtc = new DateTime(2026, 4, 5, 12, 0, 0, DateTimeKind.Utc)
        };

        var resolvedMetadata = metadata ?? CreateMetadata();
        var privateRestClient = new FakeSpotPrivateRestClient();
        var executor = new BinanceSpotExecutor(
            dbContext,
            new FakeExchangeCredentialService(),
            privateRestClient,
            NullLogger<BinanceSpotExecutor>.Instance,
            marketDataService: new FakeMarketDataService(resolvedMetadata),
            exchangeInfoClient: new FakeExchangeInfoClient(resolvedMetadata));

        return new TestHarness(
            dbContext,
            executor,
            privateRestClient,
            exchangeAccountId,
            order);
    }

    private static SymbolMetadataSnapshot CreateMetadata(
        decimal tickSize = 0.1m,
        decimal stepSize = 0.001m,
        decimal? minQuantity = 0.001m,
        decimal? minNotional = 100m,
        int? pricePrecision = 1,
        int? quantityPrecision = 3)
    {
        return new SymbolMetadataSnapshot(
            "BTCUSDT",
            "Binance",
            "BTC",
            "USDT",
            tickSize,
            stepSize,
            "TRADING",
            true,
            new DateTime(2026, 4, 5, 12, 0, 0, DateTimeKind.Utc))
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
            OwnerUserId: "user-spot-exec",
            TradingStrategyId: Guid.NewGuid(),
            TradingStrategyVersionId: Guid.NewGuid(),
            StrategySignalId: Guid.NewGuid(),
            SignalType: StrategySignalType.Entry,
            StrategyKey: "spot-core",
            Symbol: "BTCUSDT",
            Timeframe: "1m",
            BaseAsset: "BTC",
            QuoteAsset: "USDT",
            Side: ExecutionOrderSide.Buy,
            OrderType: ExecutionOrderType.Market,
            Quantity: 0.01m,
            Price: 65000m,
            ExchangeAccountId: exchangeAccountId,
            IsDemo: false,
            CorrelationId: "corr-spot-exec-test",
            Context: "SpotExecutionTests",
            Plane: ExchangeDataPlane.Spot);
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
                        StoredAtUtc: new DateTime(2026, 4, 5, 10, 0, 0, DateTimeKind.Utc),
                        LastValidatedAtUtc: new DateTime(2026, 4, 5, 11, 0, 0, DateTimeKind.Utc),
                        LastAccessedAtUtc: new DateTime(2026, 4, 5, 11, 5, 0, DateTimeKind.Utc),
                        LastRotatedAtUtc: new DateTime(2026, 4, 5, 9, 0, 0, DateTimeKind.Utc),
                        RevalidateAfterUtc: new DateTime(2026, 5, 5, 11, 0, 0, DateTimeKind.Utc),
                        RotateAfterUtc: new DateTime(2026, 7, 5, 11, 0, 0, DateTimeKind.Utc))));
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

    private sealed class FakeSpotPrivateRestClient : IBinanceSpotPrivateRestClient
    {
        public int PlaceOrderCalls { get; private set; }

        public BinanceOrderPlacementRequest? LastPlacementRequest { get; private set; }

        public Task<BinanceOrderPlacementResult> PlaceOrderAsync(
            BinanceOrderPlacementRequest request,
            CancellationToken cancellationToken = default)
        {
            PlaceOrderCalls++;
            LastPlacementRequest = request;

            var snapshot = new BinanceOrderStatusSnapshot(
                request.Symbol,
                $"spot-order-{PlaceOrderCalls}",
                request.ClientOrderId,
                "NEW",
                request.Quantity,
                0m,
                0m,
                0m,
                0m,
                0m,
                DateTime.UtcNow,
                "Binance.SpotPrivateRest.OrderPlacement",
                Plane: ExchangeDataPlane.Spot);

            return Task.FromResult(new BinanceOrderPlacementResult(snapshot.ExchangeOrderId, snapshot.ClientOrderId, DateTime.UtcNow, snapshot));
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

        public Task<BinanceOrderStatusSnapshot> GetOrderAsync(
            BinanceOrderQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyCollection<BinanceSpotTradeFillSnapshot>> GetTradeFillsAsync(
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
    }

    private sealed class FakeMarketDataService(SymbolMetadataSnapshot metadata) : IMarketDataService
    {
        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<MarketPriceSnapshot?>(new MarketPriceSnapshot(symbol, 65000m, DateTime.UtcNow, DateTime.UtcNow, "UnitTest"));

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<SymbolMetadataSnapshot?>(metadata);

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }
    }

    private sealed class FakeExchangeInfoClient(SymbolMetadataSnapshot metadata) : IBinanceExchangeInfoClient
    {
        public Task<IReadOnlyCollection<SymbolMetadataSnapshot>> GetSymbolMetadataAsync(
            IReadOnlyCollection<string> symbols,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyCollection<SymbolMetadataSnapshot>>([metadata]);
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
        BinanceSpotExecutor executor,
        FakeSpotPrivateRestClient privateRestClient,
        Guid exchangeAccountId,
        ExecutionOrder order) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public BinanceSpotExecutor Executor { get; } = executor;

        public FakeSpotPrivateRestClient PrivateRestClient { get; } = privateRestClient;

        public Guid ExchangeAccountId { get; } = exchangeAccountId;

        public ExecutionOrder Order { get; } = order;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }
    }
}
