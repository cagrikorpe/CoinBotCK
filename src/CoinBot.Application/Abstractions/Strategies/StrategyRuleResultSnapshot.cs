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
    IReadOnlyCollection<StrategyRuleResultSnapshot> Children,
    string? RuleId = null,
    string? RuleType = null,
    string? Timeframe = null,
    decimal Weight = 1m,
    bool Enabled = true,
    string? Group = null,
    string? Reason = null);
