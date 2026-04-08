using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.IntegrationTests.Infrastructure;
using CoinBot.Web.ViewModels.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoinBot.IntegrationTests.Administration;

public sealed class AdminFoundationIntegrationTests
{
    [Fact]
    public async Task AdminFoundationServices_IntegrateAcrossStateSwitchReadModelAndIdempotency()
    {
        var databaseName = $"CoinBotAdminFoundationInt_{Guid.NewGuid():N}";
        var connectionString = ResolveConnectionString(databaseName);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(2026, 3, 24, 12, 0, 0, TimeSpan.Zero)));
        services.AddSingleton<ICorrelationContextAccessor, CorrelationContextAccessor>();
        services.AddSingleton<IDataScopeContext>(new TestDataScopeContext());
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(connectionString));
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<IGlobalExecutionSwitchService, GlobalExecutionSwitchService>();
        services.AddScoped<IGlobalSystemStateService, GlobalSystemStateService>();
        services.AddScoped<IAdminCommandRegistry, AdminCommandRegistryService>();
        services.AddScoped<IAdminShellReadModelService, AdminShellReadModelService>();

        using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<IAdminCommandRegistry>();
        var switchService = scope.ServiceProvider.GetRequiredService<IGlobalExecutionSwitchService>();
        var stateService = scope.ServiceProvider.GetRequiredService<IGlobalSystemStateService>();
        var readModelService = scope.ServiceProvider.GetRequiredService<IAdminShellReadModelService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        var startResult = await registry.TryStartAsync(
            new AdminCommandStartRequest(
                "cmd-int-001",
                "Admin.Settings.GlobalSystemState.Update",
                "super-admin",
                "GlobalSystemState.Singleton",
                "payload-hash-int",
                "corr-int-1"));
        await switchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            "admin:super-admin",
            "Integration test",
            "corr-int-2");
        await stateService.SetStateAsync(
            new GlobalSystemStateSetRequest(
                GlobalSystemStateKind.Degraded,
                "DEGRADED_TEST",
                "Snapshot mode",
                "AdminPortal.Settings",
                "corr-int-3",
                IsManualOverride: true,
                ExpiresAtUtc: null,
                UpdatedByUserId: "super-admin",
                UpdatedFromIp: "ip:masked"));
        await registry.CompleteAsync(
            new AdminCommandCompletionRequest(
                "cmd-int-001",
                "payload-hash-int",
                AdminCommandStatus.Completed,
                "Applied.",
                "corr-int-4"));

        var healthSnapshot = await readModelService.GetHealthSnapshotAsync();
        var registryEntry = await dbContext.AdminCommandRegistryEntries.SingleAsync();
        var systemState = await dbContext.GlobalSystemStates.SingleAsync();

        Assert.Equal(AdminCommandStartDisposition.Started, startResult.Disposition);
        Assert.Equal("DEGRADED", healthSnapshot.EnvironmentBadge);
        Assert.Equal(GlobalSystemStateKind.Degraded, healthSnapshot.SystemState);
        Assert.True(healthSnapshot.IsManualOverride);
        Assert.Equal(AdminCommandStatus.Completed, registryEntry.Status);
        Assert.Equal(GlobalSystemStateKind.Degraded, systemState.State);

        await dbContext.Database.EnsureDeletedAsync();
    }


    [Fact]
    public async Task AdminActivationControlCenterComposer_UsesPersistedSnapshots_AndFailsClosedOnFullHalt()
    {
        var databaseName = $"CoinBotAdminActivationInt_{Guid.NewGuid():N}";
        var connectionString = ResolveConnectionString(databaseName);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(2026, 4, 8, 12, 0, 0, TimeSpan.Zero)));
        services.AddSingleton<ICorrelationContextAccessor, CorrelationContextAccessor>();
        services.AddSingleton<IDataScopeContext>(new TestDataScopeContext());
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(connectionString));
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<IGlobalExecutionSwitchService, GlobalExecutionSwitchService>();
        services.AddScoped<IGlobalSystemStateService, GlobalSystemStateService>();

        using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var switchService = scope.ServiceProvider.GetRequiredService<IGlobalExecutionSwitchService>();
        var stateService = scope.ServiceProvider.GetRequiredService<IGlobalSystemStateService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        await switchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Disarmed,
            "admin:super-admin",
            "Activation composer integration",
            "corr-activation-int-1");
        await stateService.SetStateAsync(
            new GlobalSystemStateSetRequest(
                GlobalSystemStateKind.Active,
                "SYSTEM_ACTIVE",
                Message: null,
                Source: "AdminPortal.Settings",
                CorrelationId: "corr-activation-int-2",
                IsManualOverride: false,
                ExpiresAtUtc: null,
                UpdatedByUserId: "super-admin",
                UpdatedFromIp: "ip:masked"));

        var readyModel = AdminActivationControlCenterComposer.Compose(
            await switchService.GetSnapshotAsync(),
            await stateService.GetSnapshotAsync(),
            new BinanceTimeSyncSnapshot(
                new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc),
                0,
                14,
                new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc),
                "Synchronized",
                null),
            new DegradedModeSnapshot(
                DegradedModeStateCode.Normal,
                DegradedModeReasonCode.None,
                SignalFlowBlocked: false,
                ExecutionFlowBlocked: false,
                LatestDataTimestampAtUtc: new DateTime(2026, 4, 8, 11, 59, 0, DateTimeKind.Utc),
                LatestHeartbeatReceivedAtUtc: new DateTime(2026, 4, 8, 11, 59, 5, DateTimeKind.Utc),
                LatestDataAgeMilliseconds: 500,
                LatestClockDriftMilliseconds: 8,
                LastStateChangedAtUtc: new DateTime(2026, 4, 8, 11, 59, 10, DateTimeKind.Utc),
                IsPersisted: true),
            new BotExecutionPilotOptions
            {
                PilotActivationEnabled = true,
                MaxPilotOrderNotional = "250"
            },
            "250",
            "healthy",
            new DateTime(2026, 4, 8, 12, 0, 30, DateTimeKind.Utc));

        Assert.True(readyModel.IsActivatable);
        Assert.Equal("ActivationReady", readyModel.LastDecision.Code);

        await stateService.SetStateAsync(
            new GlobalSystemStateSetRequest(
                GlobalSystemStateKind.FullHalt,
                "EMERGENCY_STOP",
                Message: "Emergency stop",
                Source: "AdminPortal.Settings",
                CorrelationId: "corr-activation-int-3",
                IsManualOverride: true,
                ExpiresAtUtc: null,
                UpdatedByUserId: "super-admin",
                UpdatedFromIp: "ip:masked"));

        var blockedModel = AdminActivationControlCenterComposer.Compose(
            await switchService.GetSnapshotAsync(),
            await stateService.GetSnapshotAsync(),
            new BinanceTimeSyncSnapshot(
                new DateTime(2026, 4, 8, 12, 1, 0, DateTimeKind.Utc),
                new DateTime(2026, 4, 8, 12, 1, 0, DateTimeKind.Utc),
                0,
                14,
                new DateTime(2026, 4, 8, 12, 1, 0, DateTimeKind.Utc),
                "Synchronized",
                null),
            new DegradedModeSnapshot(
                DegradedModeStateCode.Normal,
                DegradedModeReasonCode.None,
                SignalFlowBlocked: false,
                ExecutionFlowBlocked: false,
                LatestDataTimestampAtUtc: new DateTime(2026, 4, 8, 12, 0, 30, DateTimeKind.Utc),
                LatestHeartbeatReceivedAtUtc: new DateTime(2026, 4, 8, 12, 0, 35, DateTimeKind.Utc),
                LatestDataAgeMilliseconds: 500,
                LatestClockDriftMilliseconds: 8,
                LastStateChangedAtUtc: new DateTime(2026, 4, 8, 12, 0, 40, DateTimeKind.Utc),
                IsPersisted: true),
            new BotExecutionPilotOptions
            {
                PilotActivationEnabled = true,
                MaxPilotOrderNotional = "250"
            },
            "250",
            "healthy",
            new DateTime(2026, 4, 8, 12, 1, 30, DateTimeKind.Utc));

        Assert.False(blockedModel.IsActivatable);
        Assert.Equal("GlobalSystemFullHalt", blockedModel.LastDecision.Code);

        await dbContext.Database.EnsureDeletedAsync();
    }

    private static string ResolveConnectionString(string databaseName)
    {
        return SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}



