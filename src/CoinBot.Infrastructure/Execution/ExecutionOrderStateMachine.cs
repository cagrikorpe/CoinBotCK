using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;

namespace CoinBot.Infrastructure.Execution;

internal static class ExecutionOrderStateMachine
{
    private static readonly IReadOnlyDictionary<ExecutionOrderState, IReadOnlySet<ExecutionOrderState>> AllowedTransitions =
        new Dictionary<ExecutionOrderState, IReadOnlySet<ExecutionOrderState>>
        {
            [ExecutionOrderState.Received] = new HashSet<ExecutionOrderState>
            {
                ExecutionOrderState.GatePassed,
                ExecutionOrderState.Rejected,
                ExecutionOrderState.Failed
            },
            [ExecutionOrderState.GatePassed] = new HashSet<ExecutionOrderState>
            {
                ExecutionOrderState.Dispatching,
                ExecutionOrderState.Rejected,
                ExecutionOrderState.Failed
            },
            [ExecutionOrderState.Dispatching] = new HashSet<ExecutionOrderState>
            {
                ExecutionOrderState.Submitted,
                ExecutionOrderState.Rejected,
                ExecutionOrderState.Failed
            },
            [ExecutionOrderState.Submitted] = new HashSet<ExecutionOrderState>
            {
                ExecutionOrderState.PartiallyFilled,
                ExecutionOrderState.Filled,
                ExecutionOrderState.Cancelled,
                ExecutionOrderState.Rejected,
                ExecutionOrderState.Failed
            },
            [ExecutionOrderState.PartiallyFilled] = new HashSet<ExecutionOrderState>
            {
                ExecutionOrderState.PartiallyFilled,
                ExecutionOrderState.Filled,
                ExecutionOrderState.Cancelled,
                ExecutionOrderState.Failed
            },
            [ExecutionOrderState.Filled] = new HashSet<ExecutionOrderState>(),
            [ExecutionOrderState.Cancelled] = new HashSet<ExecutionOrderState>(),
            [ExecutionOrderState.Rejected] = new HashSet<ExecutionOrderState>(),
            [ExecutionOrderState.Failed] = new HashSet<ExecutionOrderState>()
        };

    public static ExecutionOrderTransition CreateInitialTransition(
        ExecutionOrder order,
        DateTime occurredAtUtc,
        string correlationId,
        string? parentCorrelationId,
        string? detail = null)
    {
        order.State = ExecutionOrderState.Received;
        order.LastStateChangedAtUtc = occurredAtUtc;

        return CreateTransition(
            order,
            sequenceNumber: 1,
            state: ExecutionOrderState.Received,
            eventCode: "Received",
            detail,
            occurredAtUtc,
            correlationId,
            parentCorrelationId);
    }

    public static ExecutionOrderTransition Transition(
        ExecutionOrder order,
        int sequenceNumber,
        ExecutionOrderState targetState,
        string eventCode,
        DateTime occurredAtUtc,
        string correlationId,
        string? parentCorrelationId,
        string? detail = null)
    {
        if (!AllowedTransitions.TryGetValue(order.State, out var allowedStates) ||
            !allowedStates.Contains(targetState))
        {
            throw new InvalidOperationException(
                $"Execution order state '{order.State}' cannot transition to '{targetState}'.");
        }

        order.State = targetState;
        order.LastStateChangedAtUtc = occurredAtUtc;

        return CreateTransition(
            order,
            sequenceNumber,
            targetState,
            eventCode,
            detail,
            occurredAtUtc,
            correlationId,
            parentCorrelationId);
    }

    private static ExecutionOrderTransition CreateTransition(
        ExecutionOrder order,
        int sequenceNumber,
        ExecutionOrderState state,
        string eventCode,
        string? detail,
        DateTime occurredAtUtc,
        string correlationId,
        string? parentCorrelationId)
    {
        return new ExecutionOrderTransition
        {
            OwnerUserId = order.OwnerUserId,
            ExecutionOrderId = order.Id,
            SequenceNumber = sequenceNumber,
            State = state,
            EventCode = eventCode,
            Detail = detail,
            CorrelationId = correlationId,
            ParentCorrelationId = parentCorrelationId,
            OccurredAtUtc = occurredAtUtc
        };
    }
}
