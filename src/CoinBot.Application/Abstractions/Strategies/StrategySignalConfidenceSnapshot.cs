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
    string Summary);
