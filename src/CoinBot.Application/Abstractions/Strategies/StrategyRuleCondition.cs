namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategyRuleCondition(
    string Path,
    StrategyRuleComparisonOperator Comparison,
    StrategyRuleOperand Operand,
    StrategyRuleMetadata? Metadata = null) : StrategyRuleNode;
