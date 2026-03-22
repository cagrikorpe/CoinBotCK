namespace CoinBot.Application.Abstractions.Execution;

public sealed record TradingModeResolutionRequest(
    string? UserId = null,
    Guid? BotId = null,
    string? StrategyKey = null);
