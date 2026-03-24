using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Autonomy;

public sealed record AutonomyReviewQueueItem(
    string ApprovalId,
    string ScopeKey,
    string SuggestedAction,
    decimal ConfidenceScore,
    IReadOnlyCollection<string> AffectedUsers,
    IReadOnlyCollection<string> AffectedSymbols,
    DateTime ExpiresAtUtc,
    string Reason,
    AutonomyReviewStatus Status,
    string? CorrelationId);
