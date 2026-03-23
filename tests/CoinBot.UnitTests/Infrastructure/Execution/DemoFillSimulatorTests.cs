using System.Runtime.CompilerServices;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Execution;

public sealed class DemoFillSimulatorTests
{
    [Fact]
    public async Task SimulateOnSubmissionAsync_UsesExecutionOrderPriceFallbackForMarketOrders_WhenLatestPriceMissing()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        var marketDataService = new FakeMarketDataService();
        var simulator = CreateSimulator(marketDataService, timeProvider);
        var order = CreateOrder(ExecutionOrderType.Market, ExecutionOrderSide.Buy, quantity: 1m, price: 100m);

        var result = await simulator.SimulateOnSubmissionAsync(order);

        Assert.NotNull(result.Reservation);
        Assert.NotNull(result.Fill);
        Assert.Equal("USDT", result.Reservation!.Asset);
        Assert.Equal(100.20010m, result.Reservation.Amount);
        Assert.Equal(100.20010m, result.Reservation.ConsumedAmount);
        Assert.Equal("ExecutionOrder.PriceFallback", result.Fill!.ReferenceSource);
        Assert.Equal(100.10m, result.Fill.FillPrice);
        Assert.Equal(0.10010m, result.Fill.FeeAmount);
    }

    [Fact]
    public async Task SimulateOnSubmissionAsync_UsesSingleDeterministicPartialFillForLargeTriggeredLimitOrder()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        var marketDataService = new FakeMarketDataService();
        marketDataService.SetLatestPrice("AAVEUSDT", 99m, At(0), "unit-test");
        marketDataService.SetSymbolMetadata("AAVEUSDT", "AAVE", "USDT", 0.01m, 1m);
        var simulator = CreateSimulator(
            marketDataService,
            timeProvider,
            new DemoFillSimulatorOptions
            {
                PartialFillMinNotional = 1000m,
                PartialFillRatio = 0.60m
            });
        var order = CreateOrder(ExecutionOrderType.Limit, ExecutionOrderSide.Buy, quantity: 20m, price: 100m);
        order.Symbol = "AAVEUSDT";
        order.BaseAsset = "AAVE";
        order.QuoteAsset = "USDT";

        var result = await simulator.SimulateOnSubmissionAsync(order);

        Assert.NotNull(result.Reservation);
        Assert.NotNull(result.Fill);
        Assert.Equal(2001.6m, result.Reservation!.Amount);
        Assert.Equal(1200.96m, result.Reservation.ConsumedAmount);
        Assert.Equal(12m, result.Fill!.FillQuantity);
        Assert.Equal(100m, result.Fill.FillPrice);
        Assert.False(result.Fill.IsFinalFill);
        Assert.Equal("DemoPartiallyFilled", result.Fill.EventCode);
    }

    [Fact]
    public async Task SimulateOnNextPriceAsync_WaitsForNewObservationBeforeCompletingPartiallyFilledOrder()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        var marketDataService = new FakeMarketDataService();
        var simulator = CreateSimulator(
            marketDataService,
            timeProvider,
            new DemoFillSimulatorOptions
            {
                PartialFillMinNotional = 1000m,
                PartialFillRatio = 0.60m
            });
        var order = CreateOrder(ExecutionOrderType.Limit, ExecutionOrderSide.Buy, quantity: 20m, price: 100m);
        order.Symbol = "AAVEUSDT";
        order.BaseAsset = "AAVE";
        order.QuoteAsset = "USDT";
        order.FilledQuantity = 12m;
        order.AverageFillPrice = 100m;
        order.LastFilledAtUtc = At(0);
        order.State = ExecutionOrderState.PartiallyFilled;

        marketDataService.SetLatestPrice("AAVEUSDT", 99m, At(0), "unit-test");

        var sameObservation = await simulator.SimulateOnNextPriceAsync(order);

        Assert.Null(sameObservation);

        marketDataService.SetLatestPrice("AAVEUSDT", 99m, At(1), "unit-test");
        var nextObservation = await simulator.SimulateOnNextPriceAsync(order);

        Assert.NotNull(nextObservation);
        Assert.Equal(8m, nextObservation!.FillQuantity);
        Assert.True(nextObservation.IsFinalFill);
        Assert.Equal("DemoFilled", nextObservation.EventCode);
    }

    [Fact]
    public void EvaluateProtectiveTrigger_UsesSideAwareThresholds()
    {
        var simulator = CreateSimulator(new FakeMarketDataService(), new AdjustableTimeProvider(new DateTimeOffset(At(0))));

        var longOrder = CreateOrder(ExecutionOrderType.Market, ExecutionOrderSide.Buy, quantity: 1m, price: 100m);
        longOrder.StopLossPrice = 95m;
        longOrder.TakeProfitPrice = 105m;

        var shortOrder = CreateOrder(ExecutionOrderType.Market, ExecutionOrderSide.Sell, quantity: 1m, price: 100m);
        shortOrder.StopLossPrice = 105m;
        shortOrder.TakeProfitPrice = 95m;
        var failClosedOrder = CreateOrder(ExecutionOrderType.Market, ExecutionOrderSide.Buy, quantity: 1m, price: 100m);
        failClosedOrder.StopLossPrice = 105m;
        failClosedOrder.TakeProfitPrice = 95m;

        Assert.Equal(DemoProtectiveTriggerKind.StopLoss, simulator.EvaluateProtectiveTrigger(longOrder, 94m));
        Assert.Equal(DemoProtectiveTriggerKind.TakeProfit, simulator.EvaluateProtectiveTrigger(longOrder, 106m));
        Assert.Equal(DemoProtectiveTriggerKind.StopLoss, simulator.EvaluateProtectiveTrigger(shortOrder, 106m));
        Assert.Equal(DemoProtectiveTriggerKind.TakeProfit, simulator.EvaluateProtectiveTrigger(shortOrder, 94m));
        Assert.Equal(DemoProtectiveTriggerKind.StopLoss, simulator.EvaluateProtectiveTrigger(failClosedOrder, 100m));
    }

    private static DemoFillSimulator CreateSimulator(
        FakeMarketDataService marketDataService,
        AdjustableTimeProvider timeProvider,
        DemoFillSimulatorOptions? options = null)
    {
        return new DemoFillSimulator(
            marketDataService,
            Options.Create(options ?? new DemoFillSimulatorOptions()),
            timeProvider,
            NullLogger<DemoFillSimulator>.Instance);
    }

    private static ExecutionOrder CreateOrder(
        ExecutionOrderType orderType,
        ExecutionOrderSide side,
        decimal quantity,
        decimal price)
    {
        return new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-demo",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            StrategyKey = "demo-core",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = side,
            OrderType = orderType,
            Quantity = quantity,
            Price = price,
            ExecutionEnvironment = ExecutionEnvironment.Demo,
            ExecutorKind = ExecutionOrderExecutorKind.Virtual,
            RootCorrelationId = "corr-demo",
            IdempotencyKey = Guid.NewGuid().ToString("N")
        };
    }

    private static DateTime At(int minuteOffset)
    {
        return new DateTime(2026, 3, 22, 12, minuteOffset, 0, DateTimeKind.Utc);
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
                At(0));
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
}
