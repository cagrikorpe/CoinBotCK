namespace CoinBot.Infrastructure.Execution;

public sealed record VirtualExecutionWatchdogRunResult(
    int AdvancedOrderCount,
    int RepricedPositionCount,
    int ProtectiveDispatchCount);
