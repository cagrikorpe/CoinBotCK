namespace CoinBot.Application.Abstractions.Strategies;

public sealed class StrategyRuleParseException(string message) : InvalidOperationException(message);
