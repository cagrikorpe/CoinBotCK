using CoinBot.Application.Abstractions.Ai;
using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategySignalConfidenceSnapshot(
    int ScorePercentage,
    StrategySignalConfidenceBand Band,
    int MatchedRuleCount,
    int TotalRuleCount,
    bool IsDeterministic,
    bool IsRiskApproved,
    bool IsVetoed,
    RiskVetoReasonCode RiskReasonCode,
    bool IsVirtualRiskCheck,
    string Summary,
    decimal? CurrentDailyLossPercentage = null,
    decimal? MaxDailyLossPercentage = null,
    decimal? CurrentWeeklyLossPercentage = null,
    decimal? MaxWeeklyLossPercentage = null,
    decimal? CurrentLeverage = null,
    decimal? ProjectedLeverage = null,
    decimal? MaxLeverage = null,
    decimal? CurrentSymbolExposurePercentage = null,
    decimal? ProjectedSymbolExposurePercentage = null,
    decimal? MaxSymbolExposurePercentage = null,
    int? CurrentOpenPositionCount = null,
    int? ProjectedOpenPositionCount = null,
    int? MaxConcurrentPositions = null,
    string? RiskBaseAsset = null,
    decimal? CurrentCoinExposurePercentage = null,
    decimal? ProjectedCoinExposurePercentage = null,
    decimal? MaxCoinExposurePercentage = null,
    string? RiskScopeSummary = null,
    AiSignalEvaluationResult? AiEvaluation = null,
    string? AiOverlayDisposition = null,
    int AiOverlayBoostPoints = 0);
