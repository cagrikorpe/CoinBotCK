using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Execution;

public sealed record TradingStrategyPromotionResult(
    Guid StrategyId,
    string StrategyKey,
    StrategyPromotionState PromotionState,
    ExecutionEnvironment? PublishedMode,
    DateTime? PublishedAtUtc,
    bool HasExplicitLiveApproval);
