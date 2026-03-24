using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Administration;

public sealed record CrisisEscalationPreview(
    CrisisEscalationLevel Level,
    string Scope,
    int AffectedUserCount,
    int AffectedSymbolCount,
    int OpenPositionCount,
    int PendingOrderCount,
    decimal EstimatedExposure,
    bool RequiresReauth,
    bool RequiresSecondApproval,
    string PreviewStamp);
