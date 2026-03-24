using CoinBot.Domain.Enums;

namespace CoinBot.Web.ViewModels.Admin;

public sealed record AdminCrisisEscalationPreviewViewModel(
    CrisisEscalationLevel Level,
    string Scope,
    string ReasonCode,
    string? Message,
    int AffectedUserCount,
    int AffectedSymbolCount,
    int OpenPositionCount,
    int PendingOrderCount,
    decimal EstimatedExposure,
    bool RequiresReauth,
    bool RequiresSecondApproval,
    string PreviewStamp);
