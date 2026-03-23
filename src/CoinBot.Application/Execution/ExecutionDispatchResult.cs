namespace CoinBot.Application.Abstractions.Execution;

public sealed record ExecutionDispatchResult(
    ExecutionOrderSnapshot Order,
    bool IsDuplicate);
