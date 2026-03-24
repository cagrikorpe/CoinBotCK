namespace CoinBot.Application.Abstractions.Administration;

public sealed record ApprovalQueueDecisionRequest(
    string ApprovalReference,
    string ActorUserId,
    string? Reason = null,
    string? CorrelationId = null);
