using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Administration;

public sealed record CrisisIncidentHookRequest(
    string ActorUserId,
    CrisisEscalationLevel Level,
    string Scope,
    string Summary,
    string Detail,
    string? CorrelationId,
    string? CommandId = null);
