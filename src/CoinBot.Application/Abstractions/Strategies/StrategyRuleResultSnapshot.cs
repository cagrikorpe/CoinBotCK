namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategyRuleResultSnapshot(
    bool Matched,
    StrategyRuleGroupOperator? GroupOperator,
    string? Path,
    StrategyRuleComparisonOperator? Comparison,
    string? Operand,
    StrategyRuleOperandKind? OperandKind,
    string? LeftValue,
    string? RightValue,
    IReadOnlyCollection<StrategyRuleResultSnapshot> Children);
