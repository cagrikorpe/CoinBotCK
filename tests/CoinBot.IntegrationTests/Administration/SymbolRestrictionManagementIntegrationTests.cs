using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Policy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoinBot.IntegrationTests.Administration;

public sealed class SymbolRestrictionManagementIntegrationTests
{
    [Fact]
    public async Task GlobalPolicyEngine_UpdatePersistsSymbolRestrictions_AuditAndVersionHistory_OnSqlServer()
    {
        var databaseName = $"CoinBotSymbolRestrictionInt_{Guid.NewGuid():N}";
        var connectionString = ResolveConnectionString(databaseName);
        var now = new DateTimeOffset(2026, 3, 31, 9, 0, 0, TimeSpan.Zero);

        await using var dbContext = CreateDbContext(connectionString);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var timeProvider = new FixedTimeProvider(now);
        var auditService = new AdminAuditLogService(dbContext, new CorrelationContextAccessor(), timeProvider);
        var engine = new GlobalPolicyEngine(
            dbContext,
            memoryCache,
            auditService,
            timeProvider,
            NullLogger<GlobalPolicyEngine>.Instance);
        var policy = new RiskPolicySnapshot(
            "GlobalRiskPolicy",
            new ExecutionGuardPolicy(250_000m, 500_000m, 20, CloseOnlyBlocksNewPositions: true),
            new AutonomyPolicy(AutonomyPolicyMode.ManualApprovalRequired, RequireManualApprovalForLive: true),
            [
                new SymbolRestriction("BTCUSDT", SymbolRestrictionState.Blocked, "exchange halt", now.UtcDateTime, "super-admin"),
                new SymbolRestriction("ETHUSDT", SymbolRestrictionState.ReduceOnly, "liquidity cap", now.UtcDateTime, "super-admin")
            ]);

        var updatedSnapshot = await engine.UpdateAsync(
            new GlobalPolicyUpdateRequest(
                policy,
                "super-admin",
                "Restriction update",
                "corr-policy-int-1",
                "AdminPortal.Settings.SymbolRestrictions",
                "ip:masked",
                "ua:masked"),
            CancellationToken.None);

        var trackedPolicy = await dbContext.RiskPolicies.AsNoTracking().SingleAsync();
        var versions = await dbContext.RiskPolicyVersions
            .AsNoTracking()
            .OrderBy(entity => entity.Version)
            .ToListAsync();
        var latestVersion = Assert.Single(updatedSnapshot.Versions, version => version.Version == updatedSnapshot.CurrentVersion);
        var auditLog = await dbContext.AdminAuditLogs
            .AsNoTracking()
            .SingleAsync(entity => entity.ActionType == "GlobalPolicy.Update");

        Assert.Equal(2, updatedSnapshot.CurrentVersion);
        Assert.Equal(2, trackedPolicy.CurrentVersion);
        Assert.Equal(2, versions.Count);
        Assert.Equal("AdminPortal.Settings.SymbolRestrictions", latestVersion.Source);
        Assert.Equal("corr-policy-int-1", latestVersion.CorrelationId);
        Assert.Equal(2, updatedSnapshot.Policy.SymbolRestrictions.Count);
        Assert.Contains(updatedSnapshot.Policy.SymbolRestrictions, item =>
            item.Symbol == "BTCUSDT" &&
            item.State == SymbolRestrictionState.Blocked &&
            item.Reason == "exchange halt" &&
            item.UpdatedByUserId == "super-admin");
        Assert.Contains(updatedSnapshot.Policy.SymbolRestrictions, item =>
            item.Symbol == "ETHUSDT" &&
            item.State == SymbolRestrictionState.ReduceOnly &&
            item.Reason == "liquidity cap" &&
            item.UpdatedByUserId == "super-admin");
        Assert.Contains(latestVersion.DiffEntries, entry => entry.Path.StartsWith("SymbolRestrictions[BTCUSDT]", StringComparison.Ordinal));
        Assert.Contains(latestVersion.DiffEntries, entry => entry.Path.StartsWith("SymbolRestrictions[ETHUSDT]", StringComparison.Ordinal));
        Assert.Equal("GlobalPolicy.Update", auditLog.ActionType);
        Assert.Equal("RiskPolicy", auditLog.TargetType);
        Assert.Equal("GlobalRiskPolicy", auditLog.TargetId);
        Assert.DoesNotContain("secret", auditLog.NewValueSummary ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        await dbContext.Database.EnsureDeletedAsync();
    }

    private static ApplicationDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        return new ApplicationDbContext(options, new TestDataScopeContext());
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
