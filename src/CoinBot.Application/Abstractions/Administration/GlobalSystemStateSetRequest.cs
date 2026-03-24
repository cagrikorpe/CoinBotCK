using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Administration;

public sealed record GlobalSystemStateSetRequest(
    GlobalSystemStateKind State,
    string ReasonCode,
    string? Message,
    string Source,
    string? CorrelationId,
    bool IsManualOverride,
    DateTime? ExpiresAtUtc,
    string UpdatedByUserId,
    string? UpdatedFromIp,
    string? CommandId = null,
    string? ApprovalReference = null,
    string? IncidentReference = null,
    string? DependencyCircuitBreakerStateReference = null,
    string? BreakerKind = null,
    string? BreakerStateCode = null,
    string? ChangeSummary = null);
