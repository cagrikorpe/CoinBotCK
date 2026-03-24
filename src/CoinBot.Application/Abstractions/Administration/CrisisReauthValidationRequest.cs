using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Administration;

public sealed record CrisisReauthValidationRequest(
    string ActorUserId,
    CrisisEscalationLevel Level,
    string Scope,
    string Token,
    string? CorrelationId);
