using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoinBot.UnitTests.Infrastructure.Execution;

public sealed class SpotPortfolioAccountingServiceTests
{
    [Fact]
    public async Task ApplyAsync_BuyFill_IncreasesHoldingAndAverageCost_WithQuoteFee()
    {
        await using var context = CreateContext();
        var order = CreateOrder(ExecutionOrderSide.Buy);
        var service = CreateService(
            context,
            [
                CreateFill(order, tradeId: 1, quantity: 1m, quoteQuantity: 100m, price: 100m, feeAsset: "USDT", feeAmount: 5m)
            ]);

        var result = await service.ApplyAsync(order, CreateSnapshot(order, "FILLED", 1m, 100m));
        await context.SaveChangesAsync();
        var persisted = await context.SpotPortfolioFills.SingleAsync();

        Assert.NotNull(result);
        Assert.Equal(1, result!.AppliedTradeCount);
        Assert.Equal(1m, persisted.HoldingQuantityAfter);
        Assert.Equal(105m, persisted.HoldingCostBasisAfter);
        Assert.Equal(105m, persisted.HoldingAverageCostAfter);
        Assert.Equal(5m, persisted.FeeAmountInQuote);
        Assert.Equal(0m, persisted.RealizedPnlDelta);
    }

    [Fact]
    public async Task ApplyAsync_SecondBuy_ComputesWeightedAverageCost()
    {
        await using var context = CreateContext();
        var firstOrder = CreateOrder(ExecutionOrderSide.Buy);
        var secondOrder = CreateOrder(ExecutionOrderSide.Buy);
        var firstService = CreateService(
            context,
            [CreateFill(firstOrder, tradeId: 1, quantity: 1m, quoteQuantity: 100m, price: 100m)]);
        var secondService = CreateService(
            context,
            [CreateFill(secondOrder, tradeId: 2, quantity: 1m, quoteQuantity: 200m, price: 200m)]);

        await firstService.ApplyAsync(firstOrder, CreateSnapshot(firstOrder, "FILLED", 1m, 100m));
        await context.SaveChangesAsync();
        var result = await secondService.ApplyAsync(secondOrder, CreateSnapshot(secondOrder, "FILLED", 1m, 200m));
        await context.SaveChangesAsync();
        var latest = await context.SpotPortfolioFills
            .OrderByDescending(entity => entity.TradeId)
            .FirstAsync();

        Assert.NotNull(result);
        Assert.Equal(2m, latest.HoldingQuantityAfter);
        Assert.Equal(300m, latest.HoldingCostBasisAfter);
        Assert.Equal(150m, latest.HoldingAverageCostAfter);
    }

    [Fact]
    public async Task ApplyAsync_SellFill_ComputesRealizedPnl_WithFeeParity()
    {
        await using var context = CreateContext();
        var buyOne = CreateOrder(ExecutionOrderSide.Buy);
        var buyTwo = CreateOrder(ExecutionOrderSide.Buy);
        var sell = CreateOrder(ExecutionOrderSide.Sell);

        await CreateService(context, [CreateFill(buyOne, tradeId: 1, quantity: 1m, quoteQuantity: 100m, price: 100m)])
            .ApplyAsync(buyOne, CreateSnapshot(buyOne, "FILLED", 1m, 100m));
        await context.SaveChangesAsync();
        await CreateService(context, [CreateFill(buyTwo, tradeId: 2, quantity: 1m, quoteQuantity: 200m, price: 200m)])
            .ApplyAsync(buyTwo, CreateSnapshot(buyTwo, "FILLED", 1m, 200m));
        await context.SaveChangesAsync();

        var result = await CreateService(
                context,
                [CreateFill(sell, tradeId: 3, quantity: 1m, quoteQuantity: 250m, price: 250m, feeAsset: "USDT", feeAmount: 10m)])
            .ApplyAsync(sell, CreateSnapshot(sell, "FILLED", 1m, 250m));
        await context.SaveChangesAsync();
        var latest = await context.SpotPortfolioFills
            .OrderByDescending(entity => entity.TradeId)
            .FirstAsync();

        Assert.NotNull(result);
        Assert.Equal(90m, result!.RealizedPnlDelta);
        Assert.Equal(1m, latest.HoldingQuantityAfter);
        Assert.Equal(150m, latest.HoldingCostBasisAfter);
        Assert.Equal(150m, latest.HoldingAverageCostAfter);
        Assert.Equal(90m, latest.CumulativeRealizedPnlAfter);
        Assert.Equal(10m, latest.CumulativeFeesInQuoteAfter);
    }

    [Fact]
    public async Task ApplyAsync_ConvertsExternalFeeAsset_UsingMarketPrice()
    {
        await using var context = CreateContext();
        var order = CreateOrder(ExecutionOrderSide.Buy);
        var service = CreateService(
            context,
            [CreateFill(order, tradeId: 1, quantity: 1m, quoteQuantity: 100m, price: 100m, feeAsset: "BNB", feeAmount: 0.01m)],
            new Dictionary<string, decimal>(StringComparer.Ordinal)
            {
                ["BNBUSDT"] = 600m
            });

        var result = await service.ApplyAsync(order, CreateSnapshot(order, "FILLED", 1m, 100m));
        await context.SaveChangesAsync();
        var persisted = await context.SpotPortfolioFills.SingleAsync();

        Assert.NotNull(result);
        Assert.Equal(6m, persisted.FeeAmountInQuote);
        Assert.Equal(106m, persisted.HoldingCostBasisAfter);
        Assert.Equal(6m, result!.FeesInQuoteApplied);
    }

