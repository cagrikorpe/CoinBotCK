namespace CoinBot.Application.Abstractions.Auditing;

public sealed record AuditLogWriteRequest(
    string Actor,
    string Action,
    string Target,
    string? Context,
    string? CorrelationId,
    string Outcome,
    string Environment);
