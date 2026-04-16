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
    StrategyRuleResultSnapshot? RiskRuleResult,
    StrategyTradeDirection Direction = StrategyTradeDirection.Neutral,
    StrategyTradeDirection EntryDirection = StrategyTradeDirection.Neutral,
    StrategyTradeDirection ExitDirection = StrategyTradeDirection.Neutral,
    StrategyRuleResultSnapshot? LongEntryRuleResult = null,
    StrategyRuleResultSnapshot? LongExitRuleResult = null,
    StrategyRuleResultSnapshot? ShortEntryRuleResult = null,
    StrategyRuleResultSnapshot? ShortExitRuleResult = null);
