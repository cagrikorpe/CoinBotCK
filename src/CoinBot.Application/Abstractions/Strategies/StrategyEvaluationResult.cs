namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategyEvaluationResult(
    bool HasEntryRules,
    bool EntryMatched,
    bool HasExitRules,
    bool ExitMatched,
    bool HasRiskRules,
    bool RiskPassed,
    StrategyRuleResultSnapshot? EntryRuleResult,
    StrategyRuleResultSnapshot? ExitRuleResult,
    StrategyRuleResultSnapshot? RiskRuleResult);
