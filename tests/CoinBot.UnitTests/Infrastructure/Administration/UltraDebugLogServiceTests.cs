using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace CoinBot.UnitTests.Infrastructure.Administration;

public sealed class UltraDebugLogServiceTests
{
    [Fact]
    public async Task EnableAsync_PersistsTimedState_AndWritesMaskedDualBucketEntries()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero));

        var snapshot = await harness.Service.EnableAsync(
            new UltraDebugLogEnableRequest(
                "1h",
                "admin-01",
                "ops-admin@coinbot.test",
                "ip:masked",
                "ua:masked",
                "corr-ultra-1",
                256,
                1024));

        await harness.Service.WriteAsync(
            new UltraDebugLogEntry(
                Category: "execution.dispatch",
                EventName: "execution_dispatch_submitted",
                Summary: "Dispatch accepted.",
                CorrelationId: "corr-ultra-1",
                Symbol: "SOLUSDT",
                Detail: new
                {
                    apiKey = "plain-key",
                    cookie = "session-cookie",
                    refreshToken = "refresh-token",
                    csrfToken = "csrf-token",
                    connectionString = "Server=localhost;User Id=local-user;Password=plain-db-password;TrustServerCertificate=true;"
                }));

        await using var scope = harness.CreateScope();
        var entity = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .UltraDebugLogStates
            .SingleAsync();
        var normalLog = await File.ReadAllTextAsync(harness.ResolveSingleFile("normal", "execution.dispatch"));
        var ultraLog = await File.ReadAllTextAsync(harness.ResolveSingleFile("ultra_debug", "execution.dispatch"));

        Assert.True(snapshot.IsEnabled);
        Assert.True(entity.IsEnabled);
        Assert.Equal("1h", entity.DurationKey);
        Assert.Equal(256, entity.NormalLogsLimitMb);
        Assert.Equal(1024, entity.UltraLogsLimitMb);
        Assert.Equal("ops-admin@coinbot.test", entity.EnabledByAdminEmail);
        Assert.Equal(256, snapshot.NormalLogsLimitMb);
        Assert.Equal(1024, snapshot.UltraLogsLimitMb);
        Assert.Contains("execution_dispatch_submitted", normalLog, StringComparison.Ordinal);
        Assert.Contains("execution_dispatch_submitted", ultraLog, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-key", ultraLog, StringComparison.Ordinal);
        Assert.DoesNotContain("session-cookie", ultraLog, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-token", ultraLog, StringComparison.Ordinal);
        Assert.DoesNotContain("csrf-token", ultraLog, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-db-password", ultraLog, StringComparison.Ordinal);
        Assert.DoesNotContain("local-user", ultraLog, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", ultraLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisableAsync_StopsUltraBucketWrites_WhileNormalBucketContinues()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero));

        await harness.Service.EnableAsync(
            new UltraDebugLogEnableRequest("1h", "admin-01", "ops-admin@coinbot.test", null, null, "corr-ultra-2", 128, 512));
        await harness.Service.WriteAsync(new UltraDebugLogEntry("scanner", "scanner_cycle_completed", "First", "corr-ultra-2", "SOLUSDT"));

        await harness.Service.DisableAsync(
            new UltraDebugLogDisableRequest("admin-01", "ops-admin@coinbot.test", null, null, "corr-ultra-3", "manual_disable"));
        await harness.Service.WriteAsync(new UltraDebugLogEntry("scanner", "scanner_cycle_completed", "Second", "corr-ultra-4", "SOLUSDT"));

        var normalLines = await File.ReadAllLinesAsync(harness.ResolveSingleFile("normal", "scanner"));
        var ultraLines = await File.ReadAllLinesAsync(harness.ResolveSingleFile("ultra_debug", "scanner"));

        Assert.Equal(2, normalLines.Length);
        Assert.Single(ultraLines);
    }

    [Fact]
    public async Task GetSnapshotAsync_AutoDisablesExpiredState_AndWritesAudit()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero));

        await harness.Service.EnableAsync(
            new UltraDebugLogEnableRequest("1h", "admin-01", "ops-admin@coinbot.test", null, null, "corr-ultra-5", 128, 512));

        harness.TimeProvider.SetUtcNow(new DateTimeOffset(2026, 4, 22, 10, 5, 0, TimeSpan.Zero));
        var snapshot = await harness.Service.GetSnapshotAsync();
        var scope = harness.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var entity = await dbContext.UltraDebugLogStates.SingleAsync();
        var audit = await dbContext.AdminAuditLogs
            .OrderByDescending(item => item.CreatedAtUtc)
            .FirstAsync();

        Assert.False(snapshot.IsEnabled);
        Assert.Equal("duration_expired", snapshot.AutoDisabledReason);
        Assert.Equal(128, snapshot.NormalLogsLimitMb);
        Assert.Equal(512, snapshot.UltraLogsLimitMb);
        Assert.False(entity.IsEnabled);
        Assert.Equal("duration_expired", entity.AutoDisabledReason);
        Assert.Equal("ultra_log_auto_disabled_duration_expired", audit.ActionType);
        await scope.DisposeAsync();
    }

    [Fact]
    public async Task GetSnapshotAsync_AllowsLegacyState_WhenLimitsAreStillNull()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero));

        await harness.Service.EnableAsync(
            new UltraDebugLogEnableRequest("1h", "admin-01", "ops-admin@coinbot.test", null, null, "corr-ultra-6", 256, 1024));

        await using (var scope = harness.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var entity = await dbContext.UltraDebugLogStates.SingleAsync();
            entity.NormalLogsLimitMb = null;
            entity.UltraLogsLimitMb = null;
            await dbContext.SaveChangesAsync();
        }

        var snapshot = await harness.Service.GetSnapshotAsync();

        Assert.True(snapshot.IsEnabled);
        Assert.Equal("1h", snapshot.DurationKey);
        Assert.Null(snapshot.NormalLogsLimitMb);
        Assert.Null(snapshot.UltraLogsLimitMb);
    }

    [Fact]
    public async Task WriteAsync_DeletesOldestInactiveNormalBucketFiles_WhenLimitExceeded()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero));

        await harness.Service.EnableAsync(
            new UltraDebugLogEnableRequest("1h", "admin-01", "ops-admin@coinbot.test", null, null, "corr-ultra-7", 128, 512));
        await harness.Service.DisableAsync(
            new UltraDebugLogDisableRequest("admin-01", "ops-admin@coinbot.test", null, null, "corr-ultra-8", "manual_disable"));

        await using (var scope = harness.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var entity = await dbContext.UltraDebugLogStates.SingleAsync();
            entity.NormalLogsLimitMb = 1;
            await dbContext.SaveChangesAsync();
        }

        var oldestInactiveFilePath = harness.ResolveBucketFilePath("normal", new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc), "runtime");
        var newerInactiveFilePath = harness.ResolveBucketFilePath("normal", new DateTime(2026, 4, 21, 0, 0, 0, DateTimeKind.Utc), "runtime");
        var activeFilePath = harness.ResolveBucketFilePath("normal", harness.TimeProvider.GetUtcNow().UtcDateTime, "runtime");

        await File.WriteAllTextAsync(oldestInactiveFilePath, new string('a', 700 * 1024));
        await File.WriteAllTextAsync(newerInactiveFilePath, new string('b', 400 * 1024));
        File.SetLastWriteTimeUtc(oldestInactiveFilePath, new DateTime(2026, 4, 20, 9, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newerInactiveFilePath, new DateTime(2026, 4, 21, 9, 0, 0, DateTimeKind.Utc));

        await harness.Service.WriteAsync(new UltraDebugLogEntry("runtime", "normal_bucket_cleanup", "cleanup", "corr-ultra-9", "SOLUSDT"));

        var snapshot = await harness.Service.GetSnapshotAsync();

        Assert.False(snapshot.IsEnabled);
        Assert.False(File.Exists(oldestInactiveFilePath));
        Assert.True(File.Exists(newerInactiveFilePath));
        Assert.True(File.Exists(activeFilePath));
    }

    [Fact]
    public async Task WriteAsync_AutoDisablesUltraBucket_WhenActiveFileStillExceedsLimitAfterCleanup()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero));

        await harness.Service.EnableAsync(
            new UltraDebugLogEnableRequest("1h", "admin-01", "ops-admin@coinbot.test", null, null, "corr-ultra-10", 128, 512));

        await using (var scope = harness.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var entity = await dbContext.UltraDebugLogStates.SingleAsync();
            entity.UltraLogsLimitMb = 1;
            await dbContext.SaveChangesAsync();
        }

        var inactiveUltraFilePath = harness.ResolveBucketFilePath("ultra_debug", new DateTime(2026, 4, 21, 0, 0, 0, DateTimeKind.Utc), "runtime");
        var activeUltraFilePath = harness.ResolveBucketFilePath("ultra_debug", harness.TimeProvider.GetUtcNow().UtcDateTime, "runtime");

        await File.WriteAllTextAsync(inactiveUltraFilePath, new string('x', 300 * 1024));
        await File.WriteAllTextAsync(activeUltraFilePath, new string('y', 1200 * 1024));
        File.SetLastWriteTimeUtc(inactiveUltraFilePath, new DateTime(2026, 4, 21, 9, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(activeUltraFilePath, new DateTime(2026, 4, 22, 8, 0, 0, DateTimeKind.Utc));

        await harness.Service.WriteAsync(new UltraDebugLogEntry("runtime", "ultra_bucket_cleanup", "cleanup", "corr-ultra-11", "SOLUSDT"));

        var snapshot = await harness.Service.GetSnapshotAsync();
        await using var verificationScope = harness.CreateScope();
        var verificationDbContext = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persistedState = await verificationDbContext.UltraDebugLogStates.SingleAsync();
        var auditActionTypes = await verificationDbContext.AdminAuditLogs
            .OrderBy(item => item.CreatedAtUtc)
            .Select(item => item.ActionType)
            .ToListAsync();

        Assert.False(snapshot.IsEnabled);
        Assert.Equal("ultra_size_limit_exceeded", snapshot.AutoDisabledReason);
        Assert.False(persistedState.IsEnabled);
        Assert.Equal("ultra_size_limit_exceeded", persistedState.AutoDisabledReason);
        Assert.Contains("ultra_log_auto_disabled_size_limit", auditActionTypes);
        Assert.False(File.Exists(inactiveUltraFilePath));
        Assert.True(File.Exists(activeUltraFilePath));
    }

    [Fact]
    public async Task WriteAsync_RotatesNormalBucket_WhenActiveFileExceedsThreshold_AndSubsequentWritesUseNewActiveFile()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero));

        var activeFilePath = harness.ResolveBucketFilePath("normal", harness.TimeProvider.GetUtcNow().UtcDateTime, "runtime");
        await harness.CreateSizedFileAsync(activeFilePath, (32L * 1024L * 1024L) + 1024L);

        await harness.Service.WriteAsync(new UltraDebugLogEntry("runtime", "normal_rotate_first", "first", "corr-ultra-12", "SOLUSDT"));

        var bucketFiles = Directory.GetFiles(harness.ResolveBucketDirectory("normal", "runtime"), "*.ndjson");
        Assert.Equal(2, bucketFiles.Length);
        var rotatedFilePath = Assert.Single(
            bucketFiles,
            filePath => !string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(activeFilePath), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            $"coinbot-testhost-{harness.TimeProvider.GetUtcNow().UtcDateTime:yyyyMMdd}-001.ndjson",
            Path.GetFileName(rotatedFilePath));
        Assert.True(File.Exists(activeFilePath));
        Assert.True(new FileInfo(activeFilePath).Length == 0L);

        await harness.Service.WriteAsync(new UltraDebugLogEntry("runtime", "normal_rotate_second", "second", "corr-ultra-13", "SOLUSDT"));

        var activeContents = await File.ReadAllTextAsync(activeFilePath);
        var rotatedContents = await File.ReadAllTextAsync(rotatedFilePath);

        Assert.Contains("normal_rotate_first", rotatedContents, StringComparison.Ordinal);
        Assert.Contains("normal_rotate_second", activeContents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSnapshotAsync_ReturnsBucketUsageTelemetry()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero));

        var normalFilePath = harness.ResolveBucketFilePath("normal", harness.TimeProvider.GetUtcNow().UtcDateTime, "scanner");
        var ultraFilePath = harness.ResolveBucketFilePath("ultra_debug", harness.TimeProvider.GetUtcNow().UtcDateTime, "execution");
        await harness.CreateSizedFileAsync(normalFilePath, 2L * 1024L * 1024L);
        await harness.CreateSizedFileAsync(ultraFilePath, 3L * 1024L * 1024L);

        var snapshot = await harness.Service.GetSnapshotAsync();

        Assert.Equal(2L * 1024L * 1024L, snapshot.NormalLogsUsageBytes);
        Assert.Equal(3L * 1024L * 1024L, snapshot.UltraLogsUsageBytes);
    }

    [Fact]
    public async Task WriteAsync_AutoDisablesUltraBucket_WhenDiskPressureIsDetected_AndContinuesNormalCuratedLogging()
    {
        const long diskFreeSpaceBytes = 400L * 1024L * 1024L;
        await using var harness = CreateHarness(
            new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero),
            _ => diskFreeSpaceBytes);

        await harness.Service.EnableAsync(
            new UltraDebugLogEnableRequest("1h", "admin-01", "ops-admin@coinbot.test", null, null, "corr-ultra-14a", 256, 1024));

        await harness.Service.WriteAsync(
            new UltraDebugLogEntry(
                "execution",
                "execution_dispatch_submitted",
                "Dispatch accepted.",
                "corr-ultra-14a",
                "SOLUSDT",
                ExecutionAttemptId: "attempt-14a",
                StrategySignalId: "signal-14a",
                Detail: new
                {
                    token = "secret-token"
                }));

        var snapshot = await harness.Service.GetSnapshotAsync();
        await using var verificationScope = harness.CreateScope();
        var verificationDbContext = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persistedState = await verificationDbContext.UltraDebugLogStates.SingleAsync();
        var auditActionTypes = await verificationDbContext.AdminAuditLogs
            .OrderBy(item => item.CreatedAtUtc)
            .Select(item => item.ActionType)
            .ToListAsync();
        var normalLogLine = Assert.Single(await File.ReadAllLinesAsync(harness.ResolveSingleFile("normal", "execution")));
        var normalDetailJson = ExtractDetailMaskedJson(normalLogLine);
        var ultraFilePath = harness.ResolveBucketFilePath("ultra_debug", harness.TimeProvider.GetUtcNow().UtcDateTime, "execution");

        Assert.False(snapshot.IsEnabled);
        Assert.Equal("disk_pressure", snapshot.AutoDisabledReason);
        Assert.True(snapshot.IsNormalFallbackMode);
        Assert.Equal(diskFreeSpaceBytes, snapshot.DiskFreeSpaceBytes);
        Assert.False(persistedState.IsEnabled);
        Assert.Equal("disk_pressure", persistedState.AutoDisabledReason);
        Assert.Contains("ultra_log_auto_disabled_disk_pressure", auditActionTypes);
        Assert.Contains("\"fallbackMode\":true", normalDetailJson, StringComparison.Ordinal);
        Assert.Contains("\"fallbackReasonCode\":\"disk_pressure\"", normalDetailJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", normalDetailJson, StringComparison.Ordinal);
        Assert.False(File.Exists(ultraFilePath));
    }

    [Fact]
    public async Task WriteAsync_AutoDisablesUltraBucket_WhenUltraWritePathFails_AndNormalCuratedLoggingContinues()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero));

        await harness.Service.EnableAsync(
            new UltraDebugLogEnableRequest("1h", "admin-01", "ops-admin@coinbot.test", null, null, "corr-ultra-14b", 256, 1024));

        var blockedCategoryPath = Path.Combine(harness.ResolveBucketDirectory("ultra_debug"), "scanner");
        await File.WriteAllTextAsync(blockedCategoryPath, "blocked");

        await harness.Service.WriteAsync(
            new UltraDebugLogEntry(
                "scanner",
                "scanner_cycle_completed",
                "Scanner cycle completed.",
                "corr-ultra-14b",
                "SOLUSDT",
                Detail: new
                {
                    token = "plain-token"
                }));

        await harness.Service.WriteAsync(
            new UltraDebugLogEntry(
                "scanner",
                "scanner_cycle_completed_fallback",
                "Scanner cycle fallback.",
                "corr-ultra-14c",
                "SOLUSDT"));

        var snapshot = await harness.Service.GetSnapshotAsync();
        await using var verificationScope = harness.CreateScope();
        var verificationDbContext = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persistedState = await verificationDbContext.UltraDebugLogStates.SingleAsync();
        var auditActionTypes = await verificationDbContext.AdminAuditLogs
            .OrderBy(item => item.CreatedAtUtc)
            .Select(item => item.ActionType)
            .ToListAsync();
        var normalLogLines = await File.ReadAllLinesAsync(harness.ResolveSingleFile("normal", "scanner"));
        var fallbackDetailJson = ExtractDetailMaskedJson(normalLogLines[^1]);

        Assert.False(snapshot.IsEnabled);
        Assert.Equal("ultra_runtime_write_failure", snapshot.AutoDisabledReason);
        Assert.True(snapshot.IsNormalFallbackMode);
        Assert.False(persistedState.IsEnabled);
        Assert.Equal("ultra_runtime_write_failure", persistedState.AutoDisabledReason);
        Assert.Contains("ultra_log_auto_disabled_runtime_write_failure", auditActionTypes);
        Assert.Equal(2, normalLogLines.Length);
        Assert.Contains("scanner_cycle_completed", normalLogLines[0], StringComparison.Ordinal);
        Assert.Contains("scanner_cycle_completed_fallback", normalLogLines[1], StringComparison.Ordinal);
        Assert.Contains("\"fallbackMode\":true", fallbackDetailJson, StringComparison.Ordinal);
        Assert.Contains("\"fallbackReasonCode\":\"ultra_runtime_write_failure\"", fallbackDetailJson, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-token", string.Join(Environment.NewLine, normalLogLines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_WritesCategorySubfolder_AndSnapshotIncludesLatestStructuredEvent()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero));

        await harness.Service.EnableAsync(
            new UltraDebugLogEnableRequest("1h", "admin-01", "ops-admin@coinbot.test", null, null, "corr-ultra-14", 256, 1024));
        await harness.Service.WriteAsync(
            new UltraDebugLogEntry(
                "scanner.handoff",
                "scanner_handoff_blocked",
                "Handoff blocked for SOLUSDT.",
                "corr-ultra-14",
                "SOLUSDT",
                ExecutionAttemptId: "attempt-001",
                Detail: new
                {
                    sourceLayer = "MarketScannerHandoffService",
                    timeframe = "1m",
                    decisionReasonCode = "NoEligibleCandidate",
                    blockerCode = "NoEligibleCandidate",
                    latencyBreakdown = new
                    {
                        totalMs = 42,
                        handoffMs = 42
                    }
                }));

        var snapshot = await harness.Service.GetSnapshotAsync();
        var handoffFiles = Directory.GetFiles(harness.ResolveBucketDirectory("ultra_debug", "handoff"), "*.ndjson");

        Assert.Single(handoffFiles);
        Assert.NotNull(snapshot.LatestStructuredEvent);
        Assert.Equal("handoff", snapshot.LatestStructuredEvent!.Category);
        Assert.Equal("scanner_handoff_blocked", snapshot.LatestStructuredEvent.EventName);
        Assert.Equal("SOLUSDT", snapshot.LatestStructuredEvent.Symbol);
        Assert.Equal("1m", snapshot.LatestStructuredEvent.Timeframe);
        Assert.Equal("MarketScannerHandoffService", snapshot.LatestStructuredEvent.SourceLayer);
        Assert.Equal("NoEligibleCandidate", snapshot.LatestStructuredEvent.DecisionReasonCode);
        Assert.Equal("NoEligibleCandidate", snapshot.LatestStructuredEvent.BlockerCode);
        Assert.Equal("total=42ms | handoff=42ms", snapshot.LatestStructuredEvent.LatencyBreakdownLabel);
    }

    [Fact]
    public async Task GetSnapshotAsync_ReturnsMaskedBoundedTailPreview_ForNormalAndUltraBuckets()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero));

        await harness.Service.EnableAsync(
            new UltraDebugLogEnableRequest("1h", "admin-01", "ops-admin@coinbot.test", null, null, "corr-ultra-15", 256, 1024));

        for (var index = 0; index < 20; index++)
        {
            await harness.Service.WriteAsync(
                new UltraDebugLogEntry(
                    Category: "execution.dispatch",
                    EventName: $"tail_event_{index:00}",
                    Summary: $"Tail summary {index:00}",
                    CorrelationId: $"corr-tail-{index:00}",
                    Symbol: "SOLUSDT",
                    Detail: new
                    {
                        token = "plain-token",
                        password = "plain-password",
                        payload = new string('x', 12000)
                    }));
        }

        var snapshot = await harness.Service.GetSnapshotAsync();

        Assert.NotNull(snapshot.NormalLogsTail);
        Assert.NotNull(snapshot.UltraLogsTail);
        Assert.Equal(8, snapshot.NormalLogsTail!.ReturnedLineCount);
        Assert.Equal(8, snapshot.UltraLogsTail!.ReturnedLineCount);
        Assert.Equal(8, snapshot.NormalLogsTail.Lines.Count);
        Assert.Equal(8, snapshot.UltraLogsTail.Lines.Count);
        Assert.True(snapshot.NormalLogsTail.IsTruncated);
        Assert.True(snapshot.UltraLogsTail.IsTruncated);
        Assert.True(snapshot.NormalLogsTail.FilesScanned <= 2);
        Assert.True(snapshot.UltraLogsTail.FilesScanned <= 2);
        Assert.Contains(snapshot.NormalLogsTail.Lines, line => string.Equals(line.Symbol, "SOLUSDT", StringComparison.Ordinal));
        Assert.All(snapshot.NormalLogsTail.Lines, line => Assert.Equal("execution", line.Category));
        Assert.All(snapshot.UltraLogsTail.Lines, line => Assert.Equal("execution", line.Category));

        foreach (var line in snapshot.NormalLogsTail.Lines.Concat(snapshot.UltraLogsTail.Lines))
        {
            Assert.False(string.IsNullOrWhiteSpace(line.EventName));
            Assert.False(string.IsNullOrWhiteSpace(line.Summary));
            Assert.Contains(".ndjson", line.SourceFileName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("plain-token", line.DetailPreview ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("plain-password", line.DetailPreview ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("***REDACTED***", line.DetailPreview ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SearchAsync_AppliesBucketCategorySourceSearchAndTimeWindow_UsingMaskedPreview()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 22, 8, 0, 0, TimeSpan.Zero));

        await harness.Service.EnableAsync(
            new UltraDebugLogEnableRequest("1h", "admin-01", "ops-admin@coinbot.test", null, null, "corr-ultra-16", 256, 1024));

        await harness.Service.WriteAsync(
            new UltraDebugLogEntry(
                "scanner.handoff",
                "scanner_handoff_blocked",
                "Handoff blocked for SOLUSDT.",
                "corr-search-old",
                "SOLUSDT",
                Detail: new
                {
                    sourceLayer = "MarketScannerHandoffService",
                    blockerCode = "NoEligibleCandidate"
                }));

        harness.TimeProvider.SetUtcNow(new DateTimeOffset(2026, 4, 22, 8, 45, 0, TimeSpan.Zero));
        await harness.Service.WriteAsync(
            new UltraDebugLogEntry(
                "execution.dispatch",
                "execution_dispatch_submitted",
                "Dispatch accepted for SOLUSDT.",
                "corr-search-new",
                "SOLUSDT",
                Detail: new
                {
                    sourceLayer = "ExecutionEngine",
                    token = "plain-token",
                    note = "dispatch"
                }));

        var filteredResult = await harness.Service.SearchAsync(
            new UltraDebugLogSearchRequest(
                BucketName: "ultra_debug",
                Category: "execution",
                Source: "ExecutionEngine",
                SearchTerm: "dispatch",
                FromUtc: new DateTime(2026, 4, 22, 8, 30, 0, DateTimeKind.Utc),
                Take: 25));

        Assert.Single(filteredResult.Lines);
        var line = filteredResult.Lines.Single();
        Assert.Equal("execution", line.Category);
        Assert.Equal("ExecutionEngine", line.Source);
        Assert.Equal("ultra_debug", line.BucketLabel);
        Assert.Equal("corr-search-new", line.CorrelationId);
        Assert.DoesNotContain("plain-token", line.DetailPreview ?? string.Empty, StringComparison.Ordinal);

        var secretSearchResult = await harness.Service.SearchAsync(
            new UltraDebugLogSearchRequest(
                BucketName: "ultra_debug",
                Category: "execution",
                Source: "ExecutionEngine",
                SearchTerm: "plain-token",
                FromUtc: new DateTime(2026, 4, 22, 8, 30, 0, DateTimeKind.Utc),
                Take: 25));

        Assert.Empty(secretSearchResult.Lines);
    }

    private static TestHarness CreateHarness(DateTimeOffset now, Func<string, long?>? availableDiskBytesResolver = null)
    {
        var artifactsRoot = Path.Combine(
            AppContext.BaseDirectory,
            "UltraDebugArtifacts",
            Guid.NewGuid().ToString("N"));
        var databaseName = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(artifactsRoot);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICorrelationContextAccessor, CorrelationContextAccessor>();
        services.AddSingleton<IDataScopeContext>(new TestDataScopeContext());
        services.AddSingleton<MutableTimeProvider>(new MutableTimeProvider(now));
        services.AddSingleton<TimeProvider>(serviceProvider => serviceProvider.GetRequiredService<MutableTimeProvider>());
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(artifactsRoot));
        services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddScoped<IAdminAuditLogService, AdminAuditLogService>();
        services.AddSingleton<IUltraDebugLogService>(serviceProvider =>
            new UltraDebugLogService(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<UltraDebugLogService>>(),
                serviceProvider.GetRequiredService<IHostEnvironment>(),
                availableDiskBytesResolver));

        var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IUltraDebugLogService>();
        return new TestHarness(provider, artifactsRoot, provider.GetRequiredService<MutableTimeProvider>(), service);
    }

    private static string ExtractDetailMaskedJson(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("detailMasked").GetString() ?? string.Empty;
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void SetUtcNow(DateTimeOffset value)
        {
            current = value;
        }
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "CoinBot.TestHost";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestHarness(
        ServiceProvider provider,
        string artifactsRoot,
        MutableTimeProvider timeProvider,
        IUltraDebugLogService service) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;

        public string ArtifactsRoot { get; } = artifactsRoot;

        public MutableTimeProvider TimeProvider { get; } = timeProvider;

        public IUltraDebugLogService Service { get; } = service;

        public AsyncServiceScope CreateScope() => Provider.CreateAsyncScope();

        public string ResolveBucketDirectory(string bucketName, string? category = null)
        {
            var path = string.IsNullOrWhiteSpace(category)
                ? Path.Combine(ArtifactsRoot, "Logs", bucketName)
                : Path.Combine(ArtifactsRoot, "Logs", bucketName, ResolveCategoryFolder(category));
            Directory.CreateDirectory(path);
            return path;
        }

        public string ResolveBucketFilePath(string bucketName, DateTime utcDate, string? category = null)
        {
            return Path.Combine(
                ResolveBucketDirectory(bucketName, category),
                $"coinbot-testhost-{utcDate:yyyyMMdd}.ndjson");
        }

        public string ResolveRotatedBucketFilePath(string bucketName, DateTime utcDate, int sequence, string? category = null)
        {
            return Path.Combine(
                ResolveBucketDirectory(bucketName, category),
                $"coinbot-testhost-{utcDate:yyyyMMdd}-{sequence:000}.ndjson");
        }

        public async Task CreateSizedFileAsync(string filePath, long sizeBytes)
        {
            var directoryPath = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            await using var stream = new FileStream(
                filePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true);
            stream.SetLength(sizeBytes);
            await stream.FlushAsync();
        }

        public string ResolveSingleFile(string bucketName, string? category = null)
        {
            var files = Directory.GetFiles(
                string.IsNullOrWhiteSpace(category)
                    ? Path.Combine(ArtifactsRoot, "Logs", bucketName)
                    : ResolveBucketDirectory(bucketName, category),
                "*.ndjson",
                SearchOption.AllDirectories);
            return Assert.Single(files);
        }

        private static string ResolveCategoryFolder(string category)
        {
            if (category.StartsWith("scanner.handoff", StringComparison.OrdinalIgnoreCase))
            {
                return "handoff";
            }

            if (category.StartsWith("scanner", StringComparison.OrdinalIgnoreCase))
            {
                return "scanner";
            }

            if (category.StartsWith("strategy", StringComparison.OrdinalIgnoreCase))
            {
                return "strategy";
            }

            if (category.StartsWith("execution", StringComparison.OrdinalIgnoreCase))
            {
                return "execution";
            }

            if (category.StartsWith("exchange", StringComparison.OrdinalIgnoreCase))
            {
                return "exchange";
            }

            return "runtime";
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            if (Directory.Exists(ArtifactsRoot))
            {
                Directory.Delete(ArtifactsRoot, recursive: true);
            }
        }
    }
}
