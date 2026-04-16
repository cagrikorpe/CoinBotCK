namespace CoinBot.Application.Abstractions.Strategies;

public interface IStrategyDefinitionValidator
{
    StrategyDefinitionValidationSnapshot Validate(StrategyRuleDocument document);


    StrategyDefinitionValidationSnapshot ValidateForBotDirectionMode(
        StrategyRuleDocument document,
        CoinBot.Domain.Enums.TradingBotDirectionMode directionMode);
}
