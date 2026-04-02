using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Dashboard;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Persistence;
using CoinBot.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoinBot.IntegrationTests.Execution;

public sealed class ClockExecutionIntegrationTests
{
    [Fact]
    [Trait("Scope", "Clock")]
    public async Task UserDashboardOperationsReadModelService_ProjectsPersistedClockDriftSummary_OnSqlServer()
    {
        var databaseName = $"CoinBot_ClockSummaryInt_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var ownerUserId = Guid.NewGuid().ToString("N");
        var now = new DateTime(2026, 4, 2, 14, 28, 10, DateTimeKind.Utc);

        try
        {
            await using var setupContext = CreateDbContext(connectionString, ownerUserId, hasIsolationBypass: true);
            await setupContext.Database.EnsureDeletedAsync();
            await setupContext.Database.MigrateAsync();

            setupContext.Users.Add(new ApplicationUser
            {
                Id = ownerUserId,
                UserName = $"clock.{ownerUserId}@coinbot.test",
                Email = $"clock.{ownerUserId}@coinbot.test",
                FullName = "Clock Integration User",
                EmailConfirmed = true
            });
            setupContext.HealthSnapshots.Add(new HealthSnapshot
            {
                Id = Guid.NewGuid(),
                SnapshotKey = "clock-drift-monitor",
                SentinelName = "ClockDriftMonitor",
                DisplayName = "Clock Drift Monitor",
                HealthState = MonitoringHealthState.Healthy,
                FreshnessTier = MonitoringFreshnessTier.Hot,
                CircuitBreakerState = CircuitBreakerStateCode.Cooldown,
                LastUpdatedAtUtc = now,
                ObservedAtUtc = now,
                Detail = "ClockDriftMs=80; LocalClockUtc=2026-04-02T14:28:03.1196593Z; ExchangeServerTimeUtc=2026-04-02T14:28:03.2000000Z; Probe=Succeeded"
            });
            setupContext.DegradedModeStates.Add(new DegradedModeState
            {
                Id = Guid.NewGuid(),
                StateCode = DegradedModeStateCode.Stopped,
                ReasonCode = DegradedModeReasonCode.ClockDriftExceeded,
                SignalFlowBlocked = true,
                ExecutionFlowBlocked = true,
                LatestDataTimestampAtUtc = now.AddMilliseconds(-2234),
                LatestHeartbeatReceivedAtUtc = now,
                LatestClockDriftMilliseconds = 2234,
                LastStateChangedAtUtc = now
            });

            await setupContext.SaveChangesAsync();

            await using var verifyContext = CreateDbContext(connectionString, ownerUserId);
            var service = CreateReadModelService(verifyContext);

            var snapshot = await service.GetSnapshotAsync(ownerUserId, CancellationToken.None);

            Assert.Contains("2234 / 2000 ms", snapshot.DriftSummary, StringComparison.Ordinal);
            Assert.Contains("market-data heartbeat", snapshot.DriftReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }

    [Fact]
    [Trait("Scope", "Execution")]
    public async Task UserDashboardOperationsReadModelService_ProjectsLatestExecutionFailure_OnSqlServer()
    {
        var databaseName = $"CoinBot_ExecutionSummaryInt_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var ownerUserId = Guid.NewGuid().ToString("N");
        var botId = Guid.NewGuid();
        var now = new DateTime(2026, 4, 2, 14, 28, 10, DateTimeKind.Utc);

        try
        {
            await using var setupContext = CreateDbContext(connectionString, ownerUserId, hasIsolationBypass: true);
            await setupContext.Database.EnsureDeletedAsync();
            await setupContext.Database.MigrateAsync();

            setupContext.Users.Add(new ApplicationUser
            {
                Id = ownerUserId,
                UserName = $"execution.{ownerUserId}@coinbot.test",
                Email = $"execution.{ownerUserId}@coinbot.test",
                FullName = "Execution Integration User",
                EmailConfirmed = true
            });
            setupContext.TradingBots.Add(new TradingBot
            {
                Id = botId,
                OwnerUserId = ownerUserId,
                Name = "Execution Bot",
                StrategyKey = "execution-core",
                Symbol = "BTCUSDT",
                IsEnabled = true
            });
            setupContext.ExecutionOrders.Add(new ExecutionOrder
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                TradingStrategyId = Guid.NewGuid(),
                TradingStrategyVersionId = Guid.NewGuid(),
                StrategySignalId = Guid.NewGuid(),
                SignalType = StrategySignalType.Entry,
                StrategyKey = "execution-core",
                Symbol = "BTCUSDT",
                Timeframe = "1m",
                BaseAsset = "BTC",
                QuoteAsset = "USDT",
                Side = ExecutionOrderSide.Buy,
                OrderType = ExecutionOrderType.Market,
                Quantity = 0.001m,
                Price = 65000m,
                BotId = botId,
                ExecutionEnvironment = ExecutionEnvironment.Live,
                ExecutorKind = ExecutionOrderExecutorKind.Binance,
                State = ExecutionOrderState.Rejected,
                FailureCode = "ClockDriftExceeded",
                FailureDetail = "Execution blocked because clock drift exceeded the safety threshold.",
                IdempotencyKey = "execution-int-001",
                RootCorrelationId = "corr-execution-int-001",
                CreatedDate = now,
                LastStateChangedAtUtc = now
            });

            await setupContext.SaveChangesAsync();

            await using var verifyContext = CreateDbContext(connectionString, ownerUserId);
            var service = CreateReadModelService(verifyContext);

            var snapshot = await service.GetSnapshotAsync(ownerUserId, CancellationToken.None);

            Assert.Equal("Rejected", snapshot.LastExecutionState);
            Assert.Equal("ClockDriftExceeded", snapshot.LastExecutionFailureCode);
            Assert.Equal(1, snapshot.EnabledBotCount);
        }
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }

    private static ApplicationDbContext CreateDbContext(string connectionString, string? userId, bool hasIsolationBypass = false)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        return new ApplicationDbContext(options, new TestDataScopeContext(userId, hasIsolationBypass));
    }

    private static UserDashboardOperationsReadModelService CreateReadModelService(ApplicationDbContext dbContext)
    {
        return new UserDashboardOperationsReadModelService(
            dbContext,
            Options.Create(new BotExecutionPilotOptions
            {
                DefaultSymbol = "BTCUSDT",
                AllowedSymbols = ["BTCUSDT", "ETHUSDT", "SOLUSDT"],
                MaxOpenPositionsPerUser = 3
            }),
            Options.Create(new DataLatencyGuardOptions
            {
                ClockDriftThresholdSeconds = 2,
                StaleDataThresholdSeconds = 3,
                StopDataThresholdSeconds = 6
            }));
    }

    private sealed class TestDataScopeContext(string? userId, bool hasIsolationBypass) : CoinBot.Application.Abstractions.DataScope.IDataScopeContext
    {
        public string? UserId => userId;

        public bool HasIsolationBypass => hasIsolationBypass;
    }
}
