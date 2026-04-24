using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Text.Json;

namespace CoinBot.IntegrationTests.Administration;

public sealed class UltraDebugLogIntegrationTests
{
    [Fact]
    public async Task UltraDebugLog_RestoresEnabledState_AcrossRestartLikeProviderRebuild()
    {
        var databaseName = $"CoinBotUltraDebugRestore_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var artifactsRoot = Path.Combine(AppContext.BaseDirectory, "UltraDebugIntegrationArtifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactsRoot);

        try
        {
            await using (var provider = BuildProvider(connectionString, artifactsRoot, new MutableTimeProvider(new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero))))
            {
                await using var scope = provider.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await dbContext.Database.EnsureDeletedAsync();
                await dbContext.Database.EnsureCreatedAsync();

                var service = provider.GetRequiredService<IUltraDebugLogService>();
                await service.EnableAsync(
                    new UltraDebugLogEnableRequest("3h", "admin-01", "ops-admin@coinbot.test", null, null, "corr-int-ultra-1", 256, 1024));
            }

            await using (var provider = BuildProvider(connectionString, artifactsRoot, new MutableTimeProvider(new DateTimeOffset(2026, 4, 22, 10, 0, 0, TimeSpan.Zero))))
            {
                var service = provider.GetRequiredService<IUltraDebugLogService>();
                var snapshot = await service.GetSnapshotAsync();

                Assert.True(snapshot.IsEnabled);
                Assert.Equal("3h", snapshot.DurationKey);
                Assert.Equal("ops-admin@coinbot.test", snapshot.EnabledByAdminEmail);
                Assert.Equal(256, snapshot.NormalLogsLimitMb);
                Assert.Equal(1024, snapshot.UltraLogsLimitMb);
            }
        }
        finally
        {
            if (Directory.Exists(artifactsRoot))
            {
                Directory.Delete(artifactsRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UltraDebugLog_ExpiredState_AutoDisables_OnRestartLikeProviderRebuild()
    {
        var databaseName = $"CoinBotUltraDebugExpiry_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var artifactsRoot = Path.Combine(AppContext.BaseDirectory, "UltraDebugIntegrationArtifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactsRoot);

        try
        {
            await using (var provider = BuildProvider(connectionString, artifactsRoot, new MutableTimeProvider(new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero))))
            {
                await using var scope = provider.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await dbContext.Database.EnsureDeletedAsync();
                await dbContext.Database.EnsureCreatedAsync();

                var service = provider.GetRequiredService<IUltraDebugLogService>();
                await service.EnableAsync(
                    new UltraDebugLogEnableRequest("1h", "admin-01", "ops-admin@coinbot.test", null, null, "corr-int-ultra-2", 128, 512));
            }

            await using (var provider = BuildProvider(connectionString, artifactsRoot, new MutableTimeProvider(new DateTimeOffset(2026, 4, 22, 10, 30, 0, TimeSpan.Zero))))
            {
                await using var scope = provider.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var service = provider.GetRequiredService<IUltraDebugLogService>();
                var snapshot = await service.GetSnapshotAsync();
                var auditLog = await dbContext.AdminAuditLogs
                    .OrderByDescending(item => item.CreatedAtUtc)
                    .FirstAsync();

                Assert.False(snapshot.IsEnabled);
                Assert.Equal("duration_expired", snapshot.AutoDisabledReason);
                Assert.Equal(128, snapshot.NormalLogsLimitMb);
                Assert.Equal(512, snapshot.UltraLogsLimitMb);
                Assert.Equal("ultra_log_auto_disabled_duration_expired", auditLog.ActionType);
            }
        }
        finally
        {
            if (Directory.Exists(artifactsRoot))
            {
                Directory.Delete(artifactsRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UltraDebugLog_SizeLimitExceeded_AutoDisables_AndPersistsReason()
    {
        var databaseName = $"CoinBotUltraDebugSize_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var artifactsRoot = Path.Combine(AppContext.BaseDirectory, "UltraDebugIntegrationArtifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactsRoot);

        try
        {
            await using var provider = BuildProvider(
                connectionString,
                artifactsRoot,
                new MutableTimeProvider(new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero)));
            await using (var scope = provider.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await dbContext.Database.EnsureDeletedAsync();
                await dbContext.Database.EnsureCreatedAsync();
            }

            var service = provider.GetRequiredService<IUltraDebugLogService>();
            await service.EnableAsync(
                new UltraDebugLogEnableRequest("1h", "admin-01", "ops-admin@coinbot.test", null, null, "corr-int-ultra-3", 128, 512));

            await using (var scope = provider.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var entity = await dbContext.UltraDebugLogStates.SingleAsync();
                entity.UltraLogsLimitMb = 1;
                await dbContext.SaveChangesAsync();
            }

            var inactiveUltraFilePath = ResolveBucketFilePath(artifactsRoot, "ultra_debug", "coinbot-integrationtests", new DateTime(2026, 4, 21, 0, 0, 0, DateTimeKind.Utc), "runtime");
            var activeUltraFilePath = ResolveBucketFilePath(artifactsRoot, "ultra_debug", "coinbot-integrationtests", new DateTime(2026, 4, 22, 0, 0, 0, DateTimeKind.Utc), "runtime");

            await File.WriteAllTextAsync(inactiveUltraFilePath, new string('x', 300 * 1024));
            await File.WriteAllTextAsync(activeUltraFilePath, new string('y', 1200 * 1024));
            File.SetLastWriteTimeUtc(inactiveUltraFilePath, new DateTime(2026, 4, 21, 9, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(activeUltraFilePath, new DateTime(2026, 4, 22, 8, 0, 0, DateTimeKind.Utc));

            await service.WriteAsync(new UltraDebugLogEntry("runtime", "ultra_bucket_cleanup", "cleanup", "corr-int-ultra-4", "SOLUSDT"));

            await using var verificationScope = provider.CreateAsyncScope();
            var verificationDbContext = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var snapshot = await service.GetSnapshotAsync();
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
        finally
        {
            if (Directory.Exists(artifactsRoot))
            {
                Directory.Delete(artifactsRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UltraDebugLog_DiskPressure_AutoDisables_AndPersistsReason()
    {
        const long diskFreeSpaceBytes = 400L * 1024L * 1024L;
        var databaseName = $"CoinBotUltraDebugDiskPressure_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var artifactsRoot = Path.Combine(AppContext.BaseDirectory, "UltraDebugIntegrationArtifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactsRoot);

        try
        {
            await using var provider = BuildProvider(
                connectionString,
                artifactsRoot,
                new MutableTimeProvider(new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero)),
                _ => diskFreeSpaceBytes);
            await using (var scope = provider.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await dbContext.Database.EnsureDeletedAsync();
                await dbContext.Database.EnsureCreatedAsync();
            }

            var service = provider.GetRequiredService<IUltraDebugLogService>();
            await service.EnableAsync(
                new UltraDebugLogEnableRequest("1h", "admin-01", "ops-admin@coinbot.test", null, null, "corr-int-ultra-5", 256, 1024));

            await service.WriteAsync(
                new UltraDebugLogEntry(
                    "execution",
                    "execution_dispatch_submitted",
                    "Dispatch accepted.",
                    "corr-int-ultra-5",
                    "SOLUSDT"));

            await using var verificationScope = provider.CreateAsyncScope();
            var verificationDbContext = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var snapshot = await service.GetSnapshotAsync();
            var persistedState = await verificationDbContext.UltraDebugLogStates.SingleAsync();
            var auditActionTypes = await verificationDbContext.AdminAuditLogs
                .OrderBy(item => item.CreatedAtUtc)
                .Select(item => item.ActionType)
                .ToListAsync();
            var normalFilePath = ResolveBucketFilePath(
                artifactsRoot,
                "normal",
                "coinbot-integrationtests",
                new DateTime(2026, 4, 22, 0, 0, 0, DateTimeKind.Utc),
                "execution");
            var ultraFilePath = ResolveBucketFilePath(
                artifactsRoot,
                "ultra_debug",
                "coinbot-integrationtests",
                new DateTime(2026, 4, 22, 0, 0, 0, DateTimeKind.Utc),
                "execution");
            var normalLogLine = Assert.Single(await File.ReadAllLinesAsync(normalFilePath));
            var normalDetailJson = ExtractDetailMaskedJson(normalLogLine);

            Assert.False(snapshot.IsEnabled);
            Assert.Equal("disk_pressure", snapshot.AutoDisabledReason);
            Assert.True(snapshot.IsNormalFallbackMode);
            Assert.Equal(diskFreeSpaceBytes, snapshot.DiskFreeSpaceBytes);
            Assert.False(persistedState.IsEnabled);
            Assert.Equal("disk_pressure", persistedState.AutoDisabledReason);
            Assert.Contains("ultra_log_auto_disabled_disk_pressure", auditActionTypes);
            Assert.Contains("\"fallbackMode\":true", normalDetailJson, StringComparison.Ordinal);
            Assert.Contains("\"fallbackReasonCode\":\"disk_pressure\"", normalDetailJson, StringComparison.Ordinal);
            Assert.False(File.Exists(ultraFilePath));
        }
        finally
        {
            if (Directory.Exists(artifactsRoot))
            {
                Directory.Delete(artifactsRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UltraDebugLog_SearchAsync_ReturnsMaskedFilteredPreview()
    {
        var databaseName = $"CoinBotUltraDebugSearch_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var artifactsRoot = Path.Combine(AppContext.BaseDirectory, "UltraDebugIntegrationArtifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactsRoot);

        try
        {
            await using var provider = BuildProvider(
                connectionString,
                artifactsRoot,
                new MutableTimeProvider(new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero)));
            await using (var scope = provider.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await dbContext.Database.EnsureDeletedAsync();
                await dbContext.Database.EnsureCreatedAsync();
            }

            var service = provider.GetRequiredService<IUltraDebugLogService>();
            await service.EnableAsync(
                new UltraDebugLogEnableRequest("1h", "admin-01", "ops-admin@coinbot.test", null, null, "corr-int-ultra-6", 256, 1024));
            await service.WriteAsync(
                new UltraDebugLogEntry(
                    "execution.dispatch",
                    "execution_dispatch_submitted",
                    "Dispatch accepted for SOLUSDT.",
                    "corr-int-ultra-6",
                    "SOLUSDT",
                    Detail: new
                    {
                        sourceLayer = "ExecutionEngine",
                        token = "plain-token",
                        note = "dispatch"
                    }));

            var searchResult = await service.SearchAsync(
                new UltraDebugLogSearchRequest(
                    BucketName: "ultra_debug",
                    Category: "execution",
                    Source: "ExecutionEngine",
                    SearchTerm: "dispatch",
                    FromUtc: new DateTime(2026, 4, 22, 8, 0, 0, DateTimeKind.Utc),
                    Take: 25));

            Assert.Single(searchResult.Lines);
            Assert.Equal("ExecutionEngine", searchResult.Lines.Single().Source);
            Assert.DoesNotContain("plain-token", searchResult.Lines.Single().DetailPreview ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(artifactsRoot))
            {
                Directory.Delete(artifactsRoot, recursive: true);
            }
        }
    }

    private static ServiceProvider BuildProvider(
        string connectionString,
        string artifactsRoot,
        MutableTimeProvider timeProvider,
        Func<string, long?>? availableDiskBytesResolver = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICorrelationContextAccessor, CorrelationContextAccessor>();
        services.AddSingleton<IDataScopeContext>(new TestDataScopeContext());
        services.AddSingleton<TimeProvider>(timeProvider);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(artifactsRoot));
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(connectionString));
        services.AddScoped<IAdminAuditLogService, AdminAuditLogService>();
        services.AddSingleton<IUltraDebugLogService>(serviceProvider =>
            new UltraDebugLogService(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<UltraDebugLogService>>(),
                serviceProvider.GetRequiredService<IHostEnvironment>(),
                availableDiskBytesResolver));
        return services.BuildServiceProvider();
    }

    private static string ResolveBucketFilePath(string artifactsRoot, string bucketName, string applicationName, DateTime utcDate, string? category = null)
    {
        var directoryPath = string.IsNullOrWhiteSpace(category)
            ? Path.Combine(artifactsRoot, "Logs", bucketName)
            : Path.Combine(artifactsRoot, "Logs", bucketName, ResolveCategoryFolder(category));
        Directory.CreateDirectory(directoryPath);
        return Path.Combine(directoryPath, $"{applicationName}-{utcDate:yyyyMMdd}.ndjson");
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

    private static string ExtractDetailMaskedJson(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("detailMasked").GetString() ?? string.Empty;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "CoinBot.IntegrationTests";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
