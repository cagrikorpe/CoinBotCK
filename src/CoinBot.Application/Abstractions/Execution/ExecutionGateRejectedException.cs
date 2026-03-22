using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Execution;

public sealed class ExecutionGateRejectedException : InvalidOperationException
{
    public ExecutionGateRejectedException(
        ExecutionGateBlockedReason reason,
        ExecutionEnvironment requestedEnvironment,
        string message)
        : base(message)
    {
        Reason = reason;
        RequestedEnvironment = requestedEnvironment;
    }

    public ExecutionGateBlockedReason Reason { get; }

    public ExecutionEnvironment RequestedEnvironment { get; }
}
