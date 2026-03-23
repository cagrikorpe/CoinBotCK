using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;

namespace CoinBot.Infrastructure.Execution;

public interface IExecutionTargetExecutor
{
    ExecutionOrderExecutorKind Kind { get; }

    Task<ExecutionTargetDispatchResult> DispatchAsync(
        ExecutionOrder order,
        ExecutionCommand command,
        CancellationToken cancellationToken = default);
}
