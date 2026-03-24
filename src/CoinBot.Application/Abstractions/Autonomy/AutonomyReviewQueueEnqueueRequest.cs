namespace CoinBot.Application.Abstractions.Autonomy;

public sealed record AutonomyReviewQueueEnqueueRequest(
    string ApprovalId,
    string ScopeKey,
    string SuggestedAction,
    decimal ConfidenceScore,
    IReadOnlyCollection<string> AffectedUsers,
    IReadOnlyCollection<string> AffectedSymbols,
    DateTime ExpiresAtUtc,
    string Reason,
    string? CorrelationId = null);
