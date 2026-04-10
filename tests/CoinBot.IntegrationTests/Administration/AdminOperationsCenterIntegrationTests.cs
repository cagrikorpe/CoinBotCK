using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Application.Abstractions.Policy;
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

public sealed class AdminOperationsCenterIntegrationTests
{
    [Fact]
    public async Task AdminOperationsCenterComposer_UsesPersistedActivationState_AndFailsClosedUnknownRuntime()
    {
        var databaseName = $"CoinBotAdminOpsCenterInt_{Guid.NewGuid():N}";
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

        await switchService.SetTradeMasterStateAsync(TradeMasterSwitchState.Disarmed, "admin:super-admin", "Ops center test", "corr-ops-center-1");
        await stateService.SetStateAsync(new GlobalSystemStateSetRequest(
            GlobalSystemStateKind.Active,
            "SYSTEM_ACTIVE",
            null,
            "AdminPortal.Overview",
            "corr-ops-center-2",
            false,
            null,
            "super-admin",
            "ip:masked"));

        var activationModel = AdminActivationControlCenterComposer.Compose(
            await switchService.GetSnapshotAsync(),
            await stateService.GetSnapshotAsync(),
            new BinanceTimeSyncSnapshot(new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc), 0, 12, new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc), "Synchronized", null),
            new DegradedModeSnapshot(DegradedModeStateCode.Normal, DegradedModeReasonCode.None, false, false, new DateTime(2026, 4, 8, 11, 59, 0, DateTimeKind.Utc), new DateTime(2026, 4, 8, 11, 59, 5, DateTimeKind.Utc), 500, 8, new DateTime(2026, 4, 8, 11, 59, 10, DateTimeKind.Utc), true),
            new BotExecutionPilotOptions { PilotActivationEnabled = true, MaxPilotOrderNotional = "250" },
            "250",
            "healthy",
            new DateTime(2026, 4, 8, 12, 0, 30, DateTimeKind.Utc));

        var switchSnapshot = await switchService.GetSnapshotAsync();
        var stateSnapshot = await stateService.GetSnapshotAsync();

        var operationsModel = AdminOperationsCenterComposer.Compose(
            activationModel,
            MonitoringDashboardSnapshot.Empty(new DateTime(2026, 4, 8, 12, 0, 30, DateTimeKind.Utc)),
            new BinanceTimeSyncSnapshot(new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc), 0, 12, new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc), "Synchronized", null),
            new DegradedModeSnapshot(DegradedModeStateCode.Normal, DegradedModeReasonCode.None, false, false, new DateTime(2026, 4, 8, 11, 59, 0, DateTimeKind.Utc), new DateTime(2026, 4, 8, 11, 59, 5, DateTimeKind.Utc), 500, 8, new DateTime(2026, 4, 8, 11, 59, 10, DateTimeKind.Utc), true),
            AdminUsersPageSnapshot.Empty(new DateTime(2026, 4, 8, 12, 0, 30, DateTimeKind.Utc)),
            AdminBotOperationsPageSnapshot.Empty(new DateTime(2026, 4, 8, 12, 0, 30, DateTimeKind.Utc)),
            Array.Empty<ApiCredentialAdminSummary>(),
            GlobalPolicySnapshot.CreateDefault(new DateTime(2026, 4, 8, 12, 0, 30, DateTimeKind.Utc)),
            new BotExecutionPilotOptions { PilotActivationEnabled = true, MaxPilotOrderNotional = "250" },
            null,
            switchSnapshot,
            stateSnapshot,
            true,
            new DateTime(2026, 4, 8, 12, 0, 30, DateTimeKind.Utc));

        Assert.True(operationsModel.IsAccessible);
        Assert.Equal("Eksik", operationsModel.PrimaryFlow.Setup.StatusLabel);
        Assert.Equal("Exchange bagli degil", operationsModel.PrimaryFlow.Setup.PrimaryMessage);
        Assert.Equal("Eksik", operationsModel.PrimaryFlow.Monitoring.StatusLabel);
        Assert.Equal("critical", operationsModel.RuntimeHealthCenter.StatusTone);
        Assert.Contains(operationsModel.RuntimeHealthCenter.Signals, item => item.Code == "WorkerHeartbeatUnavailable");
        Assert.Contains(operationsModel.SummaryCards, item => item.Label == "CanActivate" && item.Value == "true");

        var rolloutModel = AdminOperationsCenterComposer.BuildRolloutClosureCenter(
            activationModel,
            await switchService.GetSnapshotAsync(),
            await stateService.GetSnapshotAsync(),
            new DegradedModeSnapshot(DegradedModeStateCode.Normal, DegradedModeReasonCode.None, false, false, new DateTime(2026, 4, 8, 11, 59, 0, DateTimeKind.Utc), new DateTime(2026, 4, 8, 11, 59, 5, DateTimeKind.Utc), 500, 8, new DateTime(2026, 4, 8, 11, 59, 10, DateTimeKind.Utc), false),
            MonitoringDashboardSnapshot.Empty(new DateTime(2026, 4, 8, 12, 0, 30, DateTimeKind.Utc)),
            Array.Empty<ApiCredentialAdminSummary>(),
            GlobalPolicySnapshot.CreateDefault(new DateTime(2026, 4, 8, 12, 0, 30, DateTimeKind.Utc)),
            new BotExecutionPilotOptions
            {
                PilotActivationEnabled = true,
                MaxPilotOrderNotional = "250",
                AllowedUserIds = ["user-1"],
                AllowedBotIds = ["bot-1"],
                AllowedSymbols = ["BTCUSDT"]
            },
            null,
            null,
            Array.Empty<AdminRolloutEvidenceInput>(),
            new DateTime(2026, 4, 8, 12, 0, 30, DateTimeKind.Utc));

        Assert.Equal("Blocked", rolloutModel.StatusLabel);
        Assert.Contains(rolloutModel.MandatoryGates, item => item.ReasonCode == "BuildEvidenceMissing");
        Assert.Contains(rolloutModel.BlockingReasons, item => item.ReasonCode == "ContinuityEvidenceMissing");

        await dbContext.Database.EnsureDeletedAsync();
    }

    private static string ResolveConnectionString(string databaseName) => SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);

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






