using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoinBot.UnitTests.Infrastructure.Execution;

public sealed class ExecutionOrderLifecycleServiceTests
{
    [Fact]
    public async Task ApplyExchangeUpdateAsync_PersistsCancelRequestedThenCancelled()
    {
        await using var dbContext = CreateContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var lifecycleService = CreateService(dbContext, timeProvider);
        var orderId = await SeedOrderAsync(dbContext, ExecutionOrderState.Submitted, filledQuantity: 0m);

        var cancelRequested = await lifecycleService.ApplyExchangeUpdateAsync(
            CreateSnapshot(orderId, "PENDING_CANCEL", executedQuantity: 0m, timeProvider.GetUtcNow().UtcDateTime));
        var cancelled = await lifecycleService.ApplyExchangeUpdateAsync(
            CreateSnapshot(orderId, "CANCELED", executedQuantity: 0m, timeProvider.GetUtcNow().UtcDateTime.AddSeconds(1)));

        var order = await dbContext.ExecutionOrders.SingleAsync(entity => entity.Id == orderId);
        var transitions = await dbContext.ExecutionOrderTransitions
            .OrderBy(entity => entity.SequenceNumber)
            .Select(entity => entity.State)
            .ToListAsync();

        Assert.True(cancelRequested);
        Assert.True(cancelled);
        Assert.Equal(ExecutionOrderState.Cancelled, order.State);
        Assert.True(order.SubmittedToBroker);
        Assert.Equal(ExecutionRejectionStage.None, order.RejectionStage);
        Assert.Equal(
            [ExecutionOrderState.CancelRequested, ExecutionOrderState.Cancelled],
            transitions);
    }

    [Fact]
    public async Task ApplyExchangeUpdateAsync_IgnoresDuplicatePartialFillAndOutOfOrderReject()
    {
        await using var dbContext = CreateContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var lifecycleService = CreateService(dbContext, timeProvider);
        var orderId = await SeedOrderAsync(dbContext, ExecutionOrderState.PartiallyFilled, filledQuantity: 0.02m);

        var duplicatePartialFill = await lifecycleService.ApplyExchangeUpdateAsync(
            CreateSnapshot(orderId, "PARTIALLY_FILLED", executedQuantity: 0.02m, timeProvider.GetUtcNow().UtcDateTime));
        var outOfOrderReject = await lifecycleService.ApplyExchangeUpdateAsync(
            CreateSnapshot(orderId, "REJECTED", executedQuantity: 0.02m, timeProvider.GetUtcNow().UtcDateTime.AddSeconds(1)));

        var order = await dbContext.ExecutionOrders.SingleAsync(entity => entity.Id == orderId);
        var transitionCount = await dbContext.ExecutionOrderTransitions.CountAsync(entity => entity.ExecutionOrderId == orderId);

        Assert.True(duplicatePartialFill);
        Assert.True(outOfOrderReject);
        Assert.Equal(ExecutionOrderState.PartiallyFilled, order.State);
        Assert.Equal(0.02m, order.FilledQuantity);
        Assert.Equal(0, transitionCount);
    }

    [Fact]
    public async Task ApplyExchangeUpdateAsync_IgnoresDuplicateCancelAndLateCallbackAfterTerminalState()
    {
        await using var dbContext = CreateContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var lifecycleService = CreateService(dbContext, timeProvider);
        var orderId = await SeedOrderAsync(dbContext, ExecutionOrderState.Cancelled, filledQuantity: 0m);

        var duplicateCancel = await lifecycleService.ApplyExchangeUpdateAsync(
            CreateSnapshot(orderId, "CANCELED", executedQuantity: 0m, timeProvider.GetUtcNow().UtcDateTime));
        var lateFill = await lifecycleService.ApplyExchangeUpdateAsync(
            CreateSnapshot(orderId, "FILLED", executedQuantity: 0.05m, timeProvider.GetUtcNow().UtcDateTime.AddSeconds(1)));

        var order = await dbContext.ExecutionOrders.SingleAsync(entity => entity.Id == orderId);
        var transitionCount = await dbContext.ExecutionOrderTransitions.CountAsync(entity => entity.ExecutionOrderId == orderId);

        Assert.True(duplicateCancel);
        Assert.True(lateFill);
        Assert.Equal(ExecutionOrderState.Cancelled, order.State);
        Assert.Equal(0.05m, order.FilledQuantity);
        Assert.Equal(0, transitionCount);
    }

