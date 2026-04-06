using CoinBot.Application.Abstractions.Features;

namespace CoinBot.Application.Abstractions.Strategies;

public sealed record GenerateStrategySignalsRequest(
    Guid TradingStrategyVersionId,
    StrategyEvaluationContext EvaluationContext,
    TradingFeatureSnapshotModel? FeatureSnapshot = null);
