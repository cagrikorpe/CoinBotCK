using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Administration;

public sealed record SystemStateHistoryListItem(
    string HistoryReference,
    long Version,
    GlobalSystemStateKind State,
    string ReasonCode,
    string Source,
    bool IsManualOverride,
    DateTime? ExpiresAtUtc,
    string? CorrelationId,
    string? ApprovalReference,
    string? IncidentReference,
    DateTime CreatedAtUtc);
