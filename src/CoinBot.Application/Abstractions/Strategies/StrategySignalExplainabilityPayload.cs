using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategySignalExplainabilityPayload(
    int ExplainabilitySchemaVersion,
    Guid TradingStrategyId,
    Guid TradingStrategyVersionId,
    int StrategyVersionNumber,
    int StrategySchemaVersion,
    ExecutionEnvironment Mode,
    StrategyIndicatorSnapshot IndicatorSnapshot,
    StrategyEvaluationResult RuleResultSnapshot,
    StrategySignalConfidenceSnapshot ConfidenceSnapshot,
    StrategySignalLogExplainabilitySnapshot UiLog,
    StrategySignalDuplicateSuppressionSnapshot DuplicateSignalSuppression);
