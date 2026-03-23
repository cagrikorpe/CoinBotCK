using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Execution;

public sealed record ExecutionCommand(
    string Actor,
    string OwnerUserId,
    Guid TradingStrategyId,
    Guid TradingStrategyVersionId,
    Guid StrategySignalId,
    StrategySignalType SignalType,
    string StrategyKey,
    string Symbol,
    string Timeframe,
    string BaseAsset,
    string QuoteAsset,
    ExecutionOrderSide Side,
    ExecutionOrderType OrderType,
    decimal Quantity,
    decimal Price,
    Guid? BotId = null,
    Guid? ExchangeAccountId = null,
    bool? IsDemo = null,
    string? IdempotencyKey = null,
    string? CorrelationId = null,
    string? ParentCorrelationId = null,
    string? Context = null,
    decimal? StopLossPrice = null,
    decimal? TakeProfitPrice = null,
    Guid? ReplacesExecutionOrderId = null);
