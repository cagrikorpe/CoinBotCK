using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Administration;

public sealed record CrisisEscalationPreviewRequest(
    CrisisEscalationLevel Level,
    string Scope);
