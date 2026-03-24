namespace CoinBot.Application.Abstractions.Policy;

public sealed record ExecutionGuardPolicy(
    decimal? MaxOrderNotional,
    decimal? MaxPositionNotional,
    int? MaxDailyTrades,
    bool CloseOnlyBlocksNewPositions)
{
    public static ExecutionGuardPolicy CreateDefault()
    {
        return new ExecutionGuardPolicy(
            MaxOrderNotional: 1_000_000m,
            MaxPositionNotional: null,
            MaxDailyTrades: null,
            CloseOnlyBlocksNewPositions: true);
    }
}
