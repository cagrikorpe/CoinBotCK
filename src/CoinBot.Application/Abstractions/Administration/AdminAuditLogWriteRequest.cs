namespace CoinBot.Application.Abstractions.Administration;

public sealed record AdminAuditLogWriteRequest(
    string ActorUserId,
    string ActionType,
    string TargetType,
    string? TargetId,
    string? OldValueSummary,
    string? NewValueSummary,
    string Reason,
    string? IpAddress,
    string? UserAgent,
    string? CorrelationId);
