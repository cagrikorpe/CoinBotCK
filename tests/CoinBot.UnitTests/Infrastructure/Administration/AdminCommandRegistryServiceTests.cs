using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.UnitTests.Infrastructure.Administration;

public sealed class AdminCommandRegistryServiceTests
{
    [Fact]
    public async Task TryStartAsync_ReturnsAlreadyRunning_ForSameCommandIdAndPayload()
    {
        await using var harness = CreateHarness();

        var first = await harness.Service.TryStartAsync(
            new AdminCommandStartRequest(
                "cmd-001",
                "Admin.Settings.TradeMaster.Update",
                "super-admin",
                "GlobalExecutionSwitch.TradeMaster",
                "payload-hash-1",
                "corr-1"));
        var second = await harness.Service.TryStartAsync(
            new AdminCommandStartRequest(
                "cmd-001",
                "Admin.Settings.TradeMaster.Update",
                "super-admin",
                "GlobalExecutionSwitch.TradeMaster",
                "payload-hash-1",
                "corr-1"));

        Assert.Equal(AdminCommandStartDisposition.Started, first.Disposition);
        Assert.Equal(AdminCommandStartDisposition.AlreadyRunning, second.Disposition);
        Assert.Equal(1, await harness.DbContext.AdminCommandRegistryEntries.CountAsync());
    }

    [Fact]
    public async Task TryStartAsync_ReturnsPayloadConflict_ForSameCommandIdAndDifferentPayload()
    {
        await using var harness = CreateHarness();

        await harness.Service.TryStartAsync(
            new AdminCommandStartRequest(
                "cmd-002",
                "Admin.Settings.DemoMode.Update",
                "super-admin",
                "GlobalExecutionSwitch.DemoMode",
                "payload-hash-1",
                "corr-2"));
        var second = await harness.Service.TryStartAsync(
            new AdminCommandStartRequest(
                "cmd-002",
                "Admin.Settings.DemoMode.Update",
                "super-admin",
                "GlobalExecutionSwitch.DemoMode",
                "payload-hash-2",
                "corr-2"));

        Assert.Equal(AdminCommandStartDisposition.PayloadConflict, second.Disposition);
    }

    [Fact]
    public async Task TryStartAsync_ReturnsAlreadyCompleted_AfterCompletionWithSamePayload()
    {
        await using var harness = CreateHarness();

        await harness.Service.TryStartAsync(
            new AdminCommandStartRequest(
                "cmd-003",
                "Admin.Settings.GlobalSystemState.Update",
                "super-admin",
                "GlobalSystemState.Singleton",
                "payload-hash-3",
                "corr-3"));
        await harness.Service.CompleteAsync(
            new AdminCommandCompletionRequest(
                "cmd-003",
                "payload-hash-3",
                AdminCommandStatus.Completed,
                "Applied.",
                "corr-3"));

        var second = await harness.Service.TryStartAsync(
            new AdminCommandStartRequest(
                "cmd-003",
                "Admin.Settings.GlobalSystemState.Update",
                "super-admin",
                "GlobalSystemState.Singleton",
                "payload-hash-3",
                "corr-3"));

        Assert.Equal(AdminCommandStartDisposition.AlreadyCompleted, second.Disposition);
        Assert.Equal(AdminCommandStatus.Completed, second.PersistedStatus);
        Assert.Equal("Applied.", second.ResultSummary);
    }

    [Fact]
    public async Task TryStartAsync_ReturnsAlreadyRunning_ForDifferentCommandIdOnSameScope()
    {
        await using var harness = CreateHarness();

        var first = await harness.Service.TryStartAsync(
            new AdminCommandStartRequest(
                "cmd-scope-001",
                "Admin.Settings.CrisisEscalation.Execute",
                "super-admin",
                "GLOBAL_FLATTEN",
                "payload-hash-scope-1",
                "corr-scope-1"));
        var second = await harness.Service.TryStartAsync(
            new AdminCommandStartRequest(
                "cmd-scope-002",
                "Admin.Settings.CrisisEscalation.Execute",
                "super-admin",
                "GLOBAL_FLATTEN",
                "payload-hash-scope-2",
                "corr-scope-2"));

        Assert.Equal(AdminCommandStartDisposition.Started, first.Disposition);
        Assert.Equal(AdminCommandStartDisposition.AlreadyRunning, second.Disposition);
        Assert.Equal(1, await harness.DbContext.AdminCommandRegistryEntries.CountAsync());
    }

    [Fact]
    public async Task TryStartAsync_ReturnsAlreadyCompleted_ForDifferentCommandIdOnSameScopeAndPayload()
    {
        await using var harness = CreateHarness();

        await harness.Service.TryStartAsync(
            new AdminCommandStartRequest(
                "cmd-scope-complete-001",
                "Admin.Settings.CrisisEscalation.Execute",
                "super-admin",
                "GLOBAL_PURGE",
                "payload-hash-scope-complete",
                "corr-scope-complete-1"));
        await harness.Service.CompleteAsync(
            new AdminCommandCompletionRequest(
                "cmd-scope-complete-001",
                "payload-hash-scope-complete",
                AdminCommandStatus.Completed,
                "Scope result reused.",
                "corr-scope-complete-1"));

        var second = await harness.Service.TryStartAsync(
            new AdminCommandStartRequest(
                "cmd-scope-complete-002",
                "Admin.Settings.CrisisEscalation.Execute",
                "super-admin",
                "GLOBAL_PURGE",
                "payload-hash-scope-complete",
                "corr-scope-complete-2"));

        Assert.Equal(AdminCommandStartDisposition.AlreadyCompleted, second.Disposition);
        Assert.Equal(AdminCommandStatus.Completed, second.PersistedStatus);
        Assert.Equal("Scope result reused.", second.ResultSummary);
        Assert.Equal(1, await harness.DbContext.AdminCommandRegistryEntries.CountAsync());
    }

    private static TestHarness CreateHarness()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var service = new AdminCommandRegistryService(
            dbContext,
            new FakeTimeProvider(new DateTimeOffset(2026, 3, 24, 12, 0, 0, TimeSpan.Zero)));

        return new TestHarness(dbContext, service);
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestHarness(
        ApplicationDbContext dbContext,
        AdminCommandRegistryService service) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public AdminCommandRegistryService Service { get; } = service;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }
    }
}
