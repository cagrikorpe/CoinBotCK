using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Autonomy;

public sealed record DependencyCircuitBreakerFailureRequest(
    DependencyCircuitBreakerKind BreakerKind,
    string ActorUserId,
    string ErrorCode,
    string ErrorMessage,
    string? CorrelationId = null);
