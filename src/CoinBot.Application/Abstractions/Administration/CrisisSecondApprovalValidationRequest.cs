using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Administration;

public sealed record CrisisSecondApprovalValidationRequest(
    string ActorUserId,
    CrisisEscalationLevel Level,
    string Scope,
    string ApprovalReference,
    string Reason,
    CrisisEscalationPreview Preview,
    string? CorrelationId);
