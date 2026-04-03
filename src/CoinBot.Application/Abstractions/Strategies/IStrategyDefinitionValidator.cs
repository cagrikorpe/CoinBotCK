namespace CoinBot.Application.Abstractions.Strategies;

public interface IStrategyDefinitionValidator
{
    StrategyDefinitionValidationSnapshot Validate(StrategyRuleDocument document);
}
