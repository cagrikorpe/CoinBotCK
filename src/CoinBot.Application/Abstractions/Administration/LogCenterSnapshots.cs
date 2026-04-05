namespace CoinBot.Application.Abstractions.Administration;

public sealed record LogCenterQueryRequest(
    string? Query,
    string? CorrelationId,
    string? DecisionId,
    string? ExecutionAttemptId,
    string? UserId,
    string? Symbol,
    string? Status,
    DateTime? FromUtc,
    DateTime? ToUtc,
    int Take);

public sealed record LogCenterEntrySnapshot(
    string Kind,
    string Reference,
    string Status,
    string Tone,
    string? Severity,
    string? CorrelationId,
    string? DecisionId,
    string? ExecutionAttemptId,
    string? IncidentReference,
    string? ApprovalReference,
    string? UserId,
    string? Symbol,
    string Title,
    string Summary,
    string? Source,
    DateTime CreatedAtUtc,
    IReadOnlyCollection<string> Tags,
    string? RawJson,
    string? DecisionReasonType = null,
    string? DecisionReasonCode = null,
    string? DecisionSummary = null,
    DateTime? DecisionAtUtc = null,
    DateTime? LastCandleAtUtc = null,
    int? DataAgeMs = null,
    int? StaleThresholdMs = null,
    string? StaleReason = null,
    string? ContinuityState = null,
    int? ContinuityGapCount = null,
    DateTime? ContinuityGapStartedAtUtc = null,
    DateTime? ContinuityGapLastSeenAtUtc = null,
    DateTime? ContinuityRecoveredAtUtc = null);

public sealed record LogCenterSummarySnapshot(
    int TotalRows,
    int DecisionTraceRows,
    int ExecutionTraceRows,
    int AdminAuditLogRows,
    int IncidentRows,
    int IncidentEventRows,
    int ApprovalQueueRows,
    int ApprovalActionRows,
    int CriticalRows,
    DateTime? LastUpdatedAtUtc);

public sealed record LogCenterRetentionSnapshot(
    bool Enabled,
    int DecisionTraceRetentionDays,
    int ExecutionTraceRetentionDays,
    int AdminAuditLogRetentionDays,
    int IncidentRetentionDays,
    int ApprovalRetentionDays,
    int BatchSize,
    DateTime? LastRunAtUtc,
    string? LastRunSummary);

public sealed record LogCenterPageSnapshot(
    LogCenterQueryRequest Filters,
    LogCenterSummarySnapshot Summary,
    LogCenterRetentionSnapshot Retention,
    IReadOnlyCollection<LogCenterEntrySnapshot> Entries,
    bool HasError,
    string? ErrorMessage);

public sealed record LogCenterRetentionRunSnapshot(
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    int DecisionTraceCount,
    int ExecutionTraceCount,
    int AdminAuditLogCount,
    int IncidentCount,
    int IncidentEventCount,
    int ApprovalQueueCount,
    int ApprovalActionCount);
