namespace CoinBot.Application.Abstractions.Autonomy;

public sealed record AutonomyIncidentHookRequest(
    string ActorUserId,
    string Scope,
    string Summary,
    string Detail,
    string? CorrelationId);
