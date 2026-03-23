namespace CoinBot.Application.Abstractions.Execution;

public interface IExecutionEngine
{
    Task<ExecutionDispatchResult> DispatchAsync(
        ExecutionCommand command,
        CancellationToken cancellationToken = default);
}
