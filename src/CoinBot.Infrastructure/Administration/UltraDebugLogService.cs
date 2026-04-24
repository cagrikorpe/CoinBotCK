using System.Linq;
using System.Text;
using System.Text.Json;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Administration;

public sealed class UltraDebugLogService(
    IServiceScopeFactory serviceScopeFactory,
    TimeProvider timeProvider,
    ILogger<UltraDebugLogService> logger,
    IHostEnvironment? hostEnvironment = null,
    Func<string, long?>? availableDiskBytesResolver = null) : IUltraDebugLogService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim fileWriteLock = new(1, 1);
    private readonly string applicationName = ResolveApplicationName(hostEnvironment?.ApplicationName);
    private readonly Func<string, long?> diskFreeSpaceResolver = availableDiskBytesResolver ?? ResolveAvailableDiskBytes;

    public IReadOnlyCollection<UltraDebugLogDurationOption> GetDurationOptions() => UltraDebugLogDefaults.GetDurationOptions();
    public IReadOnlyCollection<UltraDebugLogSizeLimitOption> GetLogSizeLimitOptions() => UltraDebugLogDefaults.GetLogSizeLimitOptions();

    public async Task<UltraDebugLogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var entity = await dbContext.Set<Domain.Entities.UltraDebugLogState>()
                .SingleOrDefaultAsync(item => item.Id == UltraDebugLogDefaults.SingletonId, cancellationToken);

            if (entity is null)
            {
                return await EnrichSnapshotWithUsageAsync(UltraDebugLogDefaults.CreateSnapshot(), cancellationToken);
            }

            if (entity.IsEnabled &&
                entity.ExpiresAtUtc.HasValue &&
                entity.ExpiresAtUtc.Value <= timeProvider.GetUtcNow().UtcDateTime)
            {
                var expiredSnapshot = await DisableInternalAsync(
                    scope.ServiceProvider,
                    dbContext,
                    entity,
                    UltraDebugLogDefaults.DurationExpiredReason,
                    actorUserId: "system:ultra-log-expiry",
                    actorEmail: null,
                    updatedFromIp: null,
                    userAgent: null,
                    correlationId: null,
                    auditActionType: "ultra_log_auto_disabled_duration_expired",
                    auditReason: "Ultra log duration expired and the mode was disabled automatically.",
                    cancellationToken);
                return await EnrichSnapshotWithUsageAsync(expiredSnapshot, cancellationToken);
            }

            return await EnrichSnapshotWithUsageAsync(MapSnapshot(entity, isPersisted: true), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Ultra debug log state could not be loaded. The mode will stay fail-closed.");
            return await EnrichSnapshotWithUsageAsync(UltraDebugLogDefaults.CreateSnapshot() with
            {
                AutoDisabledReason = UltraDebugLogDefaults.RuntimeErrorReason
            }, cancellationToken);
        }
    }

    public async Task<UltraDebugLogSnapshot> EnableAsync(
        UltraDebugLogEnableRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!UltraDebugLogDefaults.TryResolveDuration(request.DurationKey, out var duration))
        {
            throw new UltraDebugLogOperationException(
                "UltraLogDurationInvalid",
                "Ultra log activation failed because the selected duration is invalid.");
        }

        if (!UltraDebugLogDefaults.TryResolveLogSizeLimit(request.NormalLogsLimitMb, out var normalLogsLimit))
        {
            throw new UltraDebugLogOperationException(
                "UltraLogNormalLimitInvalid",
                "Ultra log activation failed because the normal log size limit is invalid.");
        }

        if (!UltraDebugLogDefaults.TryResolveLogSizeLimit(request.UltraLogsLimitMb, out var ultraLogsLimit))
        {
            throw new UltraDebugLogOperationException(
                "UltraLogUltraLimitInvalid",
                "Ultra log activation failed because the ultra debug log size limit is invalid.");
        }

        ValidateRequestActor(request.ActorUserId);
        EnsureMaskingPipelineSafe();
        EnsureUltraBucketWritable();

        try
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var entity = await GetOrCreateTrackedEntityAsync(dbContext, cancellationToken);
            var oldSummary = BuildStateSummary(entity);
            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
            entity.IsEnabled = true;
            entity.StartedAtUtc = nowUtc;
            entity.ExpiresAtUtc = nowUtc.Add(duration.Duration);
            entity.DurationKey = duration.Key;
            entity.EnabledByAdminId = NormalizeOptional(request.ActorUserId, 450);
            entity.EnabledByAdminEmail = NormalizeOptional(request.ActorEmail, 256);
            entity.AutoDisabledReason = null;
            entity.UpdatedAtUtc = nowUtc;
            entity.NormalLogsLimitMb = normalLogsLimit.ValueMb;
            entity.UltraLogsLimitMb = ultraLogsLimit.ValueMb;

            await dbContext.SaveChangesAsync(cancellationToken);

            var snapshot = MapSnapshot(entity, isPersisted: true);
            await WriteAdminAuditAsync(
                scope.ServiceProvider,
                request.ActorUserId,
                "ultra_log_enabled",
                oldSummary,
                BuildStateSummary(entity),
                "Ultra log mode was enabled for a limited duration.",
                request.UpdatedFromIp,
                request.UserAgent,
                request.CorrelationId,
                cancellationToken);

            return await EnrichSnapshotWithUsageAsync(snapshot, cancellationToken);
        }
        catch (UltraDebugLogOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await TryWriteRuntimeEnableFailureAuditAsync(request, cancellationToken);
            throw new UltraDebugLogOperationException(
                "UltraLogEnableRuntimeFailed",
                $"Ultra log activation failed at runtime: {SensitivePayloadMasker.Mask(exception.Message, 256) ?? "runtime failure"}");
        }
    }

    public async Task<UltraDebugLogSnapshot> DisableAsync(
        UltraDebugLogDisableRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequestActor(request.ActorUserId);

        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var entity = await GetOrCreateTrackedEntityAsync(dbContext, cancellationToken);

        var snapshot = await DisableInternalAsync(
            scope.ServiceProvider,
            dbContext,
            entity,
            NormalizeOptional(request.ReasonCode, 64) ?? UltraDebugLogDefaults.ManualDisableReason,
            request.ActorUserId,
            request.ActorEmail,
            request.UpdatedFromIp,
            request.UserAgent,
            request.CorrelationId,
            "ultra_log_disabled_manually",
            "Ultra log mode was disabled manually by an administrator.",
            cancellationToken);
        return await EnrichSnapshotWithUsageAsync(snapshot, cancellationToken);
    }

    public Task<UltraDebugLogTailSnapshot> SearchAsync(
        UltraDebugLogSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedBucketName = NormalizeSearchBucketName(request.BucketName);
        var normalizedTake = Math.Clamp(
            request.Take <= 0 ? UltraDebugLogDefaults.SearchPreviewDefaultTake : request.Take,
            1,
            UltraDebugLogDefaults.SearchPreviewMaxTake);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedCategory = NormalizeSearchCategory(request.Category);
            var normalizedSource = NormalizeOptional(request.Source, 128);
            var normalizedSearchTerm = NormalizeOptional(request.SearchTerm, 128);
            var candidateFiles = ResolveSearchCandidateFiles(normalizedBucketName, normalizedCategory, request.FromUtc);
            var collectedLines = new List<UltraDebugLogTailLineSnapshot>(normalizedTake);
            var truncated = candidateFiles.TotalFileCount > candidateFiles.Files.Count;

            foreach (var candidateFile in candidateFiles.Files)
            {
                if (collectedLines.Count >= normalizedTake)
                {
                    truncated = true;
                    break;
                }

                var tailLines = ReadTailWindowLines(
                    candidateFile.File.FullName,
                    UltraDebugLogDefaults.SearchPreviewMaxTake,
                    UltraDebugLogDefaults.SearchPreviewWindowBytes,
                    out var fileWasTruncated);
                truncated |= fileWasTruncated;

                foreach (var tailLine in tailLines)
                {
                    var parsedLine = ParseTailLineSnapshot(tailLine, candidateFile.File.Name, candidateFile.BucketName);
                    if (parsedLine is null ||
                        !MatchesSearchRequest(parsedLine, normalizedCategory, normalizedSource, normalizedSearchTerm, request.FromUtc))
                    {
                        continue;
                    }

                    collectedLines.Add(parsedLine);
                    if (collectedLines.Count >= normalizedTake)
                    {
                        truncated = true;
                        break;
                    }
                }
            }

            var orderedLines = collectedLines
                .OrderByDescending(item => item.OccurredAtUtc ?? DateTime.MinValue)
                .ThenByDescending(item => item.SourceFileName, StringComparer.Ordinal)
                .Take(normalizedTake)
                .ToArray();

            return Task.FromResult(new UltraDebugLogTailSnapshot(
                normalizedBucketName,
                normalizedTake,
                orderedLines.Length,
                candidateFiles.Files.Count,
                truncated,
                orderedLines));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Ultra debug log search preview failed. The admin search result will stay empty.");
            return Task.FromResult(new UltraDebugLogTailSnapshot(
                normalizedBucketName,
                normalizedTake,
                0,
                0,
                false,
                Array.Empty<UltraDebugLogTailLineSnapshot>()));
        }
    }

    public async Task WriteAsync(UltraDebugLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var normalizedCategory = NormalizeRequired(entry.Category, 128, nameof(entry.Category));
        var snapshot = await GetSnapshotAsync(cancellationToken);
        var diskFreeSpaceBytes = GetAvailableDiskSpaceBytes();
        var diskPressureDetected = IsDiskPressure(diskFreeSpaceBytes);

        if (diskPressureDetected && snapshot.IsEnabled)
        {
            snapshot = await DisableForDiskPressureAsync(diskFreeSpaceBytes, cancellationToken);
        }

        var normalFallbackReasonCode = diskPressureDetected
            ? UltraDebugLogDefaults.DiskPressureReason
            : snapshot.AutoDisabledReason;
        var useCuratedNormalFallback = diskPressureDetected || IsCuratedNormalFallbackReason(snapshot.AutoDisabledReason);
        var normalDetail = useCuratedNormalFallback
            ? BuildCuratedFallbackDetail(entry, normalFallbackReasonCode, diskFreeSpaceBytes)
            : entry.Detail;
        var normalRecord = new UltraDebugLogRecord(
            utcNow,
            applicationName,
            Environment.MachineName,
            normalizedCategory,
            NormalizeRequired(entry.EventName, 128, nameof(entry.EventName)),
            NormalizeRequired(entry.Summary, 512, nameof(entry.Summary)),
            NormalizeOptional(entry.CorrelationId, 128),
            NormalizeOptional(entry.Symbol, 32),
            NormalizeOptional(entry.ExecutionAttemptId, 64),
            NormalizeOptional(entry.StrategySignalId, 64),
            SerializeMaskedDetail(normalDetail));
        var normalSerializedLine = JsonSerializer.Serialize(normalRecord, SerializerOptions);

        try
        {
            var normalBucketFilePath = ResolveBucketFilePath("normal", utcNow, normalizedCategory);
            await AppendLineAndRotateIfNeededAsync(
                normalBucketFilePath,
                normalSerializedLine,
                UltraDebugLogDefaults.GetRotateThresholdMb("normal"),
                cancellationToken);
            await EnforceNormalBucketLimitAsync(normalBucketFilePath, snapshot.NormalLogsLimitMb, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Ultra debug normal bucket write failed. The runtime flow will continue.");
            if (snapshot.IsEnabled && IsLogPathFailure(exception))
            {
                snapshot = await DisableForRuntimeWriteFailureAsync(exception, cancellationToken);
            }
            return;
        }

        if (!snapshot.IsEnabled)
        {
            return;
        }

        var ultraRecord = normalRecord with
        {
            DetailMasked = SerializeMaskedDetail(entry.Detail)
        };
        var ultraSerializedLine = JsonSerializer.Serialize(ultraRecord, SerializerOptions);
        try
        {
            var ultraBucketFilePath = ResolveBucketFilePath("ultra_debug", utcNow, normalizedCategory);
            await AppendLineAndRotateIfNeededAsync(
                ultraBucketFilePath,
                ultraSerializedLine,
                UltraDebugLogDefaults.GetRotateThresholdMb("ultra_debug"),
                cancellationToken);
            var ultraBucketWithinLimit = await EnforceUltraBucketLimitAsync(
                ultraBucketFilePath,
                snapshot.UltraLogsLimitMb,
                cancellationToken);

            if (!ultraBucketWithinLimit)
            {
                await DisableForSizeLimitExceededAsync(
                    ultraBucketFilePath,
                    snapshot.UltraLogsLimitMb,
                    cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Ultra debug bucket write failed. The mode will be disabled fail-closed.");
            if (IsLogPathFailure(exception))
            {
                await DisableForRuntimeWriteFailureAsync(exception, cancellationToken);
            }
            else
            {
                await DisableForRuntimeFailureAsync(exception, cancellationToken);
            }
        }
    }

    private async Task DisableForRuntimeFailureAsync(Exception exception, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var entity = await dbContext.Set<Domain.Entities.UltraDebugLogState>()
                .SingleOrDefaultAsync(item => item.Id == UltraDebugLogDefaults.SingletonId, cancellationToken);

            if (entity is null || !entity.IsEnabled)
            {
                return;
            }

            await DisableInternalAsync(
                scope.ServiceProvider,
                dbContext,
                entity,
                UltraDebugLogDefaults.RuntimeErrorReason,
                actorUserId: "system:ultra-log-runtime",
                actorEmail: null,
                updatedFromIp: null,
                userAgent: null,
                correlationId: null,
                auditActionType: "ultra_log_auto_disabled_runtime_error",
                auditReason: $"Ultra log mode was disabled because file writing failed: {SensitivePayloadMasker.Mask(exception.Message, 256) ?? "runtime failure"}.",
                cancellationToken);
        }
        catch (Exception disableException) when (disableException is not OperationCanceledException)
        {
            logger.LogWarning(disableException, "Ultra debug log runtime disable could not be persisted.");
        }
    }

    private async Task<UltraDebugLogSnapshot> DisableForDiskPressureAsync(long? availableBytes, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var entity = await dbContext.Set<Domain.Entities.UltraDebugLogState>()
                .SingleOrDefaultAsync(item => item.Id == UltraDebugLogDefaults.SingletonId, cancellationToken);

            if (entity is null || !entity.IsEnabled)
            {
                return await EnrichSnapshotWithUsageAsync(UltraDebugLogDefaults.CreateSnapshot() with
                {
                    AutoDisabledReason = UltraDebugLogDefaults.DiskPressureReason
                }, cancellationToken);
            }

            var disabledSnapshot = await DisableInternalAsync(
                scope.ServiceProvider,
                dbContext,
                entity,
                UltraDebugLogDefaults.DiskPressureReason,
                actorUserId: "system:ultra-log-disk-pressure",
                actorEmail: null,
                updatedFromIp: null,
                userAgent: null,
                correlationId: null,
                auditActionType: "ultra_log_auto_disabled_disk_pressure",
                auditReason: $"Ultra log mode was disabled because available disk space dropped below the guard threshold. FreeBytes={availableBytes?.ToString() ?? "unknown"}; ThresholdMb={UltraDebugLogDefaults.DiskPressureFreeSpaceThresholdMb}.",
                cancellationToken);
            return await EnrichSnapshotWithUsageAsync(disabledSnapshot, cancellationToken);
        }
        catch (Exception disableException) when (disableException is not OperationCanceledException)
        {
            logger.LogWarning(disableException, "Ultra debug log disk-pressure disable could not be persisted.");
            return await EnrichSnapshotWithUsageAsync(UltraDebugLogDefaults.CreateSnapshot() with
            {
                AutoDisabledReason = UltraDebugLogDefaults.DiskPressureReason
            }, cancellationToken);
        }
    }

    private async Task<UltraDebugLogSnapshot> DisableForRuntimeWriteFailureAsync(Exception exception, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var entity = await dbContext.Set<Domain.Entities.UltraDebugLogState>()
                .SingleOrDefaultAsync(item => item.Id == UltraDebugLogDefaults.SingletonId, cancellationToken);

            if (entity is null || !entity.IsEnabled)
            {
                return await EnrichSnapshotWithUsageAsync(UltraDebugLogDefaults.CreateSnapshot() with
                {
                    AutoDisabledReason = UltraDebugLogDefaults.RuntimeWriteFailureReason
                }, cancellationToken);
            }

            var disabledSnapshot = await DisableInternalAsync(
                scope.ServiceProvider,
                dbContext,
                entity,
                UltraDebugLogDefaults.RuntimeWriteFailureReason,
                actorUserId: "system:ultra-log-runtime-write",
                actorEmail: null,
                updatedFromIp: null,
                userAgent: null,
                correlationId: null,
                auditActionType: "ultra_log_auto_disabled_runtime_write_failure",
                auditReason: $"Ultra log mode was disabled because the log write path failed: {SensitivePayloadMasker.Mask(exception.Message, 256) ?? "runtime failure"}.",
                cancellationToken);
            return await EnrichSnapshotWithUsageAsync(disabledSnapshot, cancellationToken);
        }
        catch (Exception disableException) when (disableException is not OperationCanceledException)
        {
            logger.LogWarning(disableException, "Ultra debug log runtime-write disable could not be persisted.");
            return await EnrichSnapshotWithUsageAsync(UltraDebugLogDefaults.CreateSnapshot() with
            {
                AutoDisabledReason = UltraDebugLogDefaults.RuntimeWriteFailureReason
            }, cancellationToken);
        }
    }

    private async Task DisableForSizeLimitExceededAsync(
        string activeFilePath,
        int? configuredLimitMb,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var entity = await dbContext.Set<Domain.Entities.UltraDebugLogState>()
                .SingleOrDefaultAsync(item => item.Id == UltraDebugLogDefaults.SingletonId, cancellationToken);

            if (entity is null || !entity.IsEnabled)
            {
                return;
            }

            var observedBytes = await GetBucketSizeBytesAsync(activeFilePath, cancellationToken);
            await DisableInternalAsync(
                scope.ServiceProvider,
                dbContext,
                entity,
                UltraDebugLogDefaults.SizeLimitExceededReason,
                actorUserId: "system:ultra-log-size-limit",
                actorEmail: null,
                updatedFromIp: null,
                userAgent: null,
                correlationId: null,
                auditActionType: "ultra_log_auto_disabled_size_limit",
                auditReason: $"Ultra log mode was disabled because the ultra_debug bucket remained above limit after cleanup. LimitMb={configuredLimitMb?.ToString() ?? "none"}; SizeBytes={observedBytes}.",
                cancellationToken);
        }
        catch (Exception disableException) when (disableException is not OperationCanceledException)
        {
            logger.LogWarning(disableException, "Ultra debug log size-limit disable could not be persisted.");
        }
    }

    private async Task<UltraDebugLogSnapshot> DisableInternalAsync(
        IServiceProvider serviceProvider,
        ApplicationDbContext dbContext,
        Domain.Entities.UltraDebugLogState entity,
        string reasonCode,
        string actorUserId,
        string? actorEmail,
        string? updatedFromIp,
        string? userAgent,
        string? correlationId,
        string auditActionType,
        string auditReason,
        CancellationToken cancellationToken)
    {
        var oldSummary = BuildStateSummary(entity);
        entity.IsEnabled = false;
        entity.AutoDisabledReason = NormalizeOptional(reasonCode, 64);
        entity.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        entity.EnabledByAdminId = entity.EnabledByAdminId ?? NormalizeOptional(actorUserId, 450);
        entity.EnabledByAdminEmail = entity.EnabledByAdminEmail ?? NormalizeOptional(actorEmail, 256);

        await dbContext.SaveChangesAsync(cancellationToken);

        await WriteAdminAuditAsync(
            serviceProvider,
            actorUserId,
            auditActionType,
            oldSummary,
            BuildStateSummary(entity),
            auditReason,
            updatedFromIp,
            userAgent,
            correlationId,
            cancellationToken);

        return MapSnapshot(entity, isPersisted: true);
    }

    private async Task<Domain.Entities.UltraDebugLogState> GetOrCreateTrackedEntityAsync(
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.Set<Domain.Entities.UltraDebugLogState>()
            .SingleOrDefaultAsync(item => item.Id == UltraDebugLogDefaults.SingletonId, cancellationToken);

        if (entity is not null)
        {
            return entity;
        }

        entity = UltraDebugLogDefaults.CreateEntity();
        dbContext.Set<Domain.Entities.UltraDebugLogState>().Add(entity);
        return entity;
    }

    private async Task WriteAdminAuditAsync(
        IServiceProvider serviceProvider,
        string actorUserId,
        string actionType,
        string? oldValueSummary,
        string? newValueSummary,
        string reason,
        string? updatedFromIp,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var auditService = serviceProvider.GetRequiredService<IAdminAuditLogService>();
        await auditService.WriteAsync(
            new AdminAuditLogWriteRequest(
                NormalizeRequired(actorUserId, 450, nameof(actorUserId)),
                NormalizeRequired(actionType, 128, nameof(actionType)),
                "UltraDebugLog",
                "singleton",
                Truncate(oldValueSummary, 2048),
                Truncate(newValueSummary, 2048),
                Truncate(reason, 512) ?? "Ultra debug log event.",
                NormalizeOptional(updatedFromIp, 128),
                NormalizeOptional(userAgent, 256),
                NormalizeOptional(correlationId, 128)),
            cancellationToken);
    }

    private async Task TryWriteRuntimeEnableFailureAuditAsync(
        UltraDebugLogEnableRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            await WriteAdminAuditAsync(
                scope.ServiceProvider,
                request.ActorUserId,
                "ultra_log_enable_failed_runtime",
                oldValueSummary: null,
                newValueSummary: $"DurationKey={request.DurationKey} | NormalLogsLimitMb={request.NormalLogsLimitMb?.ToString() ?? "none"} | UltraLogsLimitMb={request.UltraLogsLimitMb?.ToString() ?? "none"}",
                reason: "Ultra log activation failed before the mode could be enabled.",
                request.UpdatedFromIp,
                request.UserAgent,
                request.CorrelationId,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Ultra debug runtime enable failure audit could not be written.");
        }
    }

    private string ResolveBucketFilePath(string bucketName, DateTime utcNow, string category)
    {
        var directoryPath = ResolveBucketDirectoryPath(bucketName, ResolveCategoryFolder(category));
        Directory.CreateDirectory(directoryPath);
        return Path.Combine(directoryPath, $"{applicationName}-{utcNow:yyyyMMdd}.ndjson");
    }

    private string ResolveBucketDirectoryPath(string bucketName, string? categoryFolder = null)
    {
        var bucketRootPath = Path.Combine(ResolveLogRootPath(), bucketName);
        return string.IsNullOrWhiteSpace(categoryFolder)
            ? bucketRootPath
            : Path.Combine(bucketRootPath, categoryFolder);
    }

    private string ResolveLogRootPath()
    {
        var basePath = string.IsNullOrWhiteSpace(hostEnvironment?.ContentRootPath)
            ? AppContext.BaseDirectory
            : hostEnvironment.ContentRootPath;
        return Path.Combine(basePath, "Logs");
    }

    private static long? ResolveAvailableDiskBytes(string logRootPath)
    {
        var rootPath = Path.GetPathRoot(logRootPath);
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        return new DriveInfo(rootPath).AvailableFreeSpace;
    }

    private async Task AppendLineAndRotateIfNeededAsync(
        string filePath,
        string serializedLine,
        int rotateThresholdMb,
        CancellationToken cancellationToken)
    {
        await fileWriteLock.WaitAsync(cancellationToken);
        try
        {
            var directoryPath = Path.GetDirectoryName(filePath)
                ?? throw new InvalidOperationException("Ultra debug log bucket path is invalid.");
            Directory.CreateDirectory(directoryPath);

            await using (var stream = new FileStream(
                             filePath,
                             FileMode.Append,
                             FileAccess.Write,
                             FileShare.ReadWrite,
                             bufferSize: 4096,
                             useAsync: true))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteLineAsync(serializedLine.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }

            await RotateFileIfNeededAsync(filePath, rotateThresholdMb, cancellationToken);
        }
        finally
        {
            fileWriteLock.Release();
        }
    }

    private async Task EnforceNormalBucketLimitAsync(
        string activeFilePath,
        int? configuredLimitMb,
        CancellationToken cancellationToken)
    {
        var bucketWithinLimit = await EnforceBucketLimitAsync(activeFilePath, configuredLimitMb, cancellationToken);
        if (!bucketWithinLimit)
        {
            logger.LogWarning(
                "Ultra debug normal bucket exceeded the configured size limit after cleanup. ActiveFile={ActiveFile} LimitMb={LimitMb}.",
                Path.GetFileName(activeFilePath),
                configuredLimitMb);
        }
    }

    private Task<bool> EnforceUltraBucketLimitAsync(
        string activeFilePath,
        int? configuredLimitMb,
        CancellationToken cancellationToken)
    {
        return EnforceBucketLimitAsync(activeFilePath, configuredLimitMb, cancellationToken);
    }

    private async Task<bool> EnforceBucketLimitAsync(
        string activeFilePath,
        int? configuredLimitMb,
        CancellationToken cancellationToken)
    {
        if (!configuredLimitMb.HasValue || configuredLimitMb.Value <= 0)
        {
            return true;
        }

        var limitBytes = checked((long)configuredLimitMb.Value * 1024L * 1024L);
        await fileWriteLock.WaitAsync(cancellationToken);
        try
        {
            var bucketRootPath = ResolveBucketRootDirectoryPath(activeFilePath);
            if (string.IsNullOrWhiteSpace(bucketRootPath) || !Directory.Exists(bucketRootPath))
            {
                return true;
            }

            var directory = new DirectoryInfo(bucketRootPath);
            var files = directory.GetFiles("*.ndjson", SearchOption.AllDirectories)
                .OrderBy(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.Name, StringComparer.Ordinal)
                .ToArray();

            var activeFullPath = Path.GetFullPath(activeFilePath);
            long totalBytes = files.Sum(file => file.Length);

            foreach (var file in files)
            {
                if (totalBytes <= limitBytes)
                {
                    break;
                }

                if (string.Equals(file.FullName, activeFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                totalBytes -= file.Length;
                file.Delete();
            }

            return totalBytes <= limitBytes;
        }
        finally
        {
            fileWriteLock.Release();
        }
    }

    private Task<long> GetBucketSizeBytesAsync(string activeFilePath, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var bucketRootPath = ResolveBucketRootDirectoryPath(activeFilePath);
        if (string.IsNullOrWhiteSpace(bucketRootPath) || !Directory.Exists(bucketRootPath))
        {
            return Task.FromResult(0L);
        }

        var totalBytes = new DirectoryInfo(bucketRootPath)
            .GetFiles("*.ndjson", SearchOption.AllDirectories)
            .Sum(file => file.Length);
        return Task.FromResult(totalBytes);
    }

    private Task<UltraDebugLogSnapshot> EnrichSnapshotWithUsageAsync(
        UltraDebugLogSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var normalUsageBytes = GetBucketUsageBytes("normal");
        var ultraUsageBytes = GetBucketUsageBytes("ultra_debug");
        var diskFreeSpaceBytes = GetAvailableDiskSpaceBytes();
        var latestStructuredEvent = ReadLatestStructuredEventSnapshot("ultra_debug");
        var latestCategoryEvents = ReadLatestCategoryEventSnapshots("ultra_debug");
        var normalLogsTail = ReadBucketTailSnapshot("normal");
        var ultraLogsTail = ReadBucketTailSnapshot("ultra_debug");
        return Task.FromResult(snapshot with
        {
            NormalLogsUsageBytes = normalUsageBytes,
            UltraLogsUsageBytes = ultraUsageBytes,
            DiskFreeSpaceBytes = diskFreeSpaceBytes,
            IsNormalFallbackMode = IsDiskPressure(diskFreeSpaceBytes) || IsCuratedNormalFallbackReason(snapshot.AutoDisabledReason),
            LatestStructuredEvent = latestStructuredEvent,
            LatestCategoryEvents = latestCategoryEvents,
            NormalLogsTail = normalLogsTail,
            UltraLogsTail = ultraLogsTail
        });
    }

    private long GetBucketUsageBytes(string bucketName)
    {
        var directoryPath = ResolveBucketDirectoryPath(bucketName);
        if (!Directory.Exists(directoryPath))
        {
            return 0L;
        }

        return new DirectoryInfo(directoryPath)
            .GetFiles("*.ndjson", SearchOption.AllDirectories)
            .Sum(file => file.Length);
    }

    private long? GetAvailableDiskSpaceBytes()
    {
        try
        {
            return diskFreeSpaceResolver(ResolveLogRootPath());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Ultra debug log disk free space resolution failed.");
            return null;
        }
    }

    private static bool IsDiskPressure(long? availableBytes)
    {
        if (!availableBytes.HasValue)
        {
            return false;
        }

        var thresholdBytes = checked((long)UltraDebugLogDefaults.DiskPressureFreeSpaceThresholdMb * 1024L * 1024L);
        return availableBytes.Value <= thresholdBytes;
    }

    private static bool IsCuratedNormalFallbackReason(string? reasonCode)
    {
        return string.Equals(reasonCode, UltraDebugLogDefaults.DiskPressureReason, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reasonCode, UltraDebugLogDefaults.RuntimeWriteFailureReason, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLogPathFailure(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException;
    }

    private static object BuildCuratedFallbackDetail(
        UltraDebugLogEntry entry,
        string? fallbackReasonCode,
        long? diskFreeSpaceBytes)
    {
        return new
        {
            category = ResolveCategoryFolder(entry.Category),
            eventName = entry.EventName,
            summary = entry.Summary,
            symbol = entry.Symbol,
            correlationId = entry.CorrelationId,
            executionAttemptId = entry.ExecutionAttemptId,
            strategySignalId = entry.StrategySignalId,
            sourceLayer = nameof(UltraDebugLogService),
            fallbackMode = true,
            fallbackReasonCode = fallbackReasonCode,
            diskFreeSpaceBytes
        };
    }

    private UltraDebugLogEventSnapshot? ReadLatestStructuredEventSnapshot(string bucketName)
    {
        return ReadLatestCategoryEventSnapshots(bucketName)
            .OrderByDescending(item => item.OccurredAtUtc ?? DateTime.MinValue)
            .ThenByDescending(item => item.Category, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private UltraDebugLogTailSnapshot? ReadBucketTailSnapshot(string bucketName)
    {
        var bucketDirectoryPath = ResolveBucketDirectoryPath(bucketName);
        if (!Directory.Exists(bucketDirectoryPath))
        {
            return null;
        }

        var candidateFiles = new DirectoryInfo(bucketDirectoryPath)
            .GetFiles("*.ndjson", SearchOption.AllDirectories)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Take(UltraDebugLogDefaults.TailPreviewMaxFiles)
            .ToArray();

        if (candidateFiles.Length == 0)
        {
            return null;
        }

        var collectedLines = new List<UltraDebugLogTailLineSnapshot>(UltraDebugLogDefaults.TailPreviewLineCount);
        var truncated = false;

        foreach (var candidateFile in candidateFiles)
        {
            if (collectedLines.Count >= UltraDebugLogDefaults.TailPreviewLineCount)
            {
                break;
            }

            var remainingLineCount = UltraDebugLogDefaults.TailPreviewLineCount - collectedLines.Count;
            var tailLines = ReadTailWindowLines(
                candidateFile.FullName,
                remainingLineCount,
                UltraDebugLogDefaults.TailPreviewWindowBytes,
                out var fileWasTruncated);
            truncated |= fileWasTruncated;

            foreach (var tailLine in tailLines)
            {
                var parsedLine = ParseTailLineSnapshot(tailLine, candidateFile.Name, bucketName);
                if (parsedLine is not null)
                {
                    collectedLines.Add(parsedLine);
                }
            }
        }

        var orderedLines = collectedLines
            .OrderByDescending(item => item.OccurredAtUtc ?? DateTime.MinValue)
            .ThenByDescending(item => item.SourceFileName, StringComparer.Ordinal)
            .Take(UltraDebugLogDefaults.TailPreviewLineCount)
            .ToArray();

        return new UltraDebugLogTailSnapshot(
            bucketName,
            UltraDebugLogDefaults.TailPreviewLineCount,
            orderedLines.Length,
            candidateFiles.Length,
            truncated,
            orderedLines);
    }

    private SearchCandidateFileCollection ResolveSearchCandidateFiles(string bucketName, string? category, DateTime? fromUtc)
    {
        var bucketNames = string.Equals(bucketName, "all", StringComparison.OrdinalIgnoreCase)
            ? new[] { "normal", "ultra_debug" }
            : new[] { bucketName };
        var collectedFiles = new List<SearchCandidateFile>(UltraDebugLogDefaults.SearchPreviewMaxFiles);
        var totalFileCount = 0;

        foreach (var currentBucketName in bucketNames)
        {
            var directoryPath = ResolveBucketDirectoryPath(currentBucketName, category);
            if (!Directory.Exists(directoryPath))
            {
                continue;
            }

            var searchOption = string.IsNullOrWhiteSpace(category)
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;
            var matchingFiles = new DirectoryInfo(directoryPath)
                .GetFiles("*.ndjson", searchOption)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                .Select(file => new SearchCandidateFile(currentBucketName, file))
                .ToArray();

            totalFileCount += matchingFiles.Length;
            collectedFiles.AddRange(matchingFiles);
        }

        return new SearchCandidateFileCollection(
            totalFileCount,
            collectedFiles
                .OrderByDescending(item => item.File.LastWriteTimeUtc)
                .ThenByDescending(item => item.File.Name, StringComparer.Ordinal)
                .Take(UltraDebugLogDefaults.SearchPreviewMaxFiles)
                .ToArray());
    }

    private IReadOnlyCollection<UltraDebugLogEventSnapshot> ReadLatestCategoryEventSnapshots(string bucketName)
    {
        return UltraDebugLogDefaults.CategoryFolders
            .Select(categoryFolder => TryReadLatestCategoryEventSnapshot(bucketName, categoryFolder))
            .Where(item => item is not null)
            .Cast<UltraDebugLogEventSnapshot>()
            .OrderByDescending(item => item.OccurredAtUtc ?? DateTime.MinValue)
            .ThenBy(item => item.Category, StringComparer.Ordinal)
            .ToArray();
    }

    private UltraDebugLogEventSnapshot? TryReadLatestCategoryEventSnapshot(string bucketName, string categoryFolder)
    {
        var directoryPath = ResolveBucketDirectoryPath(bucketName, categoryFolder);
        if (!Directory.Exists(directoryPath))
        {
            return null;
        }

        var candidateFiles = new DirectoryInfo(directoryPath)
            .GetFiles("*.ndjson", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .ToArray();

        foreach (var candidateFile in candidateFiles)
        {
            var latestLine = File.ReadLines(candidateFile.FullName)
                .LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
            if (!string.IsNullOrWhiteSpace(latestLine))
            {
                var parsedSnapshot = ParseStructuredEventSnapshot(latestLine);
                if (parsedSnapshot is not null)
                {
                    return parsedSnapshot;
                }
            }
        }

        return null;
    }

    private static IReadOnlyCollection<string> ReadTailWindowLines(
        string filePath,
        int maxLines,
        int maxBytesToRead,
        out bool truncated)
    {
        truncated = false;
        if (maxLines <= 0 || maxBytesToRead <= 0 || !File.Exists(filePath))
        {
            return Array.Empty<string>();
        }

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length == 0)
        {
            return Array.Empty<string>();
        }

        var bytesToRead = (int)Math.Min(fileInfo.Length, maxBytesToRead);
        var startPosition = fileInfo.Length - bytesToRead;
        var buffer = new byte[bytesToRead];

        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        stream.Seek(startPosition, SeekOrigin.Begin);
        var bytesRead = stream.Read(buffer, 0, bytesToRead);

        var payload = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        if (startPosition > 0)
        {
            var firstNewLineIndex = payload.IndexOf('\n');
            payload = firstNewLineIndex >= 0 && firstNewLineIndex + 1 < payload.Length
                ? payload[(firstNewLineIndex + 1)..]
                : string.Empty;
            truncated = true;
        }

        var lines = payload
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .TakeLast(maxLines)
            .ToArray();

        if (lines.Length == maxLines && fileInfo.Length > bytesToRead)
        {
            truncated = true;
        }

        return lines;
    }

    private static UltraDebugLogEventSnapshot? ParseStructuredEventSnapshot(string rawLine)
    {
        try
        {
            using var document = JsonDocument.Parse(rawLine);
            var root = document.RootElement;
            var detailElement = TryParseDetailObject(root);
            return new UltraDebugLogEventSnapshot(
                Category: ResolveCategoryFolder(TryReadString(root, "category") ?? "runtime"),
                EventName: TryReadString(root, "eventName") ?? "unknown",
                Summary: TryReadString(root, "summary") ?? "Unavailable",
                OccurredAtUtc: TryReadDateTime(root, "occurredAtUtc"),
                CorrelationId: TryReadString(root, "correlationId"),
                Symbol: TryReadString(root, "symbol") ?? TryReadString(detailElement, "symbol") ?? TryReadString(detailElement, "selectedSymbol"),
                Timeframe: TryReadString(detailElement, "timeframe"),
                SourceLayer: TryReadString(detailElement, "sourceLayer"),
                DecisionReasonCode: TryReadString(detailElement, "decisionReasonCode"),
                BlockerCode: TryReadString(detailElement, "blockerCode"),
                LatencyBreakdownLabel: BuildLatencyBreakdownLabel(detailElement));
        }
        catch
        {
            return null;
        }
    }

    private static bool MatchesSearchRequest(
        UltraDebugLogTailLineSnapshot line,
        string? category,
        string? source,
        string? searchTerm,
        DateTime? fromUtc)
    {
        if (fromUtc.HasValue &&
            (!line.OccurredAtUtc.HasValue || line.OccurredAtUtc.Value < fromUtc.Value))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(category) &&
            !string.Equals(line.Category, category, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(source) &&
            !ContainsInsensitive(line.Source, source))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return true;
        }

        return ContainsInsensitive(line.EventName, searchTerm) ||
               ContainsInsensitive(line.Summary, searchTerm) ||
               ContainsInsensitive(line.DetailPreview, searchTerm) ||
               ContainsInsensitive(line.CorrelationId, searchTerm) ||
               ContainsInsensitive(line.Symbol, searchTerm) ||
               ContainsInsensitive(line.Source, searchTerm) ||
               ContainsInsensitive(line.SourceFileName, searchTerm);
    }

    private static UltraDebugLogTailLineSnapshot? ParseTailLineSnapshot(string rawLine, string sourceFileName, string? bucketName = null)
    {
        var normalizedSourceFileName = Path.GetFileName(sourceFileName);
        try
        {
            using var document = JsonDocument.Parse(rawLine);
            var root = document.RootElement;
            var detailElement = TryParseDetailObject(root);
            var detailMasked = TryReadString(root, "detailMasked");
            return new UltraDebugLogTailLineSnapshot(
                Category: ResolveCategoryFolder(TryReadString(root, "category") ?? "runtime"),
                EventName: SensitivePayloadMasker.Mask(TryReadString(root, "eventName"), 128) ?? "unknown",
                Summary: SensitivePayloadMasker.Mask(TryReadString(root, "summary"), 256) ?? "Unavailable",
                DetailPreview: BuildDetailPreview(detailMasked),
                OccurredAtUtc: TryReadDateTime(root, "occurredAtUtc"),
                CorrelationId: SensitivePayloadMasker.Mask(TryReadString(root, "correlationId"), 128),
                Symbol: SensitivePayloadMasker.Mask(TryReadString(root, "symbol"), 32),
                SourceFileName: normalizedSourceFileName,
                Source: SensitivePayloadMasker.Mask(
                    TryReadString(detailElement, "sourceLayer") ??
                    TryReadString(root, "application"),
                    128),
                BucketLabel: bucketName);
        }
        catch
        {
            return new UltraDebugLogTailLineSnapshot(
                Category: "runtime",
                EventName: "raw_tail_line",
                Summary: SensitivePayloadMasker.Mask(rawLine, 256) ?? "Unavailable",
                DetailPreview: null,
                OccurredAtUtc: null,
                CorrelationId: null,
                Symbol: null,
                SourceFileName: normalizedSourceFileName,
                Source: null,
                BucketLabel: bucketName);
        }
    }

    private static JsonElement? TryParseDetailObject(JsonElement root)
    {
        var detailMasked = TryReadString(root, "detailMasked");
        if (string.IsNullOrWhiteSpace(detailMasked))
        {
            return null;
        }

        try
        {
            using var detailDocument = JsonDocument.Parse(detailMasked);
            return detailDocument.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadString(JsonElement? element, string propertyName)
    {
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return element.Value.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : property.ToString()
            : null;
    }

    private static DateTime? TryReadDateTime(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.TryGetDateTime(out var value) ? value : null;
    }

    private static string? BuildLatencyBreakdownLabel(JsonElement? detailElement)
    {
        if (!detailElement.HasValue ||
            detailElement.Value.ValueKind != JsonValueKind.Object ||
            !detailElement.Value.TryGetProperty("latencyBreakdown", out var latencyElement) ||
            latencyElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var segments = new List<string>(7);
        TryAppendLatencySegment(latencyElement, "totalMs", "total", segments);
        TryAppendLatencySegment(latencyElement, "scannerMs", "scanner", segments);
        TryAppendLatencySegment(latencyElement, "strategyMs", "strategy", segments);
        TryAppendLatencySegment(latencyElement, "handoffMs", "handoff", segments);
        TryAppendLatencySegment(latencyElement, "executionMs", "execution", segments);
        TryAppendLatencySegment(latencyElement, "exchangeMs", "exchange", segments);
        TryAppendLatencySegment(latencyElement, "persistMs", "persist", segments);
        return segments.Count == 0 ? null : string.Join(" | ", segments);
    }

    private static void TryAppendLatencySegment(JsonElement latencyElement, string propertyName, string label, ICollection<string> segments)
    {
        if (!latencyElement.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (property.TryGetInt32(out var intValue))
        {
            segments.Add($"{label}={intValue}ms");
        }
        else if (property.TryGetInt64(out var longValue))
        {
            segments.Add($"{label}={longValue}ms");
        }
    }

    private static string? BuildDetailPreview(string? detailMasked)
    {
        if (string.IsNullOrWhiteSpace(detailMasked))
        {
            return null;
        }

        var normalizedDetail = detailMasked
            .Replace("\\r", " ", StringComparison.Ordinal)
            .Replace("\\n", " ", StringComparison.Ordinal);
        return SensitivePayloadMasker.Mask(normalizedDetail, UltraDebugLogDefaults.TailPreviewDetailMaxLength);
    }

    private static string NormalizeSearchBucketName(string? bucketName)
    {
        return bucketName?.Trim().ToLowerInvariant() switch
        {
            "normal" => "normal",
            "ultra_debug" => "ultra_debug",
            _ => "all"
        };
    }

    private static string? NormalizeSearchCategory(string? category)
    {
        var normalizedCategory = NormalizeOptional(category, 32)?.ToLowerInvariant();
        return !string.IsNullOrWhiteSpace(normalizedCategory) &&
               UltraDebugLogDefaults.CategoryFolders.Contains(normalizedCategory, StringComparer.OrdinalIgnoreCase)
            ? normalizedCategory
            : null;
    }

    private static bool ContainsInsensitive(string? haystack, string needle)
    {
        return !string.IsNullOrWhiteSpace(haystack) &&
               haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private Task RotateFileIfNeededAsync(string activeFilePath, int rotateThresholdMb, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        if (rotateThresholdMb <= 0 || !File.Exists(activeFilePath))
        {
            return Task.CompletedTask;
        }

        var thresholdBytes = checked((long)rotateThresholdMb * 1024L * 1024L);
        var activeFileInfo = new FileInfo(activeFilePath);
        if (activeFileInfo.Length <= thresholdBytes)
        {
            return Task.CompletedTask;
        }

        var rotatedFilePath = ResolveNextRotatedFilePath(activeFileInfo);
        File.Move(activeFilePath, rotatedFilePath);
        using var stream = new FileStream(
            activeFilePath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.ReadWrite);
        return Task.CompletedTask;
    }

    private static string ResolveNextRotatedFilePath(FileInfo activeFileInfo)
    {
        var directoryPath = activeFileInfo.DirectoryName
            ?? throw new InvalidOperationException("Ultra debug log bucket path is invalid.");
        var activeFileNameWithoutExtension = Path.GetFileNameWithoutExtension(activeFileInfo.Name);
        var prefix = $"{activeFileNameWithoutExtension}-";
        var nextSequence = new DirectoryInfo(directoryPath)
            .GetFiles($"{activeFileNameWithoutExtension}-*.ndjson", SearchOption.TopDirectoryOnly)
            .Select(file => Path.GetFileNameWithoutExtension(file.Name))
            .Select(fileName =>
            {
                if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }

                return int.TryParse(fileName[prefix.Length..], out var parsedValue)
                    ? parsedValue
                    : 0;
            })
            .DefaultIfEmpty(0)
            .Max() + 1;

        return Path.Combine(directoryPath, $"{activeFileNameWithoutExtension}-{nextSequence:000}.ndjson");
    }

    private void EnsureUltraBucketWritable()
    {
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var ultraFilePath = ResolveBucketFilePath("ultra_debug", utcNow, "runtime");
        using var stream = new FileStream(
            ultraFilePath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.ReadWrite);
    }

    private static string ResolveCategoryFolder(string category)
    {
        var normalizedCategory = category.Trim();
        if (normalizedCategory.StartsWith("scanner.handoff", StringComparison.OrdinalIgnoreCase))
        {
            return "handoff";
        }

        if (normalizedCategory.StartsWith("scanner", StringComparison.OrdinalIgnoreCase))
        {
            return "scanner";
        }

        if (normalizedCategory.StartsWith("strategy", StringComparison.OrdinalIgnoreCase))
        {
            return "strategy";
        }

        if (normalizedCategory.StartsWith("execution", StringComparison.OrdinalIgnoreCase))
        {
            return "execution";
        }

        if (normalizedCategory.StartsWith("exchange", StringComparison.OrdinalIgnoreCase))
        {
            return "exchange";
        }

        return "runtime";
    }

    private static string? ResolveBucketRootDirectoryPath(string activeFilePath)
    {
        var directoryPath = Path.GetDirectoryName(activeFilePath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return null;
        }

        var directory = new DirectoryInfo(directoryPath);
        return directory.Parent is not null &&
               UltraDebugLogDefaults.CategoryFolders.Contains(directory.Name, StringComparer.OrdinalIgnoreCase)
            ? directory.Parent.FullName
            : directory.FullName;
    }

    private static void EnsureMaskingPipelineSafe()
    {
        const string probePayload = """{"apiKey":"plain-key","token":"plain-token","cookie":"session-cookie","password":"db-password"}""";
        var masked = SensitivePayloadMasker.Mask(probePayload, 512);

        if (string.IsNullOrWhiteSpace(masked) ||
            masked.Contains("plain-key", StringComparison.Ordinal) ||
            masked.Contains("plain-token", StringComparison.Ordinal) ||
            masked.Contains("session-cookie", StringComparison.Ordinal) ||
            masked.Contains("db-password", StringComparison.Ordinal))
        {
            throw new UltraDebugLogOperationException(
                "UltraLogMaskingValidationFailed",
                "Ultra log activation failed because the masking pipeline did not redact probe secrets.");
        }
    }

    private static string? SerializeMaskedDetail(object? detail)
    {
        if (detail is null)
        {
            return null;
        }

        try
        {
            return SensitivePayloadMasker.Mask(JsonSerializer.Serialize(detail, SerializerOptions), 8192);
        }
        catch (Exception exception)
        {
            return SensitivePayloadMasker.Mask(
                JsonSerializer.Serialize(
                    new
                    {
                        serializationError = Truncate(exception.Message, 256),
                        detailType = detail.GetType().FullName
                    },
                    SerializerOptions),
                512);
        }
    }

    private static UltraDebugLogSnapshot MapSnapshot(Domain.Entities.UltraDebugLogState entity, bool isPersisted)
    {
        return new UltraDebugLogSnapshot(
            entity.IsEnabled,
            entity.StartedAtUtc,
            entity.ExpiresAtUtc,
            entity.DurationKey,
            entity.EnabledByAdminId,
            entity.EnabledByAdminEmail,
            entity.AutoDisabledReason,
            entity.UpdatedAtUtc,
            isPersisted,
            entity.NormalLogsLimitMb,
            entity.UltraLogsLimitMb,
            0L,
            0L);
    }

    private static string BuildStateSummary(Domain.Entities.UltraDebugLogState entity)
    {
        return string.Join(
            " | ",
            $"Enabled={entity.IsEnabled}",
            $"StartedAtUtc={entity.StartedAtUtc?.ToString("O") ?? "none"}",
            $"ExpiresAtUtc={entity.ExpiresAtUtc?.ToString("O") ?? "none"}",
            $"DurationKey={entity.DurationKey ?? "none"}",
            $"NormalLogsLimitMb={entity.NormalLogsLimitMb?.ToString() ?? "none"}",
            $"UltraLogsLimitMb={entity.UltraLogsLimitMb?.ToString() ?? "none"}",
            $"EnabledByAdminId={entity.EnabledByAdminId ?? "none"}",
            $"EnabledByAdminEmail={entity.EnabledByAdminEmail ?? "none"}",
            $"AutoDisabledReason={entity.AutoDisabledReason ?? "none"}");
    }

    private static string ResolveApplicationName(string? rawValue)
    {
        var normalizedValue = string.IsNullOrWhiteSpace(rawValue)
            ? "coinbot"
            : rawValue.Trim();
        var sanitized = new string(
            normalizedValue
                .ToLowerInvariant()
                .Select(character => char.IsLetterOrDigit(character) ? character : '-')
                .ToArray())
            .Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "coinbot" : sanitized;
    }

    private static void ValidateRequestActor(string actorUserId)
    {
        _ = NormalizeRequired(actorUserId, 450, nameof(actorUserId));
    }

    private static string NormalizeRequired(string? value, int maxLength, string parameterName)
    {
        var normalizedValue = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : throw new ArgumentOutOfRangeException(parameterName, $"The value cannot exceed {maxLength} characters.");
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalizedValue = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return null;
        }

        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : normalizedValue[..maxLength];
    }

    private static string? Truncate(string? value, int maxLength)
    {
        var normalizedValue = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return null;
        }

        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : normalizedValue[..maxLength];
    }

    private sealed record SearchCandidateFile(string BucketName, FileInfo File);

    private sealed record SearchCandidateFileCollection(
        int TotalFileCount,
        IReadOnlyCollection<SearchCandidateFile> Files);

    private sealed record UltraDebugLogRecord(
        DateTime OccurredAtUtc,
        string Application,
        string MachineName,
        string Category,
        string EventName,
        string Summary,
        string? CorrelationId,
        string? Symbol,
        string? ExecutionAttemptId,
        string? StrategySignalId,
        string? DetailMasked);
}
