namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategyRuleOperand(
    StrategyRuleOperandKind Kind,
    string Value);
