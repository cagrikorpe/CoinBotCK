using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
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

    private static string ResolveConnectionString(string databaseName)
    {
        var configuredConnectionString = Environment.GetEnvironmentVariable("COINBOT_INTEGRATION_SQLSERVER_CONNECTION_STRING");

        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return configuredConnectionString.Replace("{Database}", databaseName, StringComparison.OrdinalIgnoreCase);
        }

        return $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";
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
