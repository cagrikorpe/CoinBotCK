using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Administration;

public sealed record CrisisEscalationExecuteRequest(
    CrisisEscalationLevel Level,
    string Scope,
    string CommandId,
    string ActorUserId,
    string ExecutionActor,
    string? Reason,
    string? ReasonCode,
    string? Message,
    string PreviewStamp,
    string? CorrelationId,
    string? ReauthToken,
    string? SecondApprovalReference,
    string? RemoteIpAddress);
