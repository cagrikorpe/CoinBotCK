using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Autonomy;

public sealed record DependencyCircuitBreakerSnapshot(
    DependencyCircuitBreakerKind BreakerKind,
    CircuitBreakerStateCode StateCode,
    int ConsecutiveFailureCount,
    DateTime? LastFailureAtUtc,
    DateTime? LastSuccessAtUtc,
    DateTime? CooldownUntilUtc,
    DateTime? HalfOpenStartedAtUtc,
    DateTime? LastProbeAtUtc,
    string? LastErrorCode,
    string? LastErrorMessage,
    string? CorrelationId,
    bool IsPersisted)
{
    public bool IsClosed => StateCode == CircuitBreakerStateCode.Closed;
}
