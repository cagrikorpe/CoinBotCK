using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.ExchangeCredentials;
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

public sealed class ExecutionReconciliationServiceTests
{
    [Fact]
    public async Task RunOnceAsync_DetectsDrift_AndAlignsOpenOrderState()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var auditLogService = new AuditLogService(dbContext, new CorrelationContextAccessor());
        var lifecycleService = new ExecutionOrderLifecycleService(
            dbContext,
            auditLogService,
            timeProvider,
            NullLogger<ExecutionOrderLifecycleService>.Instance);
        var service = new ExecutionReconciliationService(
            dbContext,
            new FakeExchangeCredentialService(),
            new FakePrivateRestClient(
                new BinanceOrderStatusSnapshot(
                    "BTCUSDT",
                    "binance-order-1",
                    ExecutionClientOrderId.Create(new Guid("11111111-1111-1111-1111-111111111111")),
                    "PARTIALLY_FILLED",
                    0.05m,
                    0.02m,
                    1280m,
                    64000m,
                    0m,
                    0m,
                    timeProvider.GetUtcNow().UtcDateTime,
                    "Binance.PrivateRest.Order")),
            new FakeSpotPrivateRestClient(),
            lifecycleService,
            NullLogger<ExecutionReconciliationService>.Instance);
        var executionOrderId = new Guid("11111111-1111-1111-1111-111111111111");

        dbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = executionOrderId,
            OwnerUserId = "user-reconcile",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            ExchangeAccountId = Guid.NewGuid(),
            StrategyKey = "reconcile-core",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.05m,
            Price = 65000m,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            State = ExecutionOrderState.Submitted,
            IdempotencyKey = $"reconcile_{executionOrderId:N}",
            RootCorrelationId = "root-reconcile-correlation-1",
            ExternalOrderId = "binance-order-1",
            SubmittedAtUtc = new DateTime(2026, 3, 22, 11, 55, 0, DateTimeKind.Utc),
            LastStateChangedAtUtc = new DateTime(2026, 3, 22, 11, 55, 0, DateTimeKind.Utc)
        });
        await dbContext.SaveChangesAsync();

        var reconciledCount = await service.RunOnceAsync();

        var order = await dbContext.ExecutionOrders.SingleAsync(entity => entity.Id == executionOrderId);
        var transition = await dbContext.ExecutionOrderTransitions.SingleAsync(entity => entity.ExecutionOrderId == executionOrderId);
        var auditLog = await dbContext.AuditLogs
            .OrderByDescending(entity => entity.CreatedDate)
            .FirstAsync();

        Assert.Equal(1, reconciledCount);
        Assert.Equal(ExecutionOrderState.PartiallyFilled, order.State);
        Assert.Equal(0.02m, order.FilledQuantity);
        Assert.Equal(64000m, order.AverageFillPrice);
        Assert.Equal(ExchangeStateDriftStatus.DriftDetected, order.ReconciliationStatus);
        Assert.Contains("LocalState=Submitted", order.ReconciliationSummary, StringComparison.Ordinal);
        Assert.Equal(ExecutionOrderState.PartiallyFilled, transition.State);
        Assert.Equal("ExecutionOrder.Reconciliation", auditLog.Action);
        Assert.Equal("Reconciled:DriftDetected", auditLog.Outcome);
        Assert.Equal("root-reconcile-correlation-1", auditLog.CorrelationId);
    }

    [Fact]
    public async Task RunOnceAsync_UsesSpotRestClient_WhenExecutionOrderPlaneIsSpot()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero));
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var auditLogService = new AuditLogService(dbContext, new CorrelationContextAccessor());
        var lifecycleService = new ExecutionOrderLifecycleService(
            dbContext,
            auditLogService,
            timeProvider,
            NullLogger<ExecutionOrderLifecycleService>.Instance);
        var futuresClient = new FakePrivateRestClient(
            new BinanceOrderStatusSnapshot(
                "BTCUSDT",
                "futures-order-1",
                ExecutionClientOrderId.Create(new Guid("22222222-2222-2222-2222-222222222222")),
                "NEW",
                0.05m,
                0m,
                0m,
                0m,
                0m,
                0m,
                timeProvider.GetUtcNow().UtcDateTime,
                "Binance.PrivateRest.Order"));
        var spotClient = new FakeSpotPrivateRestClient(
            new BinanceOrderStatusSnapshot(
                "BTCUSDT",
                "spot-order-1",
                ExecutionClientOrderId.Create(new Guid("33333333-3333-3333-3333-333333333333")),
                "FILLED",
                0.05m,
                0.05m,
                3200m,
                64000m,
                0m,
                0m,
                timeProvider.GetUtcNow().UtcDateTime,
                "Binance.SpotPrivateRest.Order",
                Plane: ExchangeDataPlane.Spot));
        var service = new ExecutionReconciliationService(
            dbContext,
            new FakeExchangeCredentialService(),
            futuresClient,
            spotClient,
            lifecycleService,
            NullLogger<ExecutionReconciliationService>.Instance);
        var executionOrderId = new Guid("33333333-3333-3333-3333-333333333333");

        dbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = executionOrderId,
            OwnerUserId = "user-reconcile-spot",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            ExchangeAccountId = Guid.NewGuid(),
            Plane = ExchangeDataPlane.Spot,
            StrategyKey = "reconcile-spot-core",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.05m,
            Price = 65000m,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            State = ExecutionOrderState.Submitted,
            IdempotencyKey = $"reconcile_spot_{executionOrderId:N}",
            RootCorrelationId = "root-reconcile-spot-correlation-1",
            ExternalOrderId = "spot-order-1",
            SubmittedAtUtc = new DateTime(2026, 4, 5, 11, 55, 0, DateTimeKind.Utc),
            LastStateChangedAtUtc = new DateTime(2026, 4, 5, 11, 55, 0, DateTimeKind.Utc)
        });
        await dbContext.SaveChangesAsync();

        var reconciledCount = await service.RunOnceAsync();
        var order = await dbContext.ExecutionOrders.SingleAsync(entity => entity.Id == executionOrderId);

        Assert.Equal(1, reconciledCount);
        Assert.Equal(0, futuresClient.GetOrderCalls);
        Assert.Equal(1, spotClient.GetOrderCalls);
        Assert.Equal(ExecutionOrderState.Filled, order.State);
    }


    [Fact]
    public async Task RunOnceAsync_ReconcilesBrokerSubmittedTerminalOrder_WhenReconciliationIsUnknown()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 7, 9, 0, 0, TimeSpan.Zero));
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var auditLogService = new AuditLogService(dbContext, new CorrelationContextAccessor());
        var lifecycleService = new ExecutionOrderLifecycleService(
            dbContext,
            auditLogService,
            timeProvider,
            NullLogger<ExecutionOrderLifecycleService>.Instance);
        var executionOrderId = new Guid("44444444-4444-4444-4444-444444444444");
        var service = new ExecutionReconciliationService(
            dbContext,
            new FakeExchangeCredentialService(),
            new FakePrivateRestClient(
                new BinanceOrderStatusSnapshot(
                    "BTCUSDT",
                    "binance-order-terminal-1",
                    ExecutionClientOrderId.Create(executionOrderId),
                    "FILLED",
                    0.05m,
                    0.05m,
                    3200m,
                    64000m,
                    0m,
                    0m,
                    timeProvider.GetUtcNow().UtcDateTime,
                    "Binance.PrivateRest.Order")),
            new FakeSpotPrivateRestClient(),
            lifecycleService,
            NullLogger<ExecutionReconciliationService>.Instance);

        dbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = executionOrderId,
            OwnerUserId = "user-reconcile-terminal",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            ExchangeAccountId = Guid.NewGuid(),
            Plane = ExchangeDataPlane.Futures,
            StrategyKey = "reconcile-terminal-core",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.05m,
            Price = 64000m,
            FilledQuantity = 0.05m,
            AverageFillPrice = 64000m,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            State = ExecutionOrderState.Filled,
            IdempotencyKey = $"reconcile_terminal_{executionOrderId:N}",
            RootCorrelationId = "root-reconcile-terminal-correlation-1",
            ExternalOrderId = "binance-order-terminal-1",
            SubmittedToBroker = true,
            SubmittedAtUtc = new DateTime(2026, 4, 7, 8, 55, 0, DateTimeKind.Utc),
            ReconciliationStatus = ExchangeStateDriftStatus.Unknown,
            LastStateChangedAtUtc = new DateTime(2026, 4, 7, 8, 56, 0, DateTimeKind.Utc)
        });
        await dbContext.SaveChangesAsync();

        var reconciledCount = await service.RunOnceAsync();

        var order = await dbContext.ExecutionOrders.SingleAsync(entity => entity.Id == executionOrderId);
        var auditLog = await dbContext.AuditLogs
            .OrderByDescending(entity => entity.CreatedDate)
            .FirstAsync();

        Assert.Equal(1, reconciledCount);
        Assert.Equal(ExecutionOrderState.Filled, order.State);
        Assert.Equal(ExchangeStateDriftStatus.InSync, order.ReconciliationStatus);
        Assert.NotNull(order.LastReconciledAtUtc);
        Assert.Contains("FilledQuantity=0.05", order.ReconciliationSummary, StringComparison.Ordinal);
        Assert.Equal("ExecutionOrder.Reconciliation", auditLog.Action);
        Assert.Equal("Reconciled:InSync", auditLog.Outcome);
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
            Assert.Equal(ExchangeCredentialAccessPurpose.Synchronization, request.Purpose);

            return Task.FromResult(
                new ExchangeCredentialAccessResult(
                    "api-key",
                    "api-secret",
                    new ExchangeCredentialStateSnapshot(
                        request.ExchangeAccountId,
                        ExchangeCredentialStatus.Active,
                        "fingerprint",
                        "v1",
                        StoredAtUtc: null,
                        LastValidatedAtUtc: null,
                        LastAccessedAtUtc: null,
                        LastRotatedAtUtc: null,
                        RevalidateAfterUtc: null,
                        RotateAfterUtc: null)));
        }

        public Task<ExchangeCredentialStateSnapshot> StoreAsync(
            StoreExchangeCredentialsRequest request,
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

    private sealed class FakePrivateRestClient(BinanceOrderStatusSnapshot snapshot) : IBinancePrivateRestClient
    {
        public int GetOrderCalls { get; private set; }

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
            GetOrderCalls++;
            return Task.FromResult(snapshot);
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

    private sealed class FakeSpotPrivateRestClient(BinanceOrderStatusSnapshot? snapshot = null) : IBinanceSpotPrivateRestClient
    {
        public int GetOrderCalls { get; private set; }

        public Task<BinanceOrderPlacementResult> PlaceOrderAsync(
            BinanceOrderPlacementRequest request,
            CancellationToken cancellationToken = default)
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

        public Task<BinanceOrderStatusSnapshot> GetOrderAsync(
            BinanceOrderQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            GetOrderCalls++;
            return Task.FromResult(snapshot ?? throw new NotSupportedException());
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
}

