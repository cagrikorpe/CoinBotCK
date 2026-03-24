using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class DependencyCircuitBreakerState : BaseEntity
{
    public DependencyCircuitBreakerKind BreakerKind { get; set; } = DependencyCircuitBreakerKind.WebSocket;

    public CircuitBreakerStateCode StateCode { get; set; } = CircuitBreakerStateCode.Closed;

    public int ConsecutiveFailureCount { get; set; }

    public DateTime? LastFailureAtUtc { get; set; }

    public DateTime? LastSuccessAtUtc { get; set; }

    public DateTime? CooldownUntilUtc { get; set; }

    public DateTime? HalfOpenStartedAtUtc { get; set; }

    public DateTime? LastProbeAtUtc { get; set; }

    public string? LastErrorCode { get; set; }

    public string? LastErrorMessage { get; set; }

    public string? CorrelationId { get; set; }
}
