using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Dashboard;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Dashboard;

public sealed class UserDashboardOperationsReadModelServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_ComputesOperationalSummary_ForEnabledBotsCooldownsAndHealth()
    {
        await using var dbContext = CreateDbContext();
        var now = DateTime.UtcNow;
        var ownerUserId = "user-dashboard-ops";
        var exchangeAccountId = Guid.NewGuid();
        var btcBotId = Guid.NewGuid();
        var ethBotId = Guid.NewGuid();
        var duplicateBtcBotId = Guid.NewGuid();

        dbContext.TradingBots.AddRange(
            new TradingBot
            {
                Id = btcBotId,
                OwnerUserId = ownerUserId,
                Name = "BTC Bot",
                StrategyKey = "btc-core",
                Symbol = "BTCUSDT",
                IsEnabled = true
            },
            new TradingBot
            {
                Id = ethBotId,
                OwnerUserId = ownerUserId,
                Name = "ETH Bot",
                StrategyKey = "eth-core",
                Symbol = "ETHUSDT",
                IsEnabled = true
            },
            new TradingBot
            {
                Id = duplicateBtcBotId,
                OwnerUserId = ownerUserId,
                Name = "BTC Bot 2",
                StrategyKey = "btc-core-2",
                Symbol = "BTCUSDT",
                IsEnabled = true
            });

        dbContext.BackgroundJobStates.Add(new BackgroundJobState
        {
            Id = Guid.NewGuid(),
            BotId = btcBotId,
            JobKey = "bot:btc",
            JobType = BackgroundJobTypes.BotExecution,
            Status = BackgroundJobStatus.Failed,
            LastHeartbeatAtUtc = now.AddSeconds(-20),
            LastErrorCode = "SymbolTrackingUnavailable",
            NextRunAtUtc = now
        });

        dbContext.ExecutionOrders.Add(
            new ExecutionOrder
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                TradingStrategyId = Guid.NewGuid(),
                TradingStrategyVersionId = Guid.NewGuid(),
                StrategySignalId = Guid.NewGuid(),
                SignalType = StrategySignalType.Entry,
                StrategyKey = "btc-core",
                Symbol = "BTCUSDT",
                Timeframe = "1m",
                BaseAsset = "BTC",
                QuoteAsset = "USDT",
                Side = ExecutionOrderSide.Buy,
                OrderType = ExecutionOrderType.Market,
                Quantity = 0.002m,
                Price = 65000m,
                BotId = btcBotId,
                ExecutionEnvironment = ExecutionEnvironment.Live,
                ExecutorKind = ExecutionOrderExecutorKind.Binance,
                State = ExecutionOrderState.Submitted,
                IdempotencyKey = "btc-recent",
                RootCorrelationId = "corr-btc",
                CreatedDate = now.AddSeconds(-30),
                LastStateChangedAtUtc = now.AddSeconds(-30)
            });
        await dbContext.SaveChangesAsync();

        dbContext.ExecutionOrders.Add(
            new ExecutionOrder
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                TradingStrategyId = Guid.NewGuid(),
                TradingStrategyVersionId = Guid.NewGuid(),
                StrategySignalId = Guid.NewGuid(),
                SignalType = StrategySignalType.Entry,
                StrategyKey = "eth-core",
                Symbol = "ETHUSDT",
                Timeframe = "1m",
                BaseAsset = "ETH",
                QuoteAsset = "USDT",
                Side = ExecutionOrderSide.Buy,
                OrderType = ExecutionOrderType.Market,
                Quantity = 0.02m,
                Price = 3000m,
                BotId = ethBotId,
                ExecutionEnvironment = ExecutionEnvironment.Live,
                ExecutorKind = ExecutionOrderExecutorKind.Binance,
                State = ExecutionOrderState.Rejected,
                FailureCode = "UserExecutionBotCooldownActive",
                IdempotencyKey = "eth-recent",
                RootCorrelationId = "corr-eth",
                CreatedDate = now,
                LastStateChangedAtUtc = now
            });

        dbContext.WorkerHeartbeats.AddRange(
            new WorkerHeartbeat
            {
                Id = Guid.NewGuid(),
                WorkerKey = "job-orchestration",
                WorkerName = "Job Orchestration",
                HealthState = MonitoringHealthState.Healthy,
                FreshnessTier = MonitoringFreshnessTier.Hot,
                CircuitBreakerState = CircuitBreakerStateCode.Closed,
                LastHeartbeatAtUtc = now.AddSeconds(-5),
                LastUpdatedAtUtc = now.AddSeconds(-5)
            },
            new WorkerHeartbeat
            {
                Id = Guid.NewGuid(),
                WorkerKey = "exchange-private-stream",
                WorkerName = "Private Stream",
                HealthState = MonitoringHealthState.Warning,
                FreshnessTier = MonitoringFreshnessTier.Warm,
                CircuitBreakerState = CircuitBreakerStateCode.Cooldown,
                LastHeartbeatAtUtc = now.AddSeconds(-40),
                LastUpdatedAtUtc = now.AddSeconds(-40),
                LastErrorCode = "ListenKeyExpired"
            });

        dbContext.HealthSnapshots.Add(new HealthSnapshot
        {
            Id = Guid.NewGuid(),
            SnapshotKey = "clock-drift-monitor",
            SentinelName = "ClockDriftMonitor",
            DisplayName = "Clock Drift Monitor",
            HealthState = MonitoringHealthState.Healthy,
            FreshnessTier = MonitoringFreshnessTier.Hot,
            CircuitBreakerState = CircuitBreakerStateCode.Closed,
            LastUpdatedAtUtc = now,
            ObservedAtUtc = now,
            Detail = "ClockDriftMs=80; LocalClockUtc=2026-04-02T14:28:03.1196593Z; ExchangeServerTimeUtc=2026-04-02T14:28:03.2000000Z; Probe=Succeeded"
        });

        dbContext.DegradedModeStates.Add(new DegradedModeState
        {
            Id = DegradedModeDefaults.SingletonId,
            StateCode = DegradedModeStateCode.Stopped,
            ReasonCode = DegradedModeReasonCode.ClockDriftExceeded,
            SignalFlowBlocked = true,
            ExecutionFlowBlocked = true,
            LatestDataTimestampAtUtc = now.AddSeconds(-3),
            LatestHeartbeatReceivedAtUtc = now,
            LatestClockDriftMilliseconds = 2234,
            LastStateChangedAtUtc = now
        });

        dbContext.DependencyCircuitBreakerStates.Add(new DependencyCircuitBreakerState
        {
            Id = Guid.NewGuid(),
            BreakerKind = DependencyCircuitBreakerKind.WebSocket,
            StateCode = CircuitBreakerStateCode.Cooldown,
            LastErrorCode = "SocketClosed"
        });

        dbContext.RiskProfiles.Add(new RiskProfile
        {
            OwnerUserId = ownerUserId,
            ProfileName = "Pilot",
            MaxDailyLossPercentage = 5m,
            MaxPositionSizePercentage = 100m,
            MaxLeverage = 2m
        });

        dbContext.ExchangeBalances.AddRange(
            new ExchangeBalance
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                ExchangeAccountId = exchangeAccountId,
                Asset = "USDT",
                WalletBalance = 1000m,
                CrossWalletBalance = 1000m,
                AvailableBalance = 1000m,
                ExchangeUpdatedAtUtc = now,
                SyncedAtUtc = now
            },
            new ExchangeBalance
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                ExchangeAccountId = exchangeAccountId,
                Asset = "BUSD",
                WalletBalance = 500m,
                CrossWalletBalance = 500m,
                AvailableBalance = 500m,
                ExchangeUpdatedAtUtc = now,
                SyncedAtUtc = now
            });

        dbContext.ExchangePositions.AddRange(
            new ExchangePosition
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                ExchangeAccountId = exchangeAccountId,
                Symbol = "BTCUSDT",
                PositionSide = "LONG",
                Quantity = 0.02m,
                EntryPrice = 65000m,
                BreakEvenPrice = 65010m,
                UnrealizedProfit = -75m,
                MarginType = "ISOLATED",
                IsolatedWallet = 100m,
                ExchangeUpdatedAtUtc = now,
                SyncedAtUtc = now
            },
            new ExchangePosition
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                ExchangeAccountId = exchangeAccountId,
                Symbol = "ETHUSDT",
                PositionSide = "LONG",
                Quantity = 0.5m,
                EntryPrice = 3000m,
                BreakEvenPrice = 3005m,
                UnrealizedProfit = -25m,
                MarginType = "ISOLATED",
                IsolatedWallet = 80m,
                ExchangeUpdatedAtUtc = now,
                SyncedAtUtc = now
            });

        await dbContext.SaveChangesAsync();

        var service = new UserDashboardOperationsReadModelService(
            dbContext,
            Options.Create(new BotExecutionPilotOptions
            {
                DefaultSymbol = "BTCUSDT",
                AllowedSymbols = ["BTCUSDT", "ETHUSDT", "SOLUSDT"],
                PerBotCooldownSeconds = 120,
                PerSymbolCooldownSeconds = 60,
                MaxOpenPositionsPerUser = 3
            }),
            Options.Create(new DataLatencyGuardOptions
            {
                ClockDriftThresholdSeconds = 2,
                StaleDataThresholdSeconds = 3,
                StopDataThresholdSeconds = 6
            }));

        var snapshot = await service.GetSnapshotAsync(ownerUserId, CancellationToken.None);

        Assert.Equal(3, snapshot.EnabledBotCount);
        Assert.Equal(2, snapshot.EnabledSymbolCount);
        Assert.Equal(1, snapshot.ConflictedSymbolCount);
        Assert.Equal("Failed", snapshot.LastJobStatus);
        Assert.Equal("SymbolTrackingUnavailable", snapshot.LastJobErrorCode);
        Assert.Equal("Rejected", snapshot.LastExecutionState);
        Assert.Equal("UserExecutionBotCooldownActive", snapshot.LastExecutionFailureCode);
        Assert.Equal("Healthy", snapshot.WorkerHealthLabel);
        Assert.Equal("positive", snapshot.WorkerHealthTone);
        Assert.Equal("Warning", snapshot.PrivateStreamHealthLabel);
        Assert.Equal("warning", snapshot.PrivateStreamHealthTone);
        Assert.Equal("1 active", snapshot.BreakerLabel);
        Assert.Equal("warning", snapshot.BreakerTone);
        Assert.Equal(1, snapshot.OpenCircuitBreakerCount);
        Assert.InRange(snapshot.CurrentDailyLossPercentage ?? 0m, 6.6m, 6.7m);
        Assert.Equal(5m, snapshot.MaxDailyLossPercentage);
        Assert.Equal(2, snapshot.OpenPositionCount);
        Assert.Equal(3, snapshot.MaxOpenPositions);
        Assert.Equal(2, snapshot.ActiveBotCooldownCount);
        Assert.Equal(2, snapshot.ActiveSymbolCooldownCount);
        Assert.NotNull(snapshot.LastExecutionAtUtc);
        Assert.Contains("2234 / 2000 ms", snapshot.DriftSummary, StringComparison.Ordinal);
        Assert.Contains("market-data heartbeat", snapshot.DriftReason, StringComparison.OrdinalIgnoreCase);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private sealed class TestDataScopeContext : CoinBot.Application.Abstractions.DataScope.IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }
}
