using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Administration;

public sealed record AdminShellHealthSnapshot(
    string EnvironmentBadge,
    GlobalSystemStateKind SystemState,
    string ReasonCode,
    string? Message,
    DateTime? LastUpdatedAtUtc,
    bool IsManualOverride);
