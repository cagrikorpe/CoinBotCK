using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Risk;

public sealed record RiskPolicyEvaluationRequest(
    string OwnerUserId,
    Guid TradingStrategyId,
    Guid TradingStrategyVersionId,
    StrategySignalType SignalType,
    ExecutionEnvironment Environment,
    string Symbol,
    string Timeframe);
