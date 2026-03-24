using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Administration;

public sealed record CrisisRecoveryHookRequest(
    string ActorUserId,
    CrisisEscalationLevel Level,
    string Scope,
    string Summary,
    string? CorrelationId,
    string? CommandId = null);
