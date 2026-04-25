namespace CoinBot.Application.Abstractions.Administration;

public interface IUltraDebugLogService
{
    IReadOnlyCollection<UltraDebugLogDurationOption> GetDurationOptions();
    IReadOnlyCollection<UltraDebugLogSizeLimitOption> GetLogSizeLimitOptions();

    Task<UltraDebugLogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<UltraDebugLogSnapshot> EnableAsync(
        UltraDebugLogEnableRequest request,
        CancellationToken cancellationToken = default);

    Task<UltraDebugLogSnapshot> DisableAsync(
        UltraDebugLogDisableRequest request,
        CancellationToken cancellationToken = default);

    Task<UltraDebugLogTailSnapshot> SearchAsync(
        UltraDebugLogSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<UltraDebugLogExportSnapshot> ExportAsync(
        UltraDebugLogExportRequest request,
        CancellationToken cancellationToken = default);

    Task WriteAsync(
        UltraDebugLogEntry entry,
        CancellationToken cancellationToken = default);
}

public sealed record UltraDebugLogDurationOption(
    string Key,
    string Label,
    TimeSpan Duration);

public sealed record UltraDebugLogSizeLimitOption(
    int ValueMb,
    string Label);

public sealed record UltraDebugLogSnapshot(
    bool IsEnabled,
    DateTime? StartedAtUtc,
    DateTime? ExpiresAtUtc,
    string? DurationKey,
    string? EnabledByAdminId,
    string? EnabledByAdminEmail,
    string? AutoDisabledReason,
    DateTime? UpdatedAtUtc,
    bool IsPersisted,
    int? NormalLogsLimitMb = null,
    int? UltraLogsLimitMb = null,
    long NormalLogsUsageBytes = 0,
    long UltraLogsUsageBytes = 0,
    long? DiskFreeSpaceBytes = null,
    bool IsNormalFallbackMode = false,
    UltraDebugLogEventSnapshot? LatestStructuredEvent = null,
    IReadOnlyCollection<UltraDebugLogEventSnapshot>? LatestCategoryEvents = null,
    UltraDebugLogTailSnapshot? NormalLogsTail = null,
    UltraDebugLogTailSnapshot? UltraLogsTail = null);

public sealed record UltraDebugLogEventSnapshot(
    string Category,
    string EventName,
    string Summary,
    DateTime? OccurredAtUtc = null,
    string? CorrelationId = null,
    string? Symbol = null,
    string? Timeframe = null,
    string? SourceLayer = null,
    string? DecisionReasonCode = null,
    string? BlockerCode = null,
    string? LatencyBreakdownLabel = null);

public sealed record UltraDebugLogTailSnapshot(
    string BucketName,
    int RequestedLineCount,
    int ReturnedLineCount,
    int FilesScanned,
    bool IsTruncated,
    IReadOnlyCollection<UltraDebugLogTailLineSnapshot> Lines);

public sealed record UltraDebugLogTailLineSnapshot(
    string Category,
    string EventName,
    string Summary,
    string? DetailPreview = null,
    DateTime? OccurredAtUtc = null,
    string? CorrelationId = null,
    string? Symbol = null,
    string? SourceFileName = null,
    string? Source = null,
    string? BucketLabel = null);

public sealed record UltraDebugLogSearchRequest(
    string BucketName,
    string? Category,
    string? Source,
    string? SearchTerm,
    DateTime? FromUtc,
    int Take);

public sealed record UltraDebugLogExportRequest(
    string BucketName,
    string? Category,
    string? Source,
    string? SearchTerm,
    DateTime? FromUtc,
    DateTime? ToUtc,
    int MaxRows,
    bool ZipPackage);

public sealed record UltraDebugLogExportSnapshot(
    string ContentType,
    string FileDownloadName,
    byte[] Content,
    DateTime FromUtc,
    DateTime ToUtc,
    int RequestedLineCount,
    int ExportedLineCount,
    int FilesScanned,
    bool IsTruncated,
    bool IsEmpty,
    string? EmptyReason = null);

public sealed record UltraDebugLogEnableRequest(
    string DurationKey,
    string ActorUserId,
    string? ActorEmail,
    string? UpdatedFromIp,
    string? UserAgent,
    string? CorrelationId,
    int? NormalLogsLimitMb = null,
    int? UltraLogsLimitMb = null);

public sealed record UltraDebugLogDisableRequest(
    string ActorUserId,
    string? ActorEmail,
    string? UpdatedFromIp,
    string? UserAgent,
    string? CorrelationId,
    string ReasonCode);

public sealed record UltraDebugLogEntry(
    string Category,
    string EventName,
    string Summary,
    string? CorrelationId = null,
    string? Symbol = null,
    string? ExecutionAttemptId = null,
    string? StrategySignalId = null,
    object? Detail = null);
