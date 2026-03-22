namespace CoinBot.Application.Abstractions.Strategies;

public interface IStrategyRuleParser
{
    StrategyRuleDocument Parse(string definitionJson);
}
