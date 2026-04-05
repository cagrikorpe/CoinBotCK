namespace CoinBot.Application.Abstractions.Strategies;

public enum StrategyRuleComparisonOperator
{
    Equals = 0,
    NotEquals = 1,
    GreaterThan = 2,
    GreaterThanOrEqual = 3,
    LessThan = 4,
    LessThanOrEqual = 5,
    Between = 6,
    NotBetween = 7,
    Contains = 8,
    StartsWith = 9,
    EndsWith = 10
}
