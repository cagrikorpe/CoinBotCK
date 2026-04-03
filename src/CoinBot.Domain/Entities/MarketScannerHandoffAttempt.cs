using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class MarketScannerHandoffAttempt : BaseEntity
{
    public Guid ScanCycleId { get; set; }

    public Guid? SelectedCandidateId { get; set; }

    public string? SelectedSymbol { get; set; }

    public string? SelectedTimeframe { get; set; }

    public DateTime SelectedAtUtc { get; set; }

    public int? CandidateRank { get; set; }

    public decimal? CandidateMarketScore { get; set; }

    public decimal? CandidateScore { get; set; }

    public string SelectionReason { get; set; } = string.Empty;

    public string? OwnerUserId { get; set; }

    public Guid? BotId { get; set; }

    public string? StrategyKey { get; set; }

    public Guid? TradingStrategyId { get; set; }

    public Guid? TradingStrategyVersionId { get; set; }

    public Guid? StrategySignalId { get; set; }

    public Guid? StrategySignalVetoId { get; set; }

    public string StrategyDecisionOutcome { get; set; } = "NotEvaluated";

    public string? StrategyVetoReasonCode { get; set; }

    public int? StrategyScore { get; set; }

    public string ExecutionRequestStatus { get; set; } = "Blocked";

    public ExecutionOrderSide? ExecutionSide { get; set; }

    public ExecutionOrderType? ExecutionOrderType { get; set; }

    public ExecutionEnvironment? ExecutionEnvironment { get; set; }

    public decimal? ExecutionQuantity { get; set; }

    public decimal? ExecutionPrice { get; set; }

    public string? BlockerCode { get; set; }

    public string? BlockerDetail { get; set; }

    public string? GuardSummary { get; set; }

    public string CorrelationId { get; set; } = string.Empty;

    public DateTime CompletedAtUtc { get; set; }
}
