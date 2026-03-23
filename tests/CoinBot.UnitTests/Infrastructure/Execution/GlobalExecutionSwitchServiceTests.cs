using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.UnitTests.Infrastructure.Execution;

public sealed class GlobalExecutionSwitchServiceTests
{
    [Fact]
    public async Task SetTradeMasterStateAsync_PersistsSwitchState_AndWritesMandatoryAuditLog()
    {
        await using var harness = CreateHarness();

        var snapshot = await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-01",
            context: "Controlled enablement",
            correlationId: "corr-001");

        var switchEntity = await harness.DbContext.GlobalExecutionSwitches.SingleAsync();
        var auditLog = await harness.DbContext.AuditLogs.SingleAsync();

        Assert.True(snapshot.IsPersisted);
        Assert.Equal(TradeMasterSwitchState.Armed, snapshot.TradeMasterState);
        Assert.True(snapshot.DemoModeEnabled);
        Assert.Equal(TradeMasterSwitchState.Armed, switchEntity.TradeMasterState);
        Assert.Equal("admin-01", auditLog.Actor);
        Assert.Equal("TradeMaster.Armed", auditLog.Action);
        Assert.Equal("GlobalExecutionSwitch/TradeMaster", auditLog.Target);
        Assert.Equal("Controlled enablement", auditLog.Context);
        Assert.Equal("corr-001", auditLog.CorrelationId);
        Assert.Equal("Applied", auditLog.Outcome);
        Assert.Equal(nameof(ExecutionEnvironment.Demo), auditLog.Environment);
        Assert.NotEqual(default, auditLog.CreatedDate);
    }

    [Fact]
    public async Task SetDemoModeAsync_PersistsSwitchState_AndWritesMandatoryAuditLog()
    {
        await using var harness = CreateHarness();

        var snapshot = await harness.SwitchService.SetDemoModeAsync(
            isEnabled: false,
            actor: "admin-02",
            liveApproval: new TradingModeLiveApproval("chg-002"),
            context: "Live path prepared",
            correlationId: "corr-002");

        var switchEntity = await harness.DbContext.GlobalExecutionSwitches.SingleAsync();
        var auditLog = await harness.DbContext.AuditLogs.SingleAsync();

        Assert.True(snapshot.IsPersisted);
        Assert.False(snapshot.DemoModeEnabled);
        Assert.False(switchEntity.DemoModeEnabled);
        Assert.Equal("admin-02", auditLog.Actor);
        Assert.Equal("DemoMode.Disabled", auditLog.Action);
        Assert.Equal("GlobalExecutionSwitch/DemoMode", auditLog.Target);
        Assert.Equal("Live path prepared", auditLog.Context);
        Assert.Equal("corr-002", auditLog.CorrelationId);
        Assert.Equal("Applied", auditLog.Outcome);
        Assert.Equal(nameof(ExecutionEnvironment.Live), auditLog.Environment);
        Assert.NotEqual(default, auditLog.CreatedDate);
    }

    [Fact]
    public async Task SetDemoModeAsync_RejectsLiveSwitchWithoutExplicitApproval()
    {
        await using var harness = CreateHarness();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.SwitchService.SetDemoModeAsync(
                isEnabled: false,
                actor: "admin-03",
                context: "Live path prepared",
                correlationId: "corr-003"));

        var auditLog = await harness.DbContext.AuditLogs.SingleAsync();

        Assert.Contains("Explicit live approval is required", exception.Message, StringComparison.Ordinal);
        Assert.Equal("Blocked:LiveApprovalRequired", auditLog.Outcome);
        Assert.Equal(nameof(ExecutionEnvironment.Live), auditLog.Environment);
    }

    private static TestHarness CreateHarness()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var auditLogService = new AuditLogService(dbContext, new CorrelationContextAccessor());
        var switchService = new GlobalExecutionSwitchService(dbContext, auditLogService);

        return new TestHarness(dbContext, switchService);
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => false;
    }

    private sealed class TestHarness(
        ApplicationDbContext dbContext,
        IGlobalExecutionSwitchService switchService) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public IGlobalExecutionSwitchService SwitchService { get; } = switchService;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }
    }
}
