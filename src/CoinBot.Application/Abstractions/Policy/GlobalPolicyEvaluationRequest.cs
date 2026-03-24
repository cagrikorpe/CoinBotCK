using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Policy;

public sealed record GlobalPolicyEvaluationRequest(
    string UserId,
    string Symbol,
    ExecutionEnvironment Environment,
    ExecutionOrderSide Side,
    decimal Quantity,
    decimal Price,
    Guid? BotId = null,
    string? StrategyKey = null);
