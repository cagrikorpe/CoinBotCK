using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Administration;

public sealed record GlobalSystemStateSnapshot(
    GlobalSystemStateKind State,
    string ReasonCode,
    string? Message,
    string Source,
    string? CorrelationId,
    bool IsManualOverride,
    DateTime? ExpiresAtUtc,
    DateTime? UpdatedAtUtc,
    string? UpdatedByUserId,
    string? UpdatedFromIp,
    long Version,
    bool IsPersisted)
{
    public bool IsExecutionBlocked => State != GlobalSystemStateKind.Active;
}
