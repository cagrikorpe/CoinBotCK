using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.UnitTests.Infrastructure.Administration;

public sealed class GlobalSystemStateServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_ReturnsActiveDefault_WhenStateIsMissing()
    {
        await using var harness = CreateHarness();

        var snapshot = await harness.Service.GetSnapshotAsync();

        Assert.Equal(GlobalSystemStateKind.Active, snapshot.State);
        Assert.Equal("SYSTEM_ACTIVE", snapshot.ReasonCode);
        Assert.False(snapshot.IsPersisted);
    }


    [Fact]
    public async Task GetSnapshotAsync_ResetsExpiredAutomaticState_ToActiveDefault()
    {
        await using var harness = CreateHarness();
        harness.DbContext.GlobalSystemStates.Add(new GlobalSystemState
        {
            Id = GlobalSystemStateDefaults.SingletonId,
            State = GlobalSystemStateKind.Degraded,
            ReasonCode = "AUTONOMY_BREAKER_ORDEREXECUTION",
            Message = "Expired breaker cooldown.",
            Source = "Autonomy.DependencyBreaker",
            IsManualOverride = false,
            ExpiresAtUtc = new DateTime(2026, 3, 24, 11, 59, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 3, 24, 11, 55, 0, DateTimeKind.Utc),
            UpdatedByUserId = "system:autonomy",
            Version = 7
        });
        await harness.DbContext.SaveChangesAsync();

        var snapshot = await harness.Service.GetSnapshotAsync();
        var entity = await harness.DbContext.GlobalSystemStates.SingleAsync();

        Assert.Equal(GlobalSystemStateKind.Active, snapshot.State);
        Assert.Equal("SYSTEM_ACTIVE", snapshot.ReasonCode);
        Assert.Equal(GlobalSystemStateKind.Active, entity.State);
        Assert.Null(entity.ExpiresAtUtc);
        Assert.Equal("system:global-state-expiry", entity.UpdatedByUserId);
    }

    [Fact]
    public async Task SetStateAsync_PersistsState_AndWritesAuditLog()
    {
        await using var harness = CreateHarness();
        var utcNow = new DateTime(2026, 3, 24, 12, 0, 0, DateTimeKind.Utc);

        var snapshot = await harness.Service.SetStateAsync(
            new GlobalSystemStateSetRequest(
                GlobalSystemStateKind.SoftHalt,
                "SOFT_HALT_TEST",
                "Controlled throttle",
                "AdminPortal.Settings",
                "corr-gs-test",
                IsManualOverride: true,
                ExpiresAtUtc: utcNow.AddHours(4),
                UpdatedByUserId: "super-admin",
                UpdatedFromIp: "ip:masked"));

        var entity = await harness.DbContext.GlobalSystemStates.SingleAsync();
        var auditLog = await harness.DbContext.AuditLogs.SingleAsync();
        var history = await harness.DbContext.SystemStateHistories.SingleAsync();

        Assert.Equal(GlobalSystemStateKind.SoftHalt, snapshot.State);
        Assert.True(snapshot.IsPersisted);
        Assert.Equal(GlobalSystemStateKind.SoftHalt, entity.State);
        Assert.Equal("SOFT_HALT_TEST", entity.ReasonCode);
        Assert.Equal("GlobalSystemState.SoftHalt", auditLog.Action);
        Assert.Equal("Applied", auditLog.Outcome);
        Assert.Equal(1, history.Version);
        Assert.Equal("GST-000001", history.HistoryReference);
        Assert.Equal("Active", history.PreviousState);
        Assert.Equal("SOFT_HALT_TEST", history.ReasonCode);
        Assert.Equal("corr-gs-test", history.CorrelationId);
    }

    private static TestHarness CreateHarness()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var correlationContextAccessor = new CorrelationContextAccessor();
        var auditLogService = new AuditLogService(dbContext, correlationContextAccessor);
        var service = new GlobalSystemStateService(
            dbContext,
            auditLogService,
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
        GlobalSystemStateService service) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public GlobalSystemStateService Service { get; } = service;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }
    }
}
