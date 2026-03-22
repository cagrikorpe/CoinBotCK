namespace CoinBot.Application.Abstractions.Strategies;

public sealed class StrategyRuleEvaluationException(string message) : InvalidOperationException(message);
