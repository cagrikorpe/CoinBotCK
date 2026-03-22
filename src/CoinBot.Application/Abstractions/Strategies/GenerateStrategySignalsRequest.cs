namespace CoinBot.Application.Abstractions.Strategies;

public sealed record GenerateStrategySignalsRequest(
    Guid TradingStrategyVersionId,
    StrategyEvaluationContext EvaluationContext);
