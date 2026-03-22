namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategyRuleDocument(
    int SchemaVersion,
    StrategyRuleNode? Entry,
    StrategyRuleNode? Exit,
    StrategyRuleNode? Risk);
