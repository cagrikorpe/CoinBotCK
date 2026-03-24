using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Autonomy;

public sealed record DependencyCircuitBreakerHalfOpenRequest(
    DependencyCircuitBreakerKind BreakerKind,
    string ActorUserId,
    string? CorrelationId = null);
