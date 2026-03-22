using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategyEvaluationContext(
    ExecutionEnvironment Mode,
    StrategyIndicatorSnapshot IndicatorSnapshot);
