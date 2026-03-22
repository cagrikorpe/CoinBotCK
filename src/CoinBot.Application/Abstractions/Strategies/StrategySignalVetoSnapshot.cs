using CoinBot.Application.Abstractions.Risk;
using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategySignalVetoSnapshot(
    Guid StrategySignalVetoId,
    Guid TradingStrategyId,
    Guid TradingStrategyVersionId,
    int StrategyVersionNumber,
    int StrategySchemaVersion,
    StrategySignalType SignalType,
    ExecutionEnvironment Mode,
    string Symbol,
    string Timeframe,
    DateTime IndicatorOpenTimeUtc,
    DateTime IndicatorCloseTimeUtc,
    DateTime IndicatorReceivedAtUtc,
    DateTime EvaluatedAtUtc,
    RiskVetoResult RiskEvaluation);
