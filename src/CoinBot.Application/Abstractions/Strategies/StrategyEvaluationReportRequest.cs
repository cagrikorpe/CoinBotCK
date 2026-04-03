namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategyEvaluationReportRequest(
    Guid TradingStrategyId,
    Guid TradingStrategyVersionId,
    int StrategyVersionNumber,
    string StrategyKey,
    string StrategyDisplayName,
    string DefinitionJson,
    StrategyEvaluationContext EvaluationContext,
    DateTime EvaluatedAtUtc);
