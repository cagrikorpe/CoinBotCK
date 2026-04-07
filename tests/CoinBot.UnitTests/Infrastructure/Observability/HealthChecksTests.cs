using CoinBot.Application.Abstractions.Alerts;
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
    public async Task MarketHealthCheck_ReturnsHealthy_WhenRuntimeMarketSnapshotIsFresh()
    {
        var now = new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(now);
        await using var dbContext = CreateDbContext();

        dbContext.ExchangeAccounts.Add(new ExchangeAccount
        {
            OwnerUserId = "user-001",
            ExchangeName = "Binance",
            DisplayName = "Main account",
            LastValidatedAt = now.UtcDateTime.AddHours(-2)
        });
        dbContext.HealthSnapshots.Add(CreateMarketWatchdogSnapshot(
            now.UtcDateTime.AddSeconds(-10),
            MonitoringHealthState.Healthy,
            detail: "State=Normal; Reason=None"));

        await dbContext.SaveChangesAsync();

        var marketHealthCheck = new MarketHealthCheck(
            dbContext,
            Options.Create(new MarketHealthOptions { ValidationFreshnessMinutes = 15 }),
            timeProvider);

        var result = await marketHealthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("Healthy", result.Data["snapshotHealthState"]);
        Assert.Equal("market-watchdog", result.Data["snapshotKey"]);
    }

    [Fact]
    public async Task MarketHealthCheck_ReturnsUnhealthy_WhenNoExchangeAccountExists()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        await using var dbContext = CreateDbContext();
        var marketHealthCheck = new MarketHealthCheck(
            dbContext,
            Options.Create(new MarketHealthOptions { ValidationFreshnessMinutes = 15 }),
            timeProvider);

        var result = await marketHealthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("missing", result.Data["snapshotKey"]);
    }

    [Fact]
    public async Task MarketHealthCheck_ReturnsUnhealthy_WhenRuntimeMarketSnapshotIsStale()
    {
        var now = new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(now);
        await using var dbContext = CreateDbContext();

        dbContext.ExchangeAccounts.Add(new ExchangeAccount
        {
            OwnerUserId = "user-001",
            ExchangeName = "Binance",
            DisplayName = "Main account",
            LastValidatedAt = now.UtcDateTime
        });
        dbContext.HealthSnapshots.Add(CreateMarketWatchdogSnapshot(
            now.UtcDateTime.AddMinutes(-20),
            MonitoringHealthState.Healthy,
            detail: "State=Normal; Reason=None"));

        await dbContext.SaveChangesAsync();

        var marketHealthCheck = new MarketHealthCheck(
            dbContext,
            Options.Create(new MarketHealthOptions { ValidationFreshnessMinutes = 15 }),
            timeProvider);

        var result = await marketHealthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Healthy", result.Data["snapshotHealthState"]);
    }

    [Fact]
    public async Task MarketHealthCheck_ReturnsUnhealthy_WhenRuntimeMarketSnapshotIsDegraded()
    {
        var now = new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(now);
        await using var dbContext = CreateDbContext();

        dbContext.ExchangeAccounts.Add(new ExchangeAccount
        {
            OwnerUserId = "user-001",
            ExchangeName = "Binance",
            DisplayName = "Main account",
            LastValidatedAt = now.UtcDateTime
        });
        dbContext.HealthSnapshots.Add(CreateMarketWatchdogSnapshot(
            now.UtcDateTime.AddSeconds(-10),
            MonitoringHealthState.Degraded,
            detail: "State=Degraded; Reason=MarketDataLatencyBreached"));

        await dbContext.SaveChangesAsync();

        var marketHealthCheck = new MarketHealthCheck(
            dbContext,
            Options.Create(new MarketHealthOptions { ValidationFreshnessMinutes = 15 }),
            timeProvider);

        var result = await marketHealthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Degraded", result.Data["snapshotHealthState"]);
    }

    [Fact]
    public async Task DataLatencyHealthCheck_ReturnsDegraded_WhenMarketDataAgeReachesThreeSeconds()
    {
        var now = new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(now);
        await using var dbContext = CreateDbContext();
        var alertService = new FakeAlertService();
        var correlationContextAccessor = new CorrelationContextAccessor();
        var circuitBreaker = new DataLatencyCircuitBreaker(
            dbContext,
            alertService,
            Options.Create(new DataLatencyGuardOptions()),
            timeProvider,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DataLatencyCircuitBreaker>.Instance);

        await circuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat("binance-btcusdt", now.UtcDateTime),
            correlationId: "corr-health-latency-001");

        timeProvider.Advance(TimeSpan.FromSeconds(3));

        var healthCheck = new DataLatencyHealthCheck(circuitBreaker, correlationContextAccessor);
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("Degraded", result.Data["stateCode"]);
        Assert.Equal("MarketDataLatencyBreached", result.Data["reasonCode"]);
        Assert.Equal("3000", result.Data["latestDataAgeMilliseconds"]);
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

    private static HealthSnapshot CreateMarketWatchdogSnapshot(
        DateTime lastUpdatedAtUtc,
        MonitoringHealthState healthState,
        string detail)
    {
        return new HealthSnapshot
        {
            SnapshotKey = "market-watchdog",
            SentinelName = "MarketWatchdog",
            DisplayName = "Market Watchdog",
            HealthState = healthState,
            FreshnessTier = MonitoringFreshnessTier.Hot,
            CircuitBreakerState = healthState == MonitoringHealthState.Healthy
                ? CircuitBreakerStateCode.Closed
                : CircuitBreakerStateCode.Degraded,
            LastUpdatedAtUtc = lastUpdatedAtUtc,
            ObservedAtUtc = lastUpdatedAtUtc,
            WorkerLastHeartbeatAtUtc = lastUpdatedAtUtc,
            Detail = detail
        };
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

    private sealed class FakeAlertService : IAlertService
    {
        public Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
