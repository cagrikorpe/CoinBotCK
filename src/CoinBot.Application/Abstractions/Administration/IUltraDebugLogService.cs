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
    IReadOnlyCollection<UltraDebugLogEventSnapshot>? LatestCategoryEvents = null);

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
