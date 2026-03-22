namespace CoinBot.Application.Abstractions.Execution;

public enum TradingModeResolutionSource
{
    GlobalDefault = 0,
    UserOverride = 1,
    BotOverride = 2,
    LiveApprovalGuard = 3,
    StrategyPromotionGuard = 4,
    ContextGuard = 5
}
