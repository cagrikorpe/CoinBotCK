using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.DemoPortfolio;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Observability;

public sealed class HealthChecksTests
{
    [Fact]
    public async Task DatabaseHealthCheck_ReturnsHealthy_WhenDatabaseIsReachable()
    {
        await using var dbContext = CreateDbContext();
        var databaseHealthCheck = new DatabaseHealthCheck(dbContext);

        var result = await databaseHealthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task MarketHealthCheck_ReturnsHealthy_WhenExchangeValidationIsFresh()
    {
        var now = new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(now);
        await using var dbContext = CreateDbContext();

        dbContext.ExchangeAccounts.Add(new ExchangeAccount
        {
            OwnerUserId = "user-001",
            ExchangeName = "Binance",
            DisplayName = "Main account",
            LastValidatedAt = now.UtcDateTime.AddMinutes(-5)
        });

        await dbContext.SaveChangesAsync();

        var marketHealthCheck = new MarketHealthCheck(
            dbContext,
            Options.Create(new MarketHealthOptions { ValidationFreshnessMinutes = 15 }),
            timeProvider);

        var result = await marketHealthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task MarketHealthCheck_ReturnsUnhealthy_WhenNoExchangeValidationExists()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        await using var dbContext = CreateDbContext();
        var marketHealthCheck = new MarketHealthCheck(
            dbContext,
            Options.Create(new MarketHealthOptions { ValidationFreshnessMinutes = 15 }),
            timeProvider);

        var result = await marketHealthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task DemoEngineHealthCheck_ReturnsHealthy_WhenTradeMasterIsArmed_AndDemoModeIsEnabled()
    {
        await using var dbContext = CreateDbContext();
        var correlationContextAccessor = new CorrelationContextAccessor();
        var auditLogService = new AuditLogService(dbContext, correlationContextAccessor);
        var globalExecutionSwitchService = new GlobalExecutionSwitchService(dbContext, auditLogService);

        await globalExecutionSwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "system",
            correlationId: "corr-health-001");

        var demoEngineHealthCheck = new DemoEngineHealthCheck(globalExecutionSwitchService, dbContext);

        var result = await demoEngineHealthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task DemoEngineHealthCheck_ReturnsUnhealthy_WhenTradeMasterIsDisarmed()
    {
        await using var dbContext = CreateDbContext();
        var correlationContextAccessor = new CorrelationContextAccessor();
        var auditLogService = new AuditLogService(dbContext, correlationContextAccessor);
        var globalExecutionSwitchService = new GlobalExecutionSwitchService(dbContext, auditLogService);

        await globalExecutionSwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Disarmed,
            actor: "system",
            correlationId: "corr-health-002");

        var demoEngineHealthCheck = new DemoEngineHealthCheck(globalExecutionSwitchService, dbContext);

        var result = await demoEngineHealthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task DemoEngineHealthCheck_ReturnsUnhealthy_WhenActiveDemoSessionHasDrift()
    {
        await using var dbContext = CreateDbContext();
        var correlationContextAccessor = new CorrelationContextAccessor();
        var auditLogService = new AuditLogService(dbContext, correlationContextAccessor);
        var globalExecutionSwitchService = new GlobalExecutionSwitchService(dbContext, auditLogService);

        await globalExecutionSwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "system",
            correlationId: "corr-health-003");

        dbContext.DemoSessions.Add(new DemoSession
        {
            OwnerUserId = "user-demo",
            SequenceNumber = 1,
            SeedAsset = "USDT",
            SeedAmount = 1000m,
            State = DemoSessionState.Active,
            ConsistencyStatus = DemoConsistencyStatus.DriftDetected,
            StartedAtUtc = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc)
        });

        await dbContext.SaveChangesAsync();

        var demoEngineHealthCheck = new DemoEngineHealthCheck(globalExecutionSwitchService, dbContext);

        var result = await demoEngineHealthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }
}
