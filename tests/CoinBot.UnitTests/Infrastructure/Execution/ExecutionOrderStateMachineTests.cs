using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;

namespace CoinBot.UnitTests.Infrastructure.Execution;

public sealed class ExecutionOrderStateMachineTests
{
    [Fact]
    public void Transition_AllowsSubmittedCancelRequestedThenCancelled()
    {
        var order = CreateOrder(ExecutionOrderState.Submitted);

        var cancelRequestedTransition = ExecutionOrderStateMachine.Transition(
            order,
            sequenceNumber: 2,
            ExecutionOrderState.CancelRequested,
            "ExchangeCancelRequested",
            new DateTime(2026, 4, 3, 12, 0, 1, DateTimeKind.Utc),
            "corr-state-2",
            "corr-state-1",
            "Source=Binance.PrivateStream.ExecutionReport; ExchangeStatus=PENDING_CANCEL");
        var cancelledTransition = ExecutionOrderStateMachine.Transition(
            order,
            sequenceNumber: 3,
            ExecutionOrderState.Cancelled,
            "ExchangeCancelled",
            new DateTime(2026, 4, 3, 12, 0, 2, DateTimeKind.Utc),
            "corr-state-3",
            cancelRequestedTransition.CorrelationId,
            "Source=Binance.PrivateStream.ExecutionReport; ExchangeStatus=CANCELED");

        Assert.Equal(ExecutionOrderState.CancelRequested, cancelRequestedTransition.State);
        Assert.Equal(ExecutionOrderState.Cancelled, cancelledTransition.State);
        Assert.Equal(ExecutionOrderState.Cancelled, order.State);
    }

    [Fact]
    public void Transition_Throws_WhenTerminalStateReceivesIllegalTransition()
    {
        var order = CreateOrder(ExecutionOrderState.Filled);

        var exception = Assert.Throws<InvalidOperationException>(() => ExecutionOrderStateMachine.Transition(
            order,
            sequenceNumber: 2,
            ExecutionOrderState.Cancelled,
            "ExchangeCancelled",
            new DateTime(2026, 4, 3, 12, 0, 3, DateTimeKind.Utc),
            "corr-state-4",
            "corr-state-1",
            "late callback"));

        Assert.Equal("Execution order state 'Filled' cannot transition to 'Cancelled'.", exception.Message);
        Assert.False(ExecutionOrderStateMachine.CanTransition(ExecutionOrderState.Filled, ExecutionOrderState.Cancelled));
    }

    private static ExecutionOrder CreateOrder(ExecutionOrderState state)
    {
        return new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-state-machine",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            StrategyKey = "state-machine-core",
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
            State = state,
            IdempotencyKey = $"state_{Guid.NewGuid():N}",
            RootCorrelationId = "corr-state-1",
            LastStateChangedAtUtc = new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc)
        };
    }
}
