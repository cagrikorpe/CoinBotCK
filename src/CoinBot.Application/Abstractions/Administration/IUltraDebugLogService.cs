namespace CoinBot.Application.Abstractions.Administration;

public interface IUltraDebugLogService
{
    IReadOnlyCollection<UltraDebugLogDurationOption> GetDurationOptions();
    IReadOnlyCollection<UltraDebugLogSizeLimitOption> GetLogSizeLimitOptions();

    Task<UltraDebugLogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    async Task<UltraDebugLogHealthSnapshot> GetHealthSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        var affectedBuckets = !string.IsNullOrWhiteSpace(snapshot.AutoDisabledReason)
            ? new[] { "ultra_debug" }
            : Array.Empty<string>();
        var normalizedState = snapshot.IsEnabled
            ? "Healthy"
            : string.Equals(snapshot.AutoDisabledReason, "manual_disable", StringComparison.Ordinal)
                ? "Disabled"
                : string.IsNullOrWhiteSpace(snapshot.AutoDisabledReason)
                    ? "Healthy"
                    : "Degraded";

        return new UltraDebugLogHealthSnapshot(
            DiskPressureState: normalizedState,
            FreeBytes: snapshot.DiskFreeSpaceBytes,
            FreePercent: null,
            ThresholdBytes: null,
            AffectedLogBuckets: affectedBuckets,
            LastCheckedAtUtc: snapshot.UpdatedAtUtc,
            LastEscalationReason: snapshot.AutoDisabledReason,
            IsWritable: true,
            IsTailAvailable: true,
            IsExportAvailable: true,
            LastRetentionCompletedAtUtc: null,
            LastRetentionReasonCode: null,
            LastRetentionSucceeded: null,
            IsNormalFallbackMode: snapshot.IsNormalFallbackMode,
            AutoDisabledReason: snapshot.AutoDisabledReason,
            IsEnabled: snapshot.IsEnabled);
    }

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

    Task<UltraDebugLogRetentionRunSnapshot> ApplyRetentionAsync(
        UltraDebugLogRetentionRunRequest request,
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

public sealed record UltraDebugLogHealthSnapshot(
    string DiskPressureState,
    long? FreeBytes,
    decimal? FreePercent,
    long? ThresholdBytes,
    IReadOnlyCollection<string> AffectedLogBuckets,
    DateTime? LastCheckedAtUtc,
    string? LastEscalationReason,
    bool IsWritable,
    bool IsTailAvailable,
    bool IsExportAvailable,
    DateTime? LastRetentionCompletedAtUtc,
    string? LastRetentionReasonCode,
    bool? LastRetentionSucceeded,
    bool IsNormalFallbackMode,
    string? AutoDisabledReason,
    bool IsEnabled)
{
    public static UltraDebugLogHealthSnapshot Empty()
    {
        return new UltraDebugLogHealthSnapshot(
            DiskPressureState: "Degraded",
            FreeBytes: null,
            FreePercent: null,
            ThresholdBytes: null,
            AffectedLogBuckets: Array.Empty<string>(),
            LastCheckedAtUtc: null,
            LastEscalationReason: "Unavailable",
            IsWritable: false,
            IsTailAvailable: false,
            IsExportAvailable: false,
            LastRetentionCompletedAtUtc: null,
            LastRetentionReasonCode: null,
            LastRetentionSucceeded: null,
            IsNormalFallbackMode: false,
            AutoDisabledReason: null,
            IsEnabled: false);
    }
}

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

public sealed record UltraDebugLogRetentionRunRequest(
    string? BucketName = null,
    bool DryRun = false,
    int? MaxFiles = null);

public sealed record UltraDebugLogRetentionRunSnapshot(
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    bool DryRun,
    int ScannedFiles,
    int DeletedFiles,
    int SkippedFiles,
    long ReclaimedBytes,
    int CandidateDeleteFiles,
    long CandidateReclaimedBytes,
    string ReasonCode,
    IReadOnlyCollection<UltraDebugLogRetentionBucketSnapshot> Buckets);

public sealed record UltraDebugLogRetentionBucketSnapshot(
    string BucketName,
    int RetentionDays,
    int ScannedFiles,
    int DeletedFiles,
    int SkippedFiles,
    long ReclaimedBytes,
    int CandidateDeleteFiles,
    long CandidateReclaimedBytes,
    bool IsTruncated,
    string ReasonCode);

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
