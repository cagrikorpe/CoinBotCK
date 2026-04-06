using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Persistence;
using CoinBot.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoinBot.IntegrationTests.Execution;

public sealed class ExecutionOrderLifecycleIntegrationTests
{
    [Fact]
    public async Task ApplyExchangeUpdateAsync_SuppressesDuplicateSubmittedTransition_WhenConcurrentWinnerAlreadyPersisted_OnSqlServer()
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString($"CoinBotLifecycleRace_{Guid.NewGuid():N}");
        var observedAtUtc = new DateTime(2026, 4, 6, 18, 30, 0, DateTimeKind.Utc);
        var orderId = Guid.NewGuid();

        await using var seedContext = CreateContext(connectionString);

        try
        {
            await seedContext.Database.EnsureDeletedAsync();
            await seedContext.Database.MigrateAsync();
            await SeedDispatchingOrderAsync(seedContext, orderId, observedAtUtc);

            await using var primaryContext = CreateContext(connectionString);
            var lifecycleService = new ExecutionOrderLifecycleService(
                primaryContext,
                new RaceInjectingAuditLogService(connectionString, orderId, observedAtUtc),
                TimeProvider.System,
                NullLogger<ExecutionOrderLifecycleService>.Instance);

            var applied = await lifecycleService.ApplyExchangeUpdateAsync(
                new BinanceOrderStatusSnapshot(
                    "BTCUSDT",
                    "binance-order-race",
                    ExecutionClientOrderId.Create(orderId),
                    "NEW",
                    0.002m,
                    0m,
                    0m,
                    0m,
                    0m,
                    0m,
                    observedAtUtc,
                    "Binance.PrivateStream.ExecutionReport",
                    Plane: ExchangeDataPlane.Futures));

            await using var verificationContext = CreateContext(connectionString);
            var order = await verificationContext.ExecutionOrders.SingleAsync(entity => entity.Id == orderId);
            var transitions = await verificationContext.ExecutionOrderTransitions
                .Where(entity => entity.ExecutionOrderId == orderId)
                .OrderBy(entity => entity.SequenceNumber)
                .ToListAsync();

            Assert.True(applied);
            Assert.Equal(ExecutionOrderState.Submitted, order.State);
            Assert.True(order.SubmittedToBroker);
            Assert.Equal("binance-order-race", order.ExternalOrderId);
            Assert.Equal(observedAtUtc, order.SubmittedAtUtc);
            Assert.Equal([1, 2, 3, 4], transitions.Select(entity => entity.SequenceNumber).ToArray());
            Assert.Equal(
                [ExecutionOrderState.Received, ExecutionOrderState.GatePassed, ExecutionOrderState.Dispatching, ExecutionOrderState.Submitted],
                transitions.Select(entity => entity.State).ToArray());
            Assert.Single(transitions, entity => entity.SequenceNumber == 4 && entity.EventCode == "ExchangeSubmitted");
        }
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }

    private static ApplicationDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private static async Task SeedDispatchingOrderAsync(ApplicationDbContext dbContext, Guid orderId, DateTime observedAtUtc)
    {
        dbContext.Users.Add(new ApplicationUser
        {
            Id = "user-lifecycle-race",
            UserName = "user-lifecycle-race@coinbot.test",
            NormalizedUserName = "USER-LIFECYCLE-RACE@COINBOT.TEST",
            Email = "user-lifecycle-race@coinbot.test",
            NormalizedEmail = "USER-LIFECYCLE-RACE@COINBOT.TEST",
            FullName = "Lifecycle Race"
        });

        await dbContext.SaveChangesAsync();
        dbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = orderId,
            OwnerUserId = "user-lifecycle-race",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            ExchangeAccountId = Guid.NewGuid(),
            Plane = ExchangeDataPlane.Futures,
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
            State = ExecutionOrderState.Dispatching,
            IdempotencyKey = $"lifecycle-race:{orderId:N}",
            RootCorrelationId = "corr-lifecycle-race",
            SubmittedToBroker = true,
            LastStateChangedAtUtc = observedAtUtc.AddSeconds(-1)
        });

        dbContext.ExecutionOrderTransitions.AddRange(
            CreateTransition(orderId, 1, ExecutionOrderState.Received, "Received", observedAtUtc.AddSeconds(-3)),
            CreateTransition(orderId, 2, ExecutionOrderState.GatePassed, "GatePassed", observedAtUtc.AddSeconds(-2)),
            CreateTransition(orderId, 3, ExecutionOrderState.Dispatching, "Dispatching", observedAtUtc.AddSeconds(-1)));

        await dbContext.SaveChangesAsync();
    }

    private static ExecutionOrderTransition CreateTransition(Guid orderId, int sequenceNumber, ExecutionOrderState state, string eventCode, DateTime occurredAtUtc)
    {
        return new ExecutionOrderTransition
        {
            OwnerUserId = "user-lifecycle-race",
            ExecutionOrderId = orderId,
            SequenceNumber = sequenceNumber,
            State = state,
            EventCode = eventCode,
            Detail = eventCode,
            CorrelationId = $"corr-lifecycle-race-{sequenceNumber}",
            ParentCorrelationId = sequenceNumber == 1 ? "corr-lifecycle-race" : $"corr-lifecycle-race-{sequenceNumber - 1}",
            OccurredAtUtc = occurredAtUtc
        };
    }

    private sealed class RaceInjectingAuditLogService(string connectionString, Guid orderId, DateTime observedAtUtc) : IAuditLogService
    {
        private bool hasInjected;

        public async Task WriteAsync(AuditLogWriteRequest request, CancellationToken cancellationToken = default)
        {
            if (hasInjected)
            {
                return;
            }

            hasInjected = true;

            await using var competingContext = CreateContext(connectionString);
            var order = await competingContext.ExecutionOrders.SingleAsync(entity => entity.Id == orderId, cancellationToken);
            order.State = ExecutionOrderState.Submitted;
            order.ExternalOrderId = "binance-order-race";
            order.SubmittedAtUtc = observedAtUtc;
            order.SubmittedToBroker = true;
            order.LastStateChangedAtUtc = observedAtUtc;

            competingContext.ExecutionOrderTransitions.Add(
                CreateTransition(orderId, 4, ExecutionOrderState.Submitted, "ExchangeSubmitted", observedAtUtc));

            await competingContext.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }
}


