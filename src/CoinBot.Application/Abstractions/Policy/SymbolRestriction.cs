using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Policy;

public sealed record SymbolRestriction(
    string Symbol,
    SymbolRestrictionState State,
    string? Reason = null,
    DateTime? UpdatedAtUtc = null,
    string? UpdatedByUserId = null);
