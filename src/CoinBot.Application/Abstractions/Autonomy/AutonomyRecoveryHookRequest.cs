namespace CoinBot.Application.Abstractions.Autonomy;

public sealed record AutonomyRecoveryHookRequest(
    string ActorUserId,
    string Scope,
    string Summary,
    string? CorrelationId);
