using System.Linq;
using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Administration;

public sealed class UltraDebugLogService(
    IServiceScopeFactory serviceScopeFactory,
    TimeProvider timeProvider,
    ILogger<UltraDebugLogService> logger,
    IHostEnvironment? hostEnvironment = null,
    Func<string, long?>? availableDiskBytesResolver = null,
    IOptions<UltraDebugLogRetentionOptions>? retentionOptions = null) : IUltraDebugLogService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim fileWriteLock = new(1, 1);
    private readonly string applicationName = ResolveApplicationName(hostEnvironment?.ApplicationName);
    private readonly Func<string, long?> diskFreeSpaceResolver = availableDiskBytesResolver ?? ResolveAvailableDiskBytes;
    private readonly UltraDebugLogRetentionOptions retentionOptionsValue = retentionOptions?.Value ?? new();
    private readonly object retentionHeartbeatLock = new();
    private RetentionHeartbeatState retentionHeartbeatState = RetentionHeartbeatState.Empty;

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

    public async Task<UltraDebugLogHealthSnapshot> GetHealthSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var checkedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var thresholdBytes = checked((long)UltraDebugLogDefaults.DiskPressureFreeSpaceThresholdMb * 1024L * 1024L);
        var warningThresholdBytes = checked(thresholdBytes * 2L);

        try
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var entity = await dbContext.Set<Domain.Entities.UltraDebugLogState>()
                .SingleOrDefaultAsync(item => item.Id == UltraDebugLogDefaults.SingletonId, cancellationToken);

            var snapshot = entity is null
                ? UltraDebugLogDefaults.CreateSnapshot()
                : MapSnapshot(entity, isPersisted: true);
            var autoDisabledReason = snapshot.AutoDisabledReason;
            var isEnabled = snapshot.IsEnabled;

            if (isEnabled &&
                snapshot.ExpiresAtUtc.HasValue &&
                snapshot.ExpiresAtUtc.Value <= checkedAtUtc)
            {
                isEnabled = false;
                autoDisabledReason = UltraDebugLogDefaults.DurationExpiredReason;
            }

            var diskSnapshot = ProbeDiskSpace(ResolveLogRootPath());
            var diskPressureState = ResolveDiskPressureState(
                isEnabled,
                autoDisabledReason,
                diskSnapshot,
                thresholdBytes,
                warningThresholdBytes);
            var affectedBuckets = ResolveAffectedLogBuckets(autoDisabledReason, diskSnapshot.IsWritable, diskPressureState);
            var fallbackMode = string.Equals(autoDisabledReason, UltraDebugLogDefaults.DiskPressureReason, StringComparison.Ordinal) ||
                               string.Equals(autoDisabledReason, UltraDebugLogDefaults.RuntimeWriteFailureReason, StringComparison.Ordinal) ||
                               (diskSnapshot.FreeBytes.HasValue && IsDiskPressure(diskSnapshot.FreeBytes));
            var retentionHeartbeat = GetRetentionHeartbeatState();

            return new UltraDebugLogHealthSnapshot(
                DiskPressureState: diskPressureState,
                FreeBytes: diskSnapshot.FreeBytes,
                FreePercent: diskSnapshot.FreePercent,
                ThresholdBytes: thresholdBytes,
                AffectedLogBuckets: affectedBuckets,
                LastCheckedAtUtc: checkedAtUtc,
                LastEscalationReason: ResolveHealthEscalationReason(autoDisabledReason, diskSnapshot.FailureReason),
                IsWritable: diskSnapshot.IsWritable,
                IsTailAvailable: diskSnapshot.IsWritable,
                IsExportAvailable: diskSnapshot.IsWritable,
                LastRetentionCompletedAtUtc: retentionHeartbeat.CompletedAtUtc,
                LastRetentionReasonCode: retentionHeartbeat.ReasonCode,
                LastRetentionSucceeded: retentionHeartbeat.Succeeded,
                IsNormalFallbackMode: fallbackMode,
                AutoDisabledReason: autoDisabledReason,
                IsEnabled: isEnabled);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Ultra debug log health snapshot failed. The monitoring summary will stay degraded.");
            var retentionHeartbeat = GetRetentionHeartbeatState();
            return new UltraDebugLogHealthSnapshot(
                DiskPressureState: "Degraded",
                FreeBytes: null,
                FreePercent: null,
                ThresholdBytes: thresholdBytes,
                AffectedLogBuckets: ["normal", "ultra_debug"],
                LastCheckedAtUtc: checkedAtUtc,
                LastEscalationReason: "HealthSnapshotRuntimeFailed",
                IsWritable: false,
                IsTailAvailable: false,
                IsExportAvailable: false,
                LastRetentionCompletedAtUtc: retentionHeartbeat.CompletedAtUtc,
                LastRetentionReasonCode: retentionHeartbeat.ReasonCode,
                LastRetentionSucceeded: retentionHeartbeat.Succeeded,
                IsNormalFallbackMode: false,
                AutoDisabledReason: UltraDebugLogDefaults.RuntimeErrorReason,
                IsEnabled: false);
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

    public Task<UltraDebugLogExportSnapshot> ExportAsync(
        UltraDebugLogExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedBucketName = NormalizeExportBucketName(request.BucketName);
            var normalizedCategory = NormalizeExportCategory(request.Category);
            var normalizedSource = NormalizeExportFilter(request.Source, 128, nameof(request.Source));
            var normalizedSearchTerm = NormalizeOptional(request.SearchTerm, 128);
            var normalizedRange = NormalizeExportRange(request.FromUtc, request.ToUtc);
            var normalizedMaxRows = NormalizeExportTake(request.MaxRows);
            var candidateFiles = ResolveExportCandidateFiles(
                normalizedBucketName,
                normalizedCategory,
                normalizedRange.FromUtc,
                normalizedRange.ToUtc);
            var collectedLines = new List<ExportCandidateLine>(normalizedMaxRows);
            var filesScanned = 0;
            var truncated = candidateFiles.TotalFileCount > candidateFiles.Files.Count;

            foreach (var candidateFile in candidateFiles.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                filesScanned++;

                foreach (var rawLine in File.ReadLines(candidateFile.File.FullName))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var parsedLine = ParseTailLineSnapshot(rawLine, candidateFile.File.Name, candidateFile.BucketName);
                    if (parsedLine is null ||
                        !MatchesExportRequest(
                            parsedLine,
                            normalizedCategory,
                            normalizedSource,
                            normalizedSearchTerm,
                            normalizedRange.FromUtc,
                            normalizedRange.ToUtc))
                    {
                        continue;
                    }

                    collectedLines.Add(new ExportCandidateLine(
                        parsedLine.OccurredAtUtc ?? DateTime.MinValue,
                        MaskExportLine(rawLine)));

                    if (collectedLines.Count >= normalizedMaxRows)
                    {
                        truncated = true;
                        break;
                    }
                }

                if (collectedLines.Count >= normalizedMaxRows)
                {
                    break;
                }
            }

            var orderedLines = collectedLines
                .OrderByDescending(item => item.OccurredAtUtc)
                .Select(item => item.MaskedLine)
                .ToArray();
            var boundedPayload = BuildBoundedExportPayload(orderedLines, ref truncated);
            var isEmpty = boundedPayload.Lines.Count == 0;
            var payloadLines = isEmpty
                ? new[] { BuildEmptyExportPayload(normalizedBucketName, normalizedCategory, normalizedRange.FromUtc, normalizedRange.ToUtc) }
                : boundedPayload.Lines.ToArray();
            var ndjsonPayload = string.Join('\n', payloadLines);
            if (!ndjsonPayload.EndsWith('\n'))
            {
                ndjsonPayload += "\n";
            }

            var baseFileName = BuildExportFileName(
                normalizedBucketName,
                normalizedCategory,
                normalizedRange.FromUtc,
                normalizedRange.ToUtc);
            var ndjsonFileName = $"{baseFileName}.ndjson";
            var content = request.ZipPackage
                ? BuildZipPayload(ndjsonFileName, ndjsonPayload)
                : Encoding.UTF8.GetBytes(ndjsonPayload);

            return Task.FromResult(new UltraDebugLogExportSnapshot(
                ContentType: request.ZipPackage ? "application/zip" : "application/x-ndjson",
                FileDownloadName: request.ZipPackage ? $"{baseFileName}.zip" : ndjsonFileName,
                Content: content,
                FromUtc: normalizedRange.FromUtc,
                ToUtc: normalizedRange.ToUtc,
                RequestedLineCount: normalizedMaxRows,
                ExportedLineCount: boundedPayload.Lines.Count,
                FilesScanned: filesScanned,
                IsTruncated: truncated,
                IsEmpty: isEmpty,
                EmptyReason: isEmpty ? "No masked log lines matched the requested export window." : null));
        }
        catch (UltraDebugLogOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Ultra debug log export failed. The request will stay fail-closed.");
            throw new UltraDebugLogOperationException(
                "UltraLogExportRuntimeFailed",
                "Masked log export failed at runtime.");
        }
    }

    public async Task<UltraDebugLogRetentionRunSnapshot> ApplyRetentionAsync(
        UltraDebugLogRetentionRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var normalizedBucketNames = ResolveRetentionBucketNames(request.BucketName);
        var normalizedMaxFiles = NormalizeRetentionMaxFiles(request.MaxFiles);
        var dryRun = request.DryRun;

        if (!retentionOptionsValue.Enabled)
        {
            var disabledSnapshot = new UltraDebugLogRetentionRunSnapshot(
                StartedAtUtc: startedAtUtc,
                CompletedAtUtc: startedAtUtc,
                DryRun: dryRun,
                ScannedFiles: 0,
                DeletedFiles: 0,
                SkippedFiles: 0,
                ReclaimedBytes: 0,
                CandidateDeleteFiles: 0,
                CandidateReclaimedBytes: 0,
                ReasonCode: "Disabled",
                Buckets: normalizedBucketNames
                    .Select(bucketName => new UltraDebugLogRetentionBucketSnapshot(
                        BucketName: bucketName,
                        RetentionDays: ResolveRetentionDays(bucketName),
                        ScannedFiles: 0,
                        DeletedFiles: 0,
                        SkippedFiles: 0,
                        ReclaimedBytes: 0,
                        CandidateDeleteFiles: 0,
                        CandidateReclaimedBytes: 0,
                        IsTruncated: false,
                        ReasonCode: "Disabled"))
                    .ToArray());
            RecordRetentionHeartbeat(disabledSnapshot);
            return disabledSnapshot;
        }

        await fileWriteLock.WaitAsync(cancellationToken);
        try
        {
            var logRootPath = EnsureAllowedLogRootPath();
            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
            var bucketSnapshots = new List<UltraDebugLogRetentionBucketSnapshot>(normalizedBucketNames.Length);
            var remainingFilesBudget = normalizedMaxFiles;

            foreach (var bucketName in normalizedBucketNames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var bucketSnapshot = ApplyBucketRetention(
                    logRootPath,
                    bucketName,
                    nowUtc,
                    dryRun,
                    remainingFilesBudget,
                    cancellationToken);
                bucketSnapshots.Add(bucketSnapshot);
                remainingFilesBudget = Math.Max(0, remainingFilesBudget - bucketSnapshot.ScannedFiles);
            }

            var completedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            var totalScanned = bucketSnapshots.Sum(item => item.ScannedFiles);
            var totalDeleted = bucketSnapshots.Sum(item => item.DeletedFiles);
            var totalSkipped = bucketSnapshots.Sum(item => item.SkippedFiles);
            var totalReclaimedBytes = bucketSnapshots.Sum(item => item.ReclaimedBytes);
            var totalCandidateDeleteFiles = bucketSnapshots.Sum(item => item.CandidateDeleteFiles);
            var totalCandidateReclaimedBytes = bucketSnapshots.Sum(item => item.CandidateReclaimedBytes);
            var reasonCode = bucketSnapshots.Any(item => string.Equals(item.ReasonCode, "ScanLimitReached", StringComparison.Ordinal))
                ? "ScanLimitReached"
                : dryRun
                    ? "DryRun"
                    : "Completed";

            var completedSnapshot = new UltraDebugLogRetentionRunSnapshot(
                StartedAtUtc: startedAtUtc,
                CompletedAtUtc: completedAtUtc,
                DryRun: dryRun,
                ScannedFiles: totalScanned,
                DeletedFiles: totalDeleted,
                SkippedFiles: totalSkipped,
                ReclaimedBytes: totalReclaimedBytes,
                CandidateDeleteFiles: totalCandidateDeleteFiles,
                CandidateReclaimedBytes: totalCandidateReclaimedBytes,
                ReasonCode: reasonCode,
                Buckets: bucketSnapshots.ToArray());
            RecordRetentionHeartbeat(completedSnapshot);
            return completedSnapshot;
        }
        catch (UltraDebugLogOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Ultra debug log retention failed. The janitor will stay fail-closed.");
            var completedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            var failedSnapshot = new UltraDebugLogRetentionRunSnapshot(
                StartedAtUtc: startedAtUtc,
                CompletedAtUtc: completedAtUtc,
                DryRun: dryRun,
                ScannedFiles: 0,
                DeletedFiles: 0,
                SkippedFiles: 0,
                ReclaimedBytes: 0,
                CandidateDeleteFiles: 0,
                CandidateReclaimedBytes: 0,
                ReasonCode: "RuntimeFailure",
                Buckets: Array.Empty<UltraDebugLogRetentionBucketSnapshot>());
            RecordRetentionHeartbeat(failedSnapshot);
            return failedSnapshot;
        }
        finally
        {
            fileWriteLock.Release();
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
            var logRootPath = EnsureAllowedLogRootPath();
            var bucketRootPath = ResolveBucketRootDirectoryPath(activeFilePath);
            if (string.IsNullOrWhiteSpace(bucketRootPath) || !Directory.Exists(bucketRootPath))
            {
                return true;
            }

            var normalizedBucketRootPath = EnsureAllowedBucketRootPath(logRootPath, Path.GetFileName(bucketRootPath));
            var files = EnumerateSafeBucketFiles(normalizedBucketRootPath, int.MaxValue, out _, out _, cancellationToken)
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
                DeleteFileWithinAllowedRoot(file, normalizedBucketRootPath);
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
        var logRootPath = EnsureAllowedLogRootPath();
        var bucketRootPath = ResolveBucketRootDirectoryPath(activeFilePath);
        if (string.IsNullOrWhiteSpace(bucketRootPath) || !Directory.Exists(bucketRootPath))
        {
            return Task.FromResult(0L);
        }

        var normalizedBucketRootPath = EnsureAllowedBucketRootPath(logRootPath, Path.GetFileName(bucketRootPath));
        var totalBytes = EnumerateSafeBucketFiles(normalizedBucketRootPath, int.MaxValue, out _, out _, cancellationToken)
            .Sum(file => file.Length);
        return Task.FromResult(totalBytes);
    }

    private UltraDebugLogRetentionBucketSnapshot ApplyBucketRetention(
        string logRootPath,
        string bucketName,
        DateTime nowUtc,
        bool dryRun,
        int maxFilesToScan,
        CancellationToken cancellationToken)
    {
        var retentionDays = ResolveRetentionDays(bucketName);
        if (maxFilesToScan <= 0)
        {
            return new UltraDebugLogRetentionBucketSnapshot(
                BucketName: bucketName,
                RetentionDays: retentionDays,
                ScannedFiles: 0,
                DeletedFiles: 0,
                SkippedFiles: 0,
                ReclaimedBytes: 0,
                CandidateDeleteFiles: 0,
                CandidateReclaimedBytes: 0,
                IsTruncated: true,
                ReasonCode: "ScanLimitReached");
        }

        var bucketRootPath = EnsureAllowedBucketRootPath(logRootPath, bucketName);
        if (!Directory.Exists(bucketRootPath))
        {
            return new UltraDebugLogRetentionBucketSnapshot(
                BucketName: bucketName,
                RetentionDays: retentionDays,
                ScannedFiles: 0,
                DeletedFiles: 0,
                SkippedFiles: 0,
                ReclaimedBytes: 0,
                CandidateDeleteFiles: 0,
                CandidateReclaimedBytes: 0,
                IsTruncated: false,
                ReasonCode: "BucketMissing");
        }

        var files = EnumerateSafeBucketFiles(bucketRootPath, maxFilesToScan, out var skippedByEnumeration, out var truncated, cancellationToken)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .ToArray();
        var keepProtectedPaths = files
            .Take(retentionOptionsValue.MinimumKeepFileCount)
            .Select(file => Path.GetFullPath(file.FullName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cutoffUtc = nowUtc.AddDays(-retentionDays);

        var scannedFiles = 0;
        var deletedFiles = 0;
        var skippedFiles = skippedByEnumeration;
        var reclaimedBytes = 0L;
        var candidateDeleteFiles = 0;
        var candidateReclaimedBytes = 0L;

        foreach (var file in files
                     .OrderBy(item => item.LastWriteTimeUtc)
                     .ThenBy(item => item.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scannedFiles++;

            if (IsProtectedActiveFile(file, nowUtc) ||
                keepProtectedPaths.Contains(Path.GetFullPath(file.FullName)))
            {
                skippedFiles++;
                continue;
            }

            if (file.LastWriteTimeUtc >= cutoffUtc)
            {
                skippedFiles++;
                continue;
            }

            candidateDeleteFiles++;
            candidateReclaimedBytes += file.Length;

            if (dryRun)
            {
                skippedFiles++;
                continue;
            }

            try
            {
                DeleteFileWithinAllowedRoot(file, bucketRootPath);
                deletedFiles++;
                reclaimedBytes += file.Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(
                    exception,
                    "Ultra debug log retention skipped a file after delete failure. Bucket={BucketName} File={FileName}.",
                    bucketName,
                    file.Name);
                skippedFiles++;
            }
        }

        var reasonCode = truncated
            ? "ScanLimitReached"
            : dryRun
                ? "DryRun"
                : "Completed";

        return new UltraDebugLogRetentionBucketSnapshot(
            BucketName: bucketName,
            RetentionDays: retentionDays,
            ScannedFiles: scannedFiles,
            DeletedFiles: deletedFiles,
            SkippedFiles: skippedFiles,
            ReclaimedBytes: reclaimedBytes,
            CandidateDeleteFiles: candidateDeleteFiles,
            CandidateReclaimedBytes: candidateReclaimedBytes,
            IsTruncated: truncated,
            ReasonCode: reasonCode);
    }

    private int ResolveRetentionDays(string bucketName)
    {
        return string.Equals(bucketName, "ultra_debug", StringComparison.OrdinalIgnoreCase)
            ? retentionOptionsValue.UltraRetentionDays
            : retentionOptionsValue.NormalRetentionDays;
    }

    private int NormalizeRetentionMaxFiles(int? maxFiles)
    {
        var configuredMaxFiles = Math.Max(1, retentionOptionsValue.MaxFilesPerRun);
        return Math.Clamp(maxFiles ?? configuredMaxFiles, 1, configuredMaxFiles);
    }

    private static string[] ResolveRetentionBucketNames(string? bucketName)
    {
        if (!string.IsNullOrWhiteSpace(bucketName) &&
            (bucketName.Contains("..", StringComparison.Ordinal) ||
             bucketName.Contains('/', StringComparison.Ordinal) ||
             bucketName.Contains('\\', StringComparison.Ordinal) ||
             bucketName.Contains(':', StringComparison.Ordinal)))
        {
            throw new UltraDebugLogOperationException(
                "UltraLogRetentionPathInvalid",
                "Ultra debug log retention failed because the selected bucket contains an invalid path fragment.");
        }

        return bucketName?.Trim().ToLowerInvariant() switch
        {
            null or "" or "all" => UltraDebugLogDefaults.BucketNames,
            "normal" => ["normal"],
            "ultra_debug" => ["ultra_debug"],
            _ => throw new UltraDebugLogOperationException(
                "UltraLogRetentionBucketInvalid",
                "Ultra debug log retention failed because the selected bucket is invalid.")
        };
    }

    private string EnsureAllowedLogRootPath()
    {
        var logRootPath = Path.GetFullPath(ResolveLogRootPath());
        Directory.CreateDirectory(logRootPath);
        return logRootPath;
    }

    private string EnsureAllowedBucketRootPath(string logRootPath, string bucketName)
    {
        if (!UltraDebugLogDefaults.BucketNames.Contains(bucketName, StringComparer.OrdinalIgnoreCase))
        {
            throw new UltraDebugLogOperationException(
                "UltraLogRetentionBucketInvalid",
                "Ultra debug log retention failed because the selected bucket is invalid.");
        }

        var bucketRootPath = Path.GetFullPath(ResolveBucketDirectoryPath(bucketName));
        if (!IsPathWithinRoot(bucketRootPath, logRootPath))
        {
            throw new UltraDebugLogOperationException(
                "UltraLogRetentionPathInvalid",
                "Ultra debug log retention failed because the resolved bucket path is outside the allowed log root.");
        }

        return bucketRootPath;
    }

    private static IReadOnlyCollection<FileInfo> EnumerateSafeBucketFiles(
        string bucketRootPath,
        int maxFiles,
        out int skippedFiles,
        out bool truncated,
        CancellationToken cancellationToken)
    {
        skippedFiles = 0;
        truncated = false;

        var normalizedBucketRootPath = Path.GetFullPath(bucketRootPath);
        var pendingDirectories = new Stack<DirectoryInfo>();
        pendingDirectories.Push(new DirectoryInfo(normalizedBucketRootPath));
        var files = new List<FileInfo>(Math.Min(maxFiles, 128));

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directory = pendingDirectories.Pop();
            if (!directory.Exists)
            {
                continue;
            }

            if (!IsPathWithinRoot(directory.FullName, normalizedBucketRootPath) ||
                (!string.Equals(Path.GetFullPath(directory.FullName), normalizedBucketRootPath, StringComparison.OrdinalIgnoreCase) &&
                 IsReparsePoint(directory.Attributes)))
            {
                skippedFiles++;
                continue;
            }

            foreach (var file in directory.GetFiles("*.ndjson", SearchOption.TopDirectoryOnly))
            {
                if (files.Count >= maxFiles)
                {
                    truncated = true;
                    return files;
                }

                if (!IsPathWithinRoot(file.FullName, normalizedBucketRootPath) || IsReparsePoint(file.Attributes))
                {
                    skippedFiles++;
                    continue;
                }

                files.Add(file);
            }

            foreach (var childDirectory in directory.GetDirectories())
            {
                if (!IsPathWithinRoot(childDirectory.FullName, normalizedBucketRootPath) || IsReparsePoint(childDirectory.Attributes))
                {
                    skippedFiles++;
                    continue;
                }

                pendingDirectories.Push(childDirectory);
            }
        }

        return files;
    }

    private bool IsProtectedActiveFile(FileInfo file, DateTime nowUtc)
    {
        return string.Equals(
            file.Name,
            $"{applicationName}-{nowUtc:yyyyMMdd}.ndjson",
            StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteFileWithinAllowedRoot(FileInfo file, string allowedRootPath)
    {
        if (!IsPathWithinRoot(file.FullName, allowedRootPath))
        {
            throw new UltraDebugLogOperationException(
                "UltraLogRetentionPathInvalid",
                "Ultra debug log retention failed because a candidate file resolved outside the allowed root.");
        }

        if (IsReparsePoint(file.Attributes))
        {
            throw new UltraDebugLogOperationException(
                "UltraLogRetentionReparsePointInvalid",
                "Ultra debug log retention failed because a candidate file uses a reparse point.");
        }

        file.Delete();
    }

    private static bool IsPathWithinRoot(string candidatePath, string allowedRootPath)
    {
        var normalizedCandidatePath = Path.GetFullPath(candidatePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedAllowedRootPath = Path.GetFullPath(allowedRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(normalizedCandidatePath, normalizedAllowedRootPath, StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidatePath.StartsWith(
                   normalizedAllowedRootPath + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReparsePoint(FileAttributes attributes)
    {
        return (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
    }

    private DiskSpaceSnapshot ProbeDiskSpace(string logRootPath)
    {
        try
        {
            var normalizedLogRootPath = Path.GetFullPath(logRootPath);
            if (File.Exists(normalizedLogRootPath))
            {
                return new DiskSpaceSnapshot(
                    FreeBytes: null,
                    FreePercent: null,
                    IsWritable: false,
                    FailureReason: "LogRootInvalid");
            }

            var rootPath = Path.GetPathRoot(normalizedLogRootPath);
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return new DiskSpaceSnapshot(
                    FreeBytes: null,
                    FreePercent: null,
                    IsWritable: false,
                    FailureReason: "DiskRootUnavailable");
            }

            var freeBytes = diskFreeSpaceResolver(normalizedLogRootPath);
            var driveInfo = new DriveInfo(rootPath);
            decimal? freePercent = freeBytes.HasValue && driveInfo.TotalSize > 0
                ? Math.Round(100m * freeBytes.Value / driveInfo.TotalSize, 2, MidpointRounding.AwayFromZero)
                : null;

            return new DiskSpaceSnapshot(
                FreeBytes: freeBytes,
                FreePercent: freePercent,
                IsWritable: true,
                FailureReason: null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Ultra debug log disk health probe failed.");
            return new DiskSpaceSnapshot(
                FreeBytes: null,
                FreePercent: null,
                IsWritable: false,
                FailureReason: "DiskCheckUnavailable");
        }
    }

    private static IReadOnlyCollection<string> ResolveAffectedLogBuckets(
        string? autoDisabledReason,
        bool isWritable,
        string diskPressureState)
    {
        if (!isWritable)
        {
            return ["normal", "ultra_debug"];
        }

        if (string.Equals(diskPressureState, "Critical", StringComparison.Ordinal))
        {
            return ["ultra_debug"];
        }

        return autoDisabledReason switch
        {
            UltraDebugLogDefaults.DiskPressureReason => ["ultra_debug"],
            UltraDebugLogDefaults.RuntimeWriteFailureReason => ["ultra_debug"],
            UltraDebugLogDefaults.SizeLimitExceededReason => ["ultra_debug"],
            UltraDebugLogDefaults.RuntimeErrorReason => ["ultra_debug"],
            _ => Array.Empty<string>()
        };
    }

    private static string ResolveDiskPressureState(
        bool isEnabled,
        string? autoDisabledReason,
        DiskSpaceSnapshot diskSnapshot,
        long thresholdBytes,
        long warningThresholdBytes)
    {
        if (!string.IsNullOrWhiteSpace(diskSnapshot.FailureReason))
        {
            return "Degraded";
        }

        if (string.Equals(autoDisabledReason, UltraDebugLogDefaults.DiskPressureReason, StringComparison.Ordinal) ||
            (diskSnapshot.FreeBytes.HasValue && diskSnapshot.FreeBytes.Value <= thresholdBytes))
        {
            return "Critical";
        }

        if (diskSnapshot.FreeBytes.HasValue && diskSnapshot.FreeBytes.Value <= warningThresholdBytes)
        {
            return "Warning";
        }

        if (!isEnabled && !string.IsNullOrWhiteSpace(autoDisabledReason))
        {
            return autoDisabledReason switch
            {
                UltraDebugLogDefaults.ManualDisableReason => "Disabled",
                UltraDebugLogDefaults.DurationExpiredReason => "Disabled",
                _ => "Degraded"
            };
        }

        return "Healthy";
    }

    private static string? ResolveHealthEscalationReason(string? autoDisabledReason, string? diskFailureReason)
    {
        return !string.IsNullOrWhiteSpace(autoDisabledReason)
            ? autoDisabledReason
            : string.IsNullOrWhiteSpace(diskFailureReason)
                ? null
                : diskFailureReason;
    }

    private void RecordRetentionHeartbeat(UltraDebugLogRetentionRunSnapshot snapshot)
    {
        lock (retentionHeartbeatLock)
        {
            retentionHeartbeatState = new RetentionHeartbeatState(
                snapshot.CompletedAtUtc,
                snapshot.ReasonCode,
                !string.Equals(snapshot.ReasonCode, "RuntimeFailure", StringComparison.Ordinal));
        }
    }

    private RetentionHeartbeatState GetRetentionHeartbeatState()
    {
        lock (retentionHeartbeatLock)
        {
            return retentionHeartbeatState;
        }
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
        var logRootPath = EnsureAllowedLogRootPath();
        var directoryPath = EnsureAllowedBucketRootPath(logRootPath, bucketName);
        if (!Directory.Exists(directoryPath))
        {
            return 0L;
        }

        return EnumerateSafeBucketFiles(directoryPath, int.MaxValue, out _, out _, CancellationToken.None)
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

    private SearchCandidateFileCollection ResolveExportCandidateFiles(
        string bucketName,
        string? category,
        DateTime fromUtc,
        DateTime toUtc)
    {
        var bucketNames = string.Equals(bucketName, "all", StringComparison.OrdinalIgnoreCase)
            ? new[] { "normal", "ultra_debug" }
            : new[] { bucketName };
        var collectedFiles = new List<SearchCandidateFile>(UltraDebugLogDefaults.ExportMaxFiles);
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
                .Where(file => IsCandidateFileWithinExportRange(file, fromUtc, toUtc))
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
                .Take(UltraDebugLogDefaults.ExportMaxFiles)
                .ToArray());
    }

    private static bool IsCandidateFileWithinExportRange(FileInfo fileInfo, DateTime fromUtc, DateTime toUtc)
    {
        var lowerBound = fromUtc.AddDays(-1);
        var upperBound = toUtc.AddDays(1);
        var lastWriteUtc = fileInfo.LastWriteTimeUtc;

        if (lastWriteUtc >= lowerBound && lastWriteUtc <= upperBound)
        {
            return true;
        }

        if (!TryResolveCandidateFileDateUtc(fileInfo.Name, out var fileDateUtc))
        {
            return false;
        }

        return fileDateUtc >= lowerBound.Date && fileDateUtc <= upperBound.Date;
    }

    private static bool TryResolveCandidateFileDateUtc(string fileName, out DateTime fileDateUtc)
    {
        fileDateUtc = default;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var segments = Path.GetFileNameWithoutExtension(fileName)
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            if (segment.Length != 8 || !segment.All(char.IsDigit))
            {
                continue;
            }

            if (DateTime.TryParseExact(
                    segment,
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsedDateUtc))
            {
                fileDateUtc = DateTime.SpecifyKind(parsedDateUtc.Date, DateTimeKind.Utc);
                return true;
            }
        }

        return false;
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

    private static string NormalizeExportBucketName(string? bucketName)
    {
        RejectPathTraversal(bucketName, nameof(bucketName));

        return bucketName?.Trim().ToLowerInvariant() switch
        {
            "normal" => "normal",
            "ultra_debug" => "ultra_debug",
            "all" => "all",
            _ => throw new UltraDebugLogOperationException(
                "UltraLogExportBucketInvalid",
                "Masked log export failed because the selected bucket is invalid.")
        };
    }

    private static string? NormalizeExportCategory(string? category)
    {
        RejectPathTraversal(category, nameof(category));
        var normalizedCategory = NormalizeOptional(category, 32)?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedCategory))
        {
            return null;
        }

        return UltraDebugLogDefaults.CategoryFolders.Contains(normalizedCategory, StringComparer.OrdinalIgnoreCase)
            ? normalizedCategory
            : throw new UltraDebugLogOperationException(
                "UltraLogExportCategoryInvalid",
                "Masked log export failed because the selected category is invalid.");
    }

    private static string? NormalizeExportFilter(string? value, int maxLength, string parameterName)
    {
        RejectPathTraversal(value, parameterName);
        return NormalizeOptional(value, maxLength);
    }

    private ExportRange NormalizeExportRange(DateTime? fromUtc, DateTime? toUtc)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var normalizedToUtc = toUtc.HasValue && toUtc.Value <= nowUtc
            ? toUtc.Value
            : nowUtc;
        var normalizedFromUtc = fromUtc ?? normalizedToUtc.AddHours(-1);

        if (normalizedFromUtc > normalizedToUtc)
        {
            normalizedFromUtc = normalizedToUtc.AddHours(-1);
        }

        if (normalizedToUtc - normalizedFromUtc > UltraDebugLogDefaults.ExportMaxDateRange)
        {
            normalizedFromUtc = normalizedToUtc - UltraDebugLogDefaults.ExportMaxDateRange;
        }

        return new ExportRange(
            DateTime.SpecifyKind(normalizedFromUtc, DateTimeKind.Utc),
            DateTime.SpecifyKind(normalizedToUtc, DateTimeKind.Utc));
    }

    private static int NormalizeExportTake(int maxRows)
    {
        return Math.Clamp(
            maxRows <= 0 ? UltraDebugLogDefaults.ExportDefaultTake : maxRows,
            1,
            UltraDebugLogDefaults.ExportMaxTake);
    }

    private static bool MatchesExportRequest(
        UltraDebugLogTailLineSnapshot line,
        string? category,
        string? source,
        string? searchTerm,
        DateTime fromUtc,
        DateTime toUtc)
    {
        if (!line.OccurredAtUtc.HasValue ||
            line.OccurredAtUtc.Value < fromUtc ||
            line.OccurredAtUtc.Value > toUtc)
        {
            return false;
        }

        return MatchesSearchRequest(line, category, source, searchTerm, fromUtc);
    }

    private static string MaskExportLine(string rawLine)
    {
        return SensitivePayloadMasker.Mask(rawLine, UltraDebugLogDefaults.ExportLineMaxLength)
            ?? """{"eventName":"masked_export_invalid_line","summary":"Masked export could not preserve the source line.","detailMasked":"{}"}""";
    }

    private static BoundedExportPayload BuildBoundedExportPayload(
        IReadOnlyCollection<string> orderedLines,
        ref bool truncated)
    {
        var lines = new List<string>(orderedLines.Count);
        var totalBytes = 0;

        foreach (var line in orderedLines)
        {
            var encodedLength = Encoding.UTF8.GetByteCount(line + "\n");
            if (totalBytes + encodedLength > UltraDebugLogDefaults.ExportMaxBytes)
            {
                truncated = true;
                break;
            }

            lines.Add(line);
            totalBytes += encodedLength;
        }

        if (lines.Count < orderedLines.Count)
        {
            truncated = true;
        }

        return new BoundedExportPayload(lines, totalBytes);
    }

    private static string BuildEmptyExportPayload(
        string bucketName,
        string? category,
        DateTime fromUtc,
        DateTime toUtc)
    {
        return SensitivePayloadMasker.Mask(
            JsonSerializer.Serialize(
                new
                {
                    occurredAtUtc = toUtc,
                    application = "coinbot-admin",
                    machineName = "masked",
                    category = "runtime",
                    eventName = "masked_export_empty",
                    summary = "No masked log lines matched the requested export window.",
                    correlationId = (string?)null,
                    symbol = (string?)null,
                    executionAttemptId = (string?)null,
                    strategySignalId = (string?)null,
                    detailMasked = JsonSerializer.Serialize(
                        new
                        {
                            bucket = bucketName,
                            category = category ?? "all",
                            fromUtc,
                            toUtc,
                            exportedLineCount = 0
                        },
                        SerializerOptions)
                },
                SerializerOptions),
            UltraDebugLogDefaults.ExportEmptyPayloadMaxLength)
            ?? """{"eventName":"masked_export_empty","summary":"No masked log lines matched the requested export window."}""";
    }

    private static string BuildExportFileName(
        string bucketName,
        string? category,
        DateTime fromUtc,
        DateTime toUtc)
    {
        static string SanitizeSegment(string value)
        {
            var sanitized = new string(
                value
                    .Trim()
                    .ToLowerInvariant()
                    .Select(character => char.IsLetterOrDigit(character) ? character : '-')
                    .ToArray())
                .Trim('-');
            return string.IsNullOrWhiteSpace(sanitized) ? "all" : sanitized;
        }

        return string.Join(
            '-',
            "coinbot",
            "logs",
            SanitizeSegment(bucketName),
            SanitizeSegment(category ?? "all"),
            fromUtc.ToString("yyyyMMddTHHmmssZ"),
            toUtc.ToString("yyyyMMddTHHmmssZ"));
    }

    private static byte[] BuildZipPayload(string entryFileName, string ndjsonPayload)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryFileName, CompressionLevel.Fastest);
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream, Encoding.UTF8);
            writer.Write(ndjsonPayload);
        }

        return stream.ToArray();
    }

    private static void RejectPathTraversal(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (value.Contains("..", StringComparison.Ordinal) ||
            value.Contains('/', StringComparison.Ordinal) ||
            value.Contains('\\', StringComparison.Ordinal) ||
            value.Contains(':', StringComparison.Ordinal))
        {
            throw new UltraDebugLogOperationException(
                "UltraLogExportPathInvalid",
                $"Masked log export failed because {parameterName} contains an invalid path fragment.");
        }
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

    private sealed record DiskSpaceSnapshot(
        long? FreeBytes,
        decimal? FreePercent,
        bool IsWritable,
        string? FailureReason);

    private sealed record RetentionHeartbeatState(
        DateTime? CompletedAtUtc,
        string? ReasonCode,
        bool? Succeeded)
    {
        public static RetentionHeartbeatState Empty { get; } = new(null, null, null);
    }

    private sealed record SearchCandidateFile(string BucketName, FileInfo File);

    private sealed record SearchCandidateFileCollection(
        int TotalFileCount,
        IReadOnlyCollection<SearchCandidateFile> Files);

    private sealed record ExportRange(DateTime FromUtc, DateTime ToUtc);

    private sealed record ExportCandidateLine(DateTime OccurredAtUtc, string MaskedLine);

    private sealed record BoundedExportPayload(
        IReadOnlyCollection<string> Lines,
        int TotalBytes);

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
