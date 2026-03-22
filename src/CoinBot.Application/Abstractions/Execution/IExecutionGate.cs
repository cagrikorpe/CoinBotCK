namespace CoinBot.Application.Abstractions.Execution;

public interface IExecutionGate
{
    Task<GlobalExecutionSwitchSnapshot> EnsureExecutionAllowedAsync(
        ExecutionGateRequest request,
        CancellationToken cancellationToken = default);
}
