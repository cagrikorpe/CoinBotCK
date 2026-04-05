namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategyEvaluationReportSnapshot(
    Guid TradingStrategyId,
    Guid TradingStrategyVersionId,
    int StrategyVersionNumber,
    string StrategyKey,
    string StrategyDisplayName,
    string? TemplateKey,
    string? TemplateName,
    string Symbol,
    string Timeframe,
    DateTime EvaluatedAtUtc,
    string Outcome,
    int AggregateScore,
    int PassedRuleCount,
    int FailedRuleCount,
    StrategyEvaluationResult RuleEvaluation,
    IReadOnlyCollection<string> PassedRules,
    IReadOnlyCollection<string> FailedRules,
    string ExplainabilitySummary,
    int? TemplateRevisionNumber = null,
    string? TemplateSource = null);
