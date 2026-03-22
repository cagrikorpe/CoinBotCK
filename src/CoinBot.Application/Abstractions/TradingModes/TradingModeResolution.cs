using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Execution;

public sealed record TradingModeResolution(
    ExecutionEnvironment GlobalDefaultMode,
    ExecutionEnvironment? UserOverrideMode,
    ExecutionEnvironment? BotOverrideMode,
    ExecutionEnvironment? StrategyPublishedMode,
    ExecutionEnvironment EffectiveMode,
    TradingModeResolutionSource ResolutionSource,
    string Reason,
    bool HasExplicitLiveApproval);
