using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategySignalSnapshot(
    Guid StrategySignalId,
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
    DateTime GeneratedAtUtc,
    StrategySignalExplainabilityPayload ExplainabilityPayload);
