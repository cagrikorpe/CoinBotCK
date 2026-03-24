using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Policy;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoinBot.UnitTests.Infrastructure.Policy;

public sealed class GlobalPolicyEngineTests
{
    [Fact]
    public async Task UpdateAndRollback_ProduceVersionHistory_AuditAndEvaluationBehavior()
    {
        await using var dbContext = CreateDbContext();
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var auditService = new FakeAdminAuditLogService();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 24, 13, 0, 0, TimeSpan.Zero));
        var engine = new GlobalPolicyEngine(
            dbContext,
            memoryCache,
            auditService,
            timeProvider,
            NullLogger<GlobalPolicyEngine>.Instance);

        var policy = new RiskPolicySnapshot(
            "GlobalRiskPolicy",
            new ExecutionGuardPolicy(250_000m, 500_000m, 20, CloseOnlyBlocksNewPositions: true),
            new AutonomyPolicy(AutonomyPolicyMode.ObserveOnly, RequireManualApprovalForLive: true),
            [
                new SymbolRestriction("BTCUSDT", SymbolRestrictionState.CloseOnly, "manual review", timeProvider.GetUtcNow().UtcDateTime, "super-admin")
            ]);

        var updatedSnapshot = await engine.UpdateAsync(
            new GlobalPolicyUpdateRequest(
                policy,
                "super-admin",
                "Seed policy",
                "corr-policy-1",
                "AdminPortal.Settings"),
            CancellationToken.None);

        Assert.Equal(2, updatedSnapshot.CurrentVersion);
        Assert.True(updatedSnapshot.Versions.Count == 2);
        Assert.Single(auditService.Requests);
        Assert.Equal("GlobalPolicy.Update", auditService.Requests[0].ActionType);
        Assert.Equal("RiskPolicy", auditService.Requests[0].TargetType);
        Assert.Equal("GlobalRiskPolicy", auditService.Requests[0].TargetId);

        var blockedEvaluation = await engine.EvaluateAsync(
            new GlobalPolicyEvaluationRequest(
                "user-01",
                "BTCUSDT",
                ExecutionEnvironment.Demo,
                ExecutionOrderSide.Buy,
                1m,
                100m),
            CancellationToken.None);

        Assert.True(blockedEvaluation.IsBlocked);
        Assert.Equal("GlobalPolicyObserveOnly", blockedEvaluation.BlockCode);
        Assert.Contains("ObserveOnly", blockedEvaluation.Message, StringComparison.Ordinal);
        Assert.Equal(2, blockedEvaluation.PolicyVersion);
        Assert.Equal(AutonomyPolicyMode.ObserveOnly, blockedEvaluation.EffectiveAutonomyMode);

        var rolledBackSnapshot = await engine.RollbackAsync(
            new GlobalPolicyRollbackRequest(
                1,
                "super-admin",
                "Rollback to baseline",
                "corr-policy-2",
                "AdminPortal.Settings"),
            CancellationToken.None);

        Assert.Equal(3, rolledBackSnapshot.CurrentVersion);
        Assert.True(rolledBackSnapshot.Versions.Count == 3);
        Assert.True(auditService.Requests.Count == 2);
        Assert.Equal("GlobalPolicy.Rollback", auditService.Requests[1].ActionType);
        Assert.Equal("RiskPolicy", auditService.Requests[1].TargetType);
        Assert.Equal("GlobalRiskPolicy", auditService.Requests[1].TargetId);
        Assert.Contains(rolledBackSnapshot.Versions, version => version.Version == 3 && version.RolledBackFromVersion == 2);
        Assert.Equal(AutonomyPolicyMode.LowRiskAutoAct, rolledBackSnapshot.Policy.AutonomyPolicy.Mode);

        var allowedEvaluation = await engine.EvaluateAsync(
            new GlobalPolicyEvaluationRequest(
                "user-01",
                "BTCUSDT",
                ExecutionEnvironment.Demo,
                ExecutionOrderSide.Buy,
                1m,
                100m),
            CancellationToken.None);

        Assert.False(allowedEvaluation.IsBlocked);
        Assert.False(allowedEvaluation.IsAdvisory);
        Assert.Equal(3, allowedEvaluation.PolicyVersion);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private sealed class FakeAdminAuditLogService : IAdminAuditLogService
    {
        public List<AdminAuditLogWriteRequest> Requests { get; } = [];

        public Task WriteAsync(AdminAuditLogWriteRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class TestDataScopeContext : CoinBot.Application.Abstractions.DataScope.IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }
}
