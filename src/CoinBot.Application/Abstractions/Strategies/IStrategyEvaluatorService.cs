namespace CoinBot.Application.Abstractions.Strategies;

public interface IStrategyEvaluatorService
{
    StrategyEvaluationResult Evaluate(string definitionJson, StrategyEvaluationContext context);

    StrategyEvaluationResult Evaluate(StrategyRuleDocument document, StrategyEvaluationContext context);
}