    [Fact]
    public async Task ApplyAsync_DuplicateTrade_DoesNotDoubleCount()
    {
        await using var context = CreateContext();
        var order = CreateOrder(ExecutionOrderSide.Buy);
        var fill = CreateFill(order, tradeId: 1, quantity: 1m, quoteQuantity: 100m, price: 100m);
        var service = CreateService(context, [fill]);

        await service.ApplyAsync(order, CreateSnapshot(order, "FILLED", 1m, 100m));
        await context.SaveChangesAsync();
        var replay = await service.ApplyAsync(order, CreateSnapshot(order, "FILLED", 1m, 100m));
        var rowCount = await context.SpotPortfolioFills.CountAsync();

        Assert.NotNull(replay);
        Assert.Equal(0, replay!.AppliedTradeCount);
        Assert.Equal(1, replay.DuplicateTradeCount);
        Assert.Equal(1, rowCount);
    }

    [Fact]
    public async Task ApplyAsync_FailsClosed_WhenExternalFeeConversionIsUnavailable()
    {
        await using var context = CreateContext();
        var order = CreateOrder(ExecutionOrderSide.Buy);
        var service = CreateService(
            context,
            [CreateFill(order, tradeId: 1, quantity: 1m, quoteQuantity: 100m, price: 100m, feeAsset: "BNB", feeAmount: 0.01m)],
            new Dictionary<string, decimal>(StringComparer.Ordinal));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyAsync(order, CreateSnapshot(order, "FILLED", 1m, 100m)));
        Assert.Empty(context.SpotPortfolioFills);
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private static SpotPortfolioAccountingService CreateService(
        ApplicationDbContext context,
        IReadOnlyCollection<BinanceSpotTradeFillSnapshot> fills,
        IReadOnlyDictionary<string, decimal>? prices = null)
    {
        return new SpotPortfolioAccountingService(
            context,
            new FakeExchangeCredentialService(),
            new FakeSpotPrivateRestClient(fills),
            new FakeMarketDataService(prices),
            NullLogger<SpotPortfolioAccountingService>.Instance);
    }

    private static ExecutionOrder CreateOrder(ExecutionOrderSide side)
    {
        return new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-spot-accounting",
            ExchangeAccountId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Plane = ExchangeDataPlane.Spot,
            StrategyKey = "spot-accounting",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = side,
            OrderType = ExecutionOrderType.Market,
            Quantity = 1m,
            Price = 100m,
            FilledQuantity = 1m,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            RootCorrelationId = "corr-spot-accounting",
            ExternalOrderId = "spot-order-1",
            LastStateChangedAtUtc = new DateTime(2026, 4, 5, 12, 0, 0, DateTimeKind.Utc)
        };
    }

    private static BinanceOrderStatusSnapshot CreateSnapshot(
        ExecutionOrder order,
        string status,
        decimal executedQuantity,
        decimal cumulativeQuoteQuantity)
    {
        return new BinanceOrderStatusSnapshot(
            order.Symbol,
            order.ExternalOrderId ?? "spot-order-1",
            ExecutionClientOrderId.Create(order.Id),
            status,
            order.Quantity,
            executedQuantity,
            cumulativeQuoteQuantity,
            executedQuantity == 0m ? 0m : cumulativeQuoteQuantity / executedQuantity,
            0m,
            0m,
            new DateTime(2026, 4, 5, 12, 0, 0, DateTimeKind.Utc),
            "Binance.SpotPrivateRest.MyTrades",
            Plane: ExchangeDataPlane.Spot);
    }

    private static BinanceSpotTradeFillSnapshot CreateFill(
        ExecutionOrder order,
        long tradeId,
        decimal quantity,
        decimal quoteQuantity,
        decimal price,
        string? feeAsset = null,
        decimal? feeAmount = null)
    {
        return new BinanceSpotTradeFillSnapshot(
            order.Symbol,
            order.ExternalOrderId ?? "spot-order-1",
            ExecutionClientOrderId.Create(order.Id),
            tradeId,
            quantity,
            quoteQuantity,
            price,
            feeAsset,
            feeAmount,
            new DateTime(2026, 4, 5, 12, 0, 0, DateTimeKind.Utc).AddSeconds(tradeId),
            "Binance.SpotPrivateRest.MyTrades");
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeExchangeCredentialService : IExchangeCredentialService
    {
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
                        "credential-v1",
                        null,
                        null,
                        null,
                        null,
                        null,
                        null)));
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

    private sealed class FakeSpotPrivateRestClient(IReadOnlyCollection<BinanceSpotTradeFillSnapshot> fills) : IBinanceSpotPrivateRestClient
    {
        public Task<BinanceOrderPlacementResult> PlaceOrderAsync(BinanceOrderPlacementRequest request, CancellationToken cancellationToken = default)
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

        public Task<BinanceOrderStatusSnapshot> GetOrderAsync(BinanceOrderQueryRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyCollection<BinanceSpotTradeFillSnapshot>> GetTradeFillsAsync(BinanceOrderQueryRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(fills);
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

    private sealed class FakeMarketDataService(IReadOnlyDictionary<string, decimal>? prices) : IMarketDataService
    {
        private readonly IReadOnlyDictionary<string, decimal> pricesBySymbol = prices ?? new Dictionary<string, decimal>(StringComparer.Ordinal);

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
            return ValueTask.FromResult<MarketPriceSnapshot?>(
                pricesBySymbol.TryGetValue(symbol, out var price)
                    ? new MarketPriceSnapshot(symbol, price, DateTime.UtcNow, DateTime.UtcNow, "SpotPortfolioAccountingTest")
                    : null);
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(null);
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }
    }
}
