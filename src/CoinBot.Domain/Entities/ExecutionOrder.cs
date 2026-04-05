using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class ExecutionOrder : UserOwnedEntity
{
    public Guid TradingStrategyId { get; set; }

    public Guid TradingStrategyVersionId { get; set; }

    public Guid StrategySignalId { get; set; }

    public StrategySignalType SignalType { get; set; }

    public Guid? BotId { get; set; }

    public Guid? ExchangeAccountId { get; set; }

    public ExchangeDataPlane Plane { get; set; } = ExchangeDataPlane.Futures;

    public string StrategyKey { get; set; } = string.Empty;

    public string Symbol { get; set; } = string.Empty;

    public string Timeframe { get; set; } = string.Empty;

    public string BaseAsset { get; set; } = string.Empty;

    public string QuoteAsset { get; set; } = string.Empty;

    public ExecutionOrderSide Side { get; set; }

    public ExecutionOrderType OrderType { get; set; }

    public decimal Quantity { get; set; }

    public decimal Price { get; set; }

    public decimal FilledQuantity { get; set; }

    public decimal? AverageFillPrice { get; set; }

    public DateTime? LastFilledAtUtc { get; set; }

    public decimal? StopLossPrice { get; set; }

    public decimal? TakeProfitPrice { get; set; }

    public bool ReduceOnly { get; set; }

    public Guid? ReplacesExecutionOrderId { get; set; }

    public ExecutionEnvironment ExecutionEnvironment { get; set; }

    public ExecutionOrderExecutorKind ExecutorKind { get; set; } = ExecutionOrderExecutorKind.Unknown;

    public ExecutionOrderState State { get; set; } = ExecutionOrderState.Received;

    public string IdempotencyKey { get; set; } = string.Empty;

    public string RootCorrelationId { get; set; } = string.Empty;

    public string? ParentCorrelationId { get; set; }

    public string? ExternalOrderId { get; set; }

    public string? FailureCode { get; set; }

    public string? FailureDetail { get; set; }

    public ExecutionRejectionStage RejectionStage { get; set; } = ExecutionRejectionStage.None;

    public bool SubmittedToBroker { get; set; }

    public bool RetryEligible { get; set; }

    public bool CooldownApplied { get; set; }

    public bool DuplicateSuppressed { get; set; }

    public DateTime? SubmittedAtUtc { get; set; }

    public DateTime? LastReconciledAtUtc { get; set; }

    public ExchangeStateDriftStatus ReconciliationStatus { get; set; } = ExchangeStateDriftStatus.Unknown;

    public string? ReconciliationSummary { get; set; }

    public DateTime? LastDriftDetectedAtUtc { get; set; }

    public DateTime LastStateChangedAtUtc { get; set; }
}
