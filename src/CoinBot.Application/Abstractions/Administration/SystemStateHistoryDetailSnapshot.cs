using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Administration;

public sealed record SystemStateHistoryDetailSnapshot(
    string HistoryReference,
    long Version,
    GlobalSystemStateKind State,
    string ReasonCode,
    string? Message,
    string Source,
    bool IsManualOverride,
    DateTime? ExpiresAtUtc,
    string? CorrelationId,
    string? CommandId,
    string? ApprovalReference,
    string? IncidentReference,
    string? DependencyCircuitBreakerStateReference,
    string? BreakerKind,
    string? BreakerStateCode,
    string? UpdatedByUserId,
    string? UpdatedFromIp,
    string? PreviousState,
    string? ChangeSummary,
    DateTime CreatedAtUtc);
