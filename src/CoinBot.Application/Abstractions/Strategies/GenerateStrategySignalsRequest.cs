using CoinBot.Application.Abstractions.Features;
using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Strategies;

public sealed record GenerateStrategySignalsRequest(
    Guid TradingStrategyVersionId,
    StrategyEvaluationContext EvaluationContext,
    TradingFeatureSnapshotModel? FeatureSnapshot = null,
    ExecutionEnvironment? EffectiveExecutionEnvironment = null);
