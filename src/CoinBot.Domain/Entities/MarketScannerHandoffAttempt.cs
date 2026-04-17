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

    public string? RiskOutcome { get; set; }

    public string? RiskVetoReasonCode { get; set; }

    public string? RiskSummary { get; set; }

    public decimal? RiskCurrentDailyLossPercentage { get; set; }

    public decimal? RiskMaxDailyLossPercentage { get; set; }

    public decimal? RiskCurrentWeeklyLossPercentage { get; set; }

    public decimal? RiskMaxWeeklyLossPercentage { get; set; }

    public decimal? RiskCurrentLeverage { get; set; }

    public decimal? RiskProjectedLeverage { get; set; }

    public decimal? RiskMaxLeverage { get; set; }

    public decimal? RiskCurrentSymbolExposurePercentage { get; set; }

    public decimal? RiskProjectedSymbolExposurePercentage { get; set; }

    public decimal? RiskMaxSymbolExposurePercentage { get; set; }

    public int? RiskCurrentOpenPositions { get; set; }

    public int? RiskProjectedOpenPositions { get; set; }

    public int? RiskMaxConcurrentPositions { get; set; }

    public string? RiskBaseAsset { get; set; }

    public decimal? RiskCurrentCoinExposurePercentage { get; set; }

    public decimal? RiskProjectedCoinExposurePercentage { get; set; }

    public decimal? RiskMaxCoinExposurePercentage { get; set; }

    public string ExecutionRequestStatus { get; set; } = "Blocked";

    public ExecutionOrderSide? ExecutionSide { get; set; }

    public ExecutionOrderType? ExecutionOrderType { get; set; }

    public ExecutionEnvironment? ExecutionEnvironment { get; set; }

    public decimal? ExecutionQuantity { get; set; }

    public decimal? ExecutionPrice { get; set; }

    public string? BlockerCode { get; set; }

    public string? BlockerDetail { get; set; }

    public string? BlockerSummary { get; set; }

    public string? GuardSummary { get; set; }

    public string CorrelationId { get; set; } = string.Empty;

    public DateTime CompletedAtUtc { get; set; }
}
