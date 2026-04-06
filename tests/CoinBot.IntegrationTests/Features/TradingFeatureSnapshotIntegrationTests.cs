using CoinBot.Application.Abstractions.Alerts;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Features;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Features;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using CoinBot.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.IntegrationTests.Features;

public sealed class TradingFeatureSnapshotIntegrationTests
{
    [Fact]
    public async Task CaptureAsync_PersistsSnapshotsAndReadsBackBySymbolTimeframe_OnSqlServer()
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString($"CoinBotFeatureStoreInt_{Guid.NewGuid():N}");
        const string userId = "feature-sql-user-01";
        var botId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();

        await using var harness = CreateHarness(connectionString, userId);

        try
        {
            await harness.DbContext.Database.EnsureDeletedAsync();
            await harness.DbContext.Database.MigrateAsync();
            await SeedFeatureGraphAsync(harness.DbContext, userId, botId, exchangeAccountId);

            var firstEvaluatedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;
            var oneMinuteCandles = CreateCandles("BTCUSDT", "1m", firstEvaluatedAtUtc.AddMinutes(-240), 240, 65000m, 5m, 110m);
            await RecordFreshHeartbeatAsync(harness.CircuitBreaker, oneMinuteCandles[^1], "BTCUSDT", "1m");
            var firstSnapshot = await harness.Service.CaptureAsync(
                new TradingFeatureCaptureRequest(
                    userId,
                    botId,
                    "feature-store",
                    "BTCUSDT",
                    "1m",
                    firstEvaluatedAtUtc,
                    exchangeAccountId,
                    ExchangeDataPlane.Futures,
                    HistoricalCandles: oneMinuteCandles),
                CancellationToken.None);

            harness.TimeProvider.Advance(TimeSpan.FromMinutes(5));
            var secondEvaluatedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;
            var fiveMinuteCandles = CreateCandles("BTCUSDT", "5m", secondEvaluatedAtUtc.AddMinutes(-1200), 240, 65100m, 8m, 130m);
            await RecordFreshHeartbeatAsync(harness.CircuitBreaker, fiveMinuteCandles[^1], "BTCUSDT", "5m");
            var secondSnapshot = await harness.Service.CaptureAsync(
                new TradingFeatureCaptureRequest(
                    userId,
                    botId,
                    "feature-store",
                    "BTCUSDT",
                    "5m",
                    secondEvaluatedAtUtc,
                    exchangeAccountId,
                    ExchangeDataPlane.Futures,
                    HistoricalCandles: fiveMinuteCandles),
                CancellationToken.None);

            var latestOneMinute = await harness.Service.GetLatestAsync(userId, botId, "BTCUSDT", "1m", CancellationToken.None);
            var recentFiveMinute = await harness.Service.ListRecentAsync(userId, botId, "BTCUSDT", "5m", cancellationToken: CancellationToken.None);
            var persistedRows = await harness.DbContext.TradingFeatureSnapshots
                .AsNoTracking()
                .OrderBy(item => item.Timeframe)
                .ToListAsync();

            Assert.NotNull(latestOneMinute);
            Assert.Equal(firstSnapshot.Id, latestOneMinute!.Id);
            Assert.Equal(firstEvaluatedAtUtc, latestOneMinute.EvaluatedAtUtc);
            Assert.Equal("AI-1.v1", latestOneMinute.FeatureVersion);
            Assert.Equal(FeatureSnapshotState.Ready, latestOneMinute.SnapshotState);
            Assert.Single(recentFiveMinute);
            Assert.Equal(secondSnapshot.Id, recentFiveMinute.Single().Id);
            Assert.Equal(secondEvaluatedAtUtc, recentFiveMinute.Single().EvaluatedAtUtc);
            Assert.Equal(2, persistedRows.Count);
            Assert.Contains(persistedRows, item => item.Timeframe == "1m" && item.Symbol == "BTCUSDT" && item.EvaluatedAtUtc == firstEvaluatedAtUtc);
            Assert.Contains(persistedRows, item => item.Timeframe == "5m" && item.Symbol == "BTCUSDT" && item.EvaluatedAtUtc == secondEvaluatedAtUtc);
        }
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task CaptureAsync_PersistsExplicitStaleState_WhenMarketDataFreshnessIsBreached_OnSqlServer()
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString($"CoinBotFeatureStoreStaleInt_{Guid.NewGuid():N}");
        const string userId = "feature-sql-user-02";
        var botId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();

        await using var harness = CreateHarness(connectionString, userId);