    [Fact]
    public async Task ApplyExchangeUpdateAsync_TransitionsSpotPartialFillToFilled_AndIgnoresDuplicateFill()
    {
        await using var dbContext = CreateContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero));
        var lifecycleService = CreateService(dbContext, timeProvider);
        var orderId = await SeedOrderAsync(dbContext, ExecutionOrderState.PartiallyFilled, filledQuantity: 0.02m, plane: ExchangeDataPlane.Spot);

        var filled = await lifecycleService.ApplyExchangeUpdateAsync(
            CreateSnapshot(orderId, "FILLED", executedQuantity: 0.05m, timeProvider.GetUtcNow().UtcDateTime, plane: ExchangeDataPlane.Spot));
        var duplicateFilled = await lifecycleService.ApplyExchangeUpdateAsync(
            CreateSnapshot(orderId, "FILLED", executedQuantity: 0.05m, timeProvider.GetUtcNow().UtcDateTime.AddSeconds(1), plane: ExchangeDataPlane.Spot));

        var order = await dbContext.ExecutionOrders.SingleAsync(entity => entity.Id == orderId);
        var transitions = await dbContext.ExecutionOrderTransitions
            .Where(entity => entity.ExecutionOrderId == orderId)
            .OrderBy(entity => entity.SequenceNumber)
            .ToListAsync();

        Assert.True(filled);
        Assert.True(duplicateFilled);
        Assert.Equal(ExecutionOrderState.Filled, order.State);
        Assert.Equal(0.05m, order.FilledQuantity);
        Assert.Single(transitions);
        Assert.Equal(ExecutionOrderState.Filled, transitions[0].State);
        Assert.Contains("Plane=Spot", transitions[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyExchangeUpdateAsync_RefreshesLiveBotOpenPositionCount_FromPrivatePlaneTruth()
    {
        await using var dbContext = CreateContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero));
        var lifecycleService = CreateService(dbContext, timeProvider);
        var botId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        var orderId = await SeedOrderAsync(
            dbContext,
            ExecutionOrderState.Submitted,
            filledQuantity: 0m,
            botId: botId,
            exchangeAccountId: exchangeAccountId);
        dbContext.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = "user-lifecycle",
            Name = "Lifecycle bot",
            StrategyKey = "lifecycle-core",
            Symbol = "BTCUSDT",
            ExchangeAccountId = exchangeAccountId,
            IsEnabled = true,
            OpenPositionCount = 0,
            OpenOrderCount = 1
        });
        dbContext.ExchangePositions.Add(new ExchangePosition
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-lifecycle",
            ExchangeAccountId = exchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            Symbol = "BTCUSDT",
            PositionSide = "BOTH",
            Quantity = 0.05m,
            EntryPrice = 65000m,
            BreakEvenPrice = 65000m,
            MarginType = "isolated",
            ExchangeUpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            SyncedAtUtc = timeProvider.GetUtcNow().UtcDateTime
        });
        await dbContext.SaveChangesAsync();

        var applied = await lifecycleService.ApplyExchangeUpdateAsync(
            CreateSnapshot(orderId, "FILLED", executedQuantity: 0.05m, timeProvider.GetUtcNow().UtcDateTime));

        var bot = await dbContext.TradingBots.SingleAsync(entity => entity.Id == botId);
        var order = await dbContext.ExecutionOrders.SingleAsync(entity => entity.Id == orderId);

        Assert.True(applied);
        Assert.Equal(ExecutionOrderState.Filled, order.State);
        Assert.Equal(1, bot.OpenPositionCount);
        Assert.Equal(0, bot.OpenOrderCount);
    }

    [Fact]
    public async Task ApplyExchangeUpdateAsync_WritesSpotPortfolioAudit_WithRootCorrelation()
    {
        await using var dbContext = CreateContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero));
        var accountingService = new FakeSpotPortfolioAccountingService(
            new SpotPortfolioApplyResult(
                AppliedTradeCount: 2,
                DuplicateTradeCount: 1,
                RealizedPnlDelta: 42.5m,
                FeesInQuoteApplied: 1.5m,
                HoldingQuantityAfter: 0.5m,
                HoldingCostBasisAfter: 32000m,
                HoldingAverageCostAfter: 64000m,
                TradeIdsSummary: "77,78",
                LastTradeAtUtc: timeProvider.GetUtcNow().UtcDateTime));
        var lifecycleService = CreateService(dbContext, timeProvider, accountingService);
        var orderId = await SeedOrderAsync(dbContext, ExecutionOrderState.PartiallyFilled, filledQuantity: 0.02m, plane: ExchangeDataPlane.Spot);

        var applied = await lifecycleService.ApplyExchangeUpdateAsync(
            CreateSnapshot(orderId, "FILLED", executedQuantity: 0.05m, timeProvider.GetUtcNow().UtcDateTime, plane: ExchangeDataPlane.Spot));
        var auditLogs = await dbContext.AuditLogs
            .OrderBy(entity => entity.CreatedDate)
            .ToListAsync();

        Assert.True(applied);
        Assert.Equal(2, auditLogs.Count);
        Assert.Contains(auditLogs, entity => entity.Action == "ExecutionOrder.ExchangeUpdate" && entity.CorrelationId == "corr-lifecycle-root");
        Assert.Contains(auditLogs, entity =>
            entity.Action == "SpotPortfolio.FillApplied" &&
            entity.CorrelationId == "corr-lifecycle-root" &&
            entity.Context!.Contains("AppliedTrades=2", StringComparison.Ordinal) &&
            entity.Context.Contains("TradeIds=77,78", StringComparison.Ordinal));
    }

    private static ExecutionOrderLifecycleService CreateService(
        ApplicationDbContext dbContext,
        TimeProvider timeProvider,
        ISpotPortfolioAccountingService? spotPortfolioAccountingService = null)
    {
        return new ExecutionOrderLifecycleService(
            dbContext,
            new AuditLogService(dbContext, new CorrelationContextAccessor()),
            timeProvider,
            NullLogger<ExecutionOrderLifecycleService>.Instance,
            spotPortfolioAccountingService: spotPortfolioAccountingService);
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private static async Task<Guid> SeedOrderAsync(
        ApplicationDbContext dbContext,
        ExecutionOrderState state,
        decimal filledQuantity,
        ExchangeDataPlane plane = ExchangeDataPlane.Futures,
        Guid? botId = null,
        Guid? exchangeAccountId = null)
    {
        var orderId = Guid.NewGuid();
        dbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = orderId,
            OwnerUserId = "user-lifecycle",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            BotId = botId,
            ExchangeAccountId = exchangeAccountId ?? Guid.NewGuid(),
            Plane = plane,
            StrategyKey = "lifecycle-core",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.05m,
            Price = 65000m,
            FilledQuantity = filledQuantity,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            State = state,
            IdempotencyKey = $"lifecycle_{orderId:N}",
            RootCorrelationId = "corr-lifecycle-root",
            ExternalOrderId = "binance-order-1",
            SubmittedAtUtc = new DateTime(2026, 4, 3, 11, 59, 0, DateTimeKind.Utc),
            SubmittedToBroker = true,
            LastStateChangedAtUtc = new DateTime(2026, 4, 3, 11, 59, 0, DateTimeKind.Utc)
        });
        await dbContext.SaveChangesAsync();

        return orderId;
    }

    private static BinanceOrderStatusSnapshot CreateSnapshot(
        Guid orderId,
        string status,
        decimal executedQuantity,
        DateTime eventTimeUtc,
        ExchangeDataPlane plane = ExchangeDataPlane.Futures)
    {
        return new BinanceOrderStatusSnapshot(
            "BTCUSDT",
            "binance-order-1",
            ExecutionClientOrderId.Create(orderId),
            status,
            0.05m,
            executedQuantity,
            0m,
            executedQuantity > 0m ? 65000m : 0m,
            0m,
            0m,
            eventTimeUtc,
            "Binance.PrivateStream.ExecutionReport",
            Plane: plane);
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeSpotPortfolioAccountingService(SpotPortfolioApplyResult? result) : ISpotPortfolioAccountingService
    {
        public Task<SpotPortfolioApplyResult?> ApplyAsync(
            ExecutionOrder order,
            BinanceOrderStatusSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(result);
        }
    }
}
