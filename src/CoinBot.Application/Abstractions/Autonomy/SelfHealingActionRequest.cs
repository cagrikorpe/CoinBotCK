using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Autonomy;

public sealed record SelfHealingActionRequest(
    string ActorUserId,
    string SuggestedAction,
    string Reason,
    string? CorrelationId = null,
    string? JobKey = null,
    string? Symbol = null,
    DependencyCircuitBreakerKind? BreakerKind = null);
