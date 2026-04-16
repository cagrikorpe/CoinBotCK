namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategyRuleDocument(
    int SchemaVersion,
    StrategyRuleNode? Entry,
    StrategyRuleNode? Exit,
    StrategyRuleNode? Risk,
    StrategyDefinitionMetadata? Metadata = null,
    StrategyTradeDirection Direction = StrategyTradeDirection.Long,
    StrategyRuleNode? LongEntry = null,
    StrategyRuleNode? LongExit = null,
    StrategyRuleNode? ShortEntry = null,
    StrategyRuleNode? ShortExit = null)
{
    public bool HasDirectionalRoots =>
        LongEntry is not null ||
        LongExit is not null ||
        ShortEntry is not null ||
        ShortExit is not null;
}
