namespace CoinBot.Application.Abstractions.Autonomy;

public sealed record PreFlightSimulationResult(
    int AffectedOpenPositionCount,
    decimal EstimatedOpenPositionExposure,
    string RiskLimitImpact,
    string LiquidityImpact,
    string RateLimitImpact,
    decimal FalsePositiveProbability,
    bool IsGlobalPolicyCompliant,
    bool HasRestrictionConflict,
    IReadOnlyCollection<string> AffectedUsers,
    IReadOnlyCollection<string> AffectedSymbols);