        try
        {
            await harness.DbContext.Database.EnsureDeletedAsync();
            await harness.DbContext.Database.MigrateAsync();
            await SeedFeatureGraphAsync(harness.DbContext, userId, botId, exchangeAccountId);

            var evaluatedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;
            var candles = CreateCandles("BTCUSDT", "1m", evaluatedAtUtc.AddMinutes(-240), 240, 65000m, 2m, 100m);
            await RecordFreshHeartbeatAsync(harness.CircuitBreaker, candles[^1], "BTCUSDT", "1m");
            harness.TimeProvider.Advance(TimeSpan.FromSeconds(10));

            var snapshot = await harness.Service.CaptureAsync(
                new TradingFeatureCaptureRequest(
                    userId,
                    botId,
                    "feature-store",
                    "BTCUSDT",
                    "1m",
                    harness.TimeProvider.GetUtcNow().UtcDateTime,
                    exchangeAccountId,
                    ExchangeDataPlane.Futures,
                    HistoricalCandles: candles),
                CancellationToken.None);

            var persistedSnapshot = await harness.DbContext.TradingFeatureSnapshots.SingleAsync();

            Assert.Equal(FeatureSnapshotState.Stale, snapshot.SnapshotState);
            Assert.Equal(DegradedModeReasonCode.MarketDataLatencyCritical, snapshot.MarketDataReasonCode);
            Assert.Equal(FeatureSnapshotState.Stale, persistedSnapshot.SnapshotState);
            Assert.Equal(DegradedModeReasonCode.MarketDataLatencyCritical, persistedSnapshot.MarketDataReasonCode);
            Assert.Contains("State=Stale", snapshot.FeatureSummary, StringComparison.Ordinal);
            Assert.Contains("MarketDataStale", snapshot.TopSignalHints, StringComparison.Ordinal);
        }
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }

    private static TradingFeatureHarness CreateHarness(string connectionString, string userId)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 6, 12, 0, 0, TimeSpan.Zero));
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext(userId, hasIsolationBypass: false));
        var circuitBreaker = new DataLatencyCircuitBreaker(
            dbContext,
            new FakeAlertService(),
            Options.Create(new DataLatencyGuardOptions()),
            timeProvider,
            NullLogger<DataLatencyCircuitBreaker>.Instance);
        var tradingModeService = new TradingModeService(dbContext, new NoopAuditLogService());
        var service = new TradingFeatureSnapshotService(
            dbContext,
            circuitBreaker,
            tradingModeService,
            new FakeHistoricalKlineClient(),
            Options.Create(new BotExecutionPilotOptions()),
            timeProvider,
            NullLogger<TradingFeatureSnapshotService>.Instance);

        return new TradingFeatureHarness(dbContext, service, circuitBreaker, timeProvider);
    }

    private static async Task SeedFeatureGraphAsync(ApplicationDbContext dbContext, string userId, Guid botId, Guid exchangeAccountId)
    {
        dbContext.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = userId,
            NormalizedUserName = userId.ToUpperInvariant(),
            Email = $"{userId}@coinbot.test",
            NormalizedEmail = $"{userId.ToUpperInvariant()}@COINBOT.TEST",
            FullName = userId,
            EmailConfirmed = true
        });
        dbContext.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = userId,
            ExchangeName = "Binance",
            DisplayName = "Feature SQL Futures",
            CredentialStatus = ExchangeCredentialStatus.Active,
            IsReadOnly = false
        });
        dbContext.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = userId,
            Name = "Feature SQL Bot",
            StrategyKey = "feature-store",
            Symbol = "BTCUSDT",
            ExchangeAccountId = exchangeAccountId,
            IsEnabled = true
        });

        await dbContext.SaveChangesAsync();
    }

    private static Task RecordFreshHeartbeatAsync(IDataLatencyCircuitBreaker circuitBreaker, MarketCandleSnapshot candle, string symbol, string timeframe)
    {
        return circuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "feature-int",
                candle.CloseTimeUtc,
                Symbol: symbol,
                Timeframe: timeframe,
                ExpectedOpenTimeUtc: candle.CloseTimeUtc.AddMilliseconds(1),
                ContinuityGapCount: 0),
            cancellationToken: CancellationToken.None);
    }

    private static MarketCandleSnapshot[] CreateCandles(string symbol, string timeframe, DateTime startOpenTimeUtc, int count, decimal startPrice, decimal driftPerCandle, decimal startVolume)
    {
        var interval = ResolveIntervalDuration(timeframe);
        return Enumerable.Range(0, count)
            .Select(index =>
            {
                var openTimeUtc = startOpenTimeUtc.AddTicks(interval.Ticks * index);
                var closeTimeUtc = openTimeUtc.Add(interval).AddMilliseconds(-1);
                var openPrice = startPrice + (driftPerCandle * index);
                var closePrice = openPrice + (driftPerCandle * 0.6m);
                return new MarketCandleSnapshot(
                    symbol,
                    timeframe,
                    openTimeUtc,
                    closeTimeUtc,
                    openPrice,
                    closePrice + 6m,
                    openPrice - 6m,
                    closePrice,
                    startVolume + (index % 11),
                    true,
                    closeTimeUtc,
                    "integration-feature");
            })
            .ToArray();
    }

    private static TimeSpan ResolveIntervalDuration(string timeframe)
    {
        return timeframe switch
        {
            "1m" => TimeSpan.FromMinutes(1),
            "5m" => TimeSpan.FromMinutes(5),
            _ => throw new InvalidOperationException($"Unsupported timeframe '{timeframe}'.")
        };
    }

    private sealed record TradingFeatureHarness(
        ApplicationDbContext DbContext,
        TradingFeatureSnapshotService Service,
        DataLatencyCircuitBreaker CircuitBreaker,
        AdjustableTimeProvider TimeProvider) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => DbContext.DisposeAsync();
    }

    private sealed class TestDataScopeContext(string? userId, bool hasIsolationBypass) : IDataScopeContext
    {
        public string? UserId => userId;
        public bool HasIsolationBypass => hasIsolationBypass;
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset currentUtcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => currentUtcNow;
        public void Advance(TimeSpan timeSpan) => currentUtcNow = currentUtcNow.Add(timeSpan);
    }

    private sealed class FakeHistoricalKlineClient : IBinanceHistoricalKlineClient
    {
        public Task<IReadOnlyCollection<MarketCandleSnapshot>> GetClosedCandlesAsync(string symbol, string interval, DateTime startOpenTimeUtc, DateTime endOpenTimeUtc, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<MarketCandleSnapshot>>(Array.Empty<MarketCandleSnapshot>());
    }

    private sealed class FakeAlertService : IAlertService
    {
        public Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopAuditLogService : IAuditLogService
    {
        public Task WriteAsync(AuditLogWriteRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

