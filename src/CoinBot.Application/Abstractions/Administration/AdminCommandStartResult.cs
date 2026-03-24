using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Administration;

public sealed record AdminCommandStartResult(
    AdminCommandStartDisposition Disposition,
    AdminCommandStatus? PersistedStatus,
    string? ResultSummary);
