using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Autonomy;

public sealed record PreFlightSimulationRequest(
    string SuggestedAction,
    decimal ConfidenceScore,
    string? ScopeKey = null,
    string? UserId = null,
    string? Symbol = null,
    ExecutionEnvironment? Environment = null,
    ExecutionOrderSide? Side = null,
    decimal? Quantity = null,
    decimal? Price = null,
    DependencyCircuitBreakerKind? BreakerKind = null);
