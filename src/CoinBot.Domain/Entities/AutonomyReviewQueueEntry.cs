using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class AutonomyReviewQueueEntry : BaseEntity
{
    public string ApprovalId { get; set; } = string.Empty;

    public string ScopeKey { get; set; } = string.Empty;

    public string SuggestedAction { get; set; } = string.Empty;

    public decimal ConfidenceScore { get; set; }

    public string AffectedUsersCsv { get; set; } = string.Empty;

    public string AffectedSymbolsCsv { get; set; } = string.Empty;

    public DateTime ExpiresAtUtc { get; set; }

    public string Reason { get; set; } = string.Empty;

    public AutonomyReviewStatus Status { get; set; } = AutonomyReviewStatus.Pending;

    public string? CorrelationId { get; set; }
}
