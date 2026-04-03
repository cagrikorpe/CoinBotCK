namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategyRuleGroup(
    StrategyRuleGroupOperator Operator,
    IReadOnlyList<StrategyRuleNode> Rules,
    StrategyRuleMetadata? Metadata = null) : StrategyRuleNode;
