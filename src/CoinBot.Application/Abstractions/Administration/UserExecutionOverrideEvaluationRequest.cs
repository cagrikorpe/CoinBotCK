using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Execution;

public sealed record UserExecutionOverrideEvaluationRequest(
    string UserId,
    string Symbol,
    ExecutionEnvironment Environment,
    ExecutionOrderSide Side,
    decimal Quantity,
    decimal Price,
    Guid? BotId = null,
    string? StrategyKey = null);
