using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

using HealthSnapshotEntity = CoinBot.Domain.Entities.HealthSnapshot;

namespace CoinBot.UnitTests.Infrastructure.Administration;

public sealed class AdminMonitoringReadModelServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_MapsRedisLatencyAndClockDriftMetrics_FromReadModel()
    {
        var now = new DateTime(2026, 3, 24, 12, 30, 0, DateTimeKind.Utc);
        await using var dbContext = CreateDbContext();

        dbContext.HealthSnapshots.AddRange(
            new HealthSnapshotEntity
            {
                Id = Guid.NewGuid(),
                SnapshotKey = "dependency-health-monitor",
                SentinelName = "DependencyHealthMonitor",
                DisplayName = "Dependency Health Monitor",
                HealthState = MonitoringHealthState.Degraded,
                FreshnessTier = MonitoringFreshnessTier.Hot,
                CircuitBreakerState = CircuitBreakerStateCode.Cooldown,
                LastUpdatedAtUtc = now,
                ObservedAtUtc = now,
                DbLatencyMs = 6,
                RedisLatencyMs = 17,
                Detail = "DbLatencyMs=6; RedisLatencyMs=17; RedisProbe=Failed; RedisEndpoint=127.0.0.1:6379"
            },
            new HealthSnapshotEntity
            {
                Id = Guid.NewGuid(),
                SnapshotKey = "clock-drift-monitor",
                SentinelName = "ClockDriftMonitor",
                DisplayName = "Clock Drift Monitor",
                HealthState = MonitoringHealthState.Critical,
                FreshnessTier = MonitoringFreshnessTier.Hot,
                CircuitBreakerState = CircuitBreakerStateCode.Cooldown,
                LastUpdatedAtUtc = now,
                ObservedAtUtc = now,
                Detail = "ClockDriftMs=5000; Reason=ClockDriftExceeded"
            });

        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System);

        var snapshot = await service.GetSnapshotAsync();

        var dependencySnapshot = Assert.Single(snapshot.HealthSnapshots, item => item.SnapshotKey == "dependency-health-monitor");
        var clockDriftSnapshot = Assert.Single(snapshot.HealthSnapshots, item => item.SnapshotKey == "clock-drift-monitor");

        Assert.Equal(17, dependencySnapshot.Metrics.RedisLatencyMs);
        Assert.Equal(5000, clockDriftSnapshot.Metrics.ClockDriftMs);
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
