using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Autonomy;

public sealed record AutonomyDecisionRequest(
    string ActorUserId,
    string SuggestedAction,
    string Reason,
    decimal? ConfidenceScore = null,
    string? ScopeKey = null,
    string? UserId = null,
    string? Symbol = null,
    ExecutionEnvironment? Environment = null,
    ExecutionOrderSide? Side = null,
    decimal? Quantity = null,
    decimal? Price = null,
    string? CorrelationId = null,
    string? JobKey = null,
    DependencyCircuitBreakerKind? BreakerKind = null);
