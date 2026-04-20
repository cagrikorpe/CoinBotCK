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
    string? StrategyKey = null,
    string? Context = null,
    Guid? TradingStrategyId = null,
    Guid? TradingStrategyVersionId = null,
    string? Timeframe = null,
    Guid? CurrentExecutionOrderId = null,
    Guid? ReplacesExecutionOrderId = null,
    ExchangeDataPlane Plane = ExchangeDataPlane.Futures,
    Guid? ExchangeAccountId = null,
    bool? ReduceOnly = null);
