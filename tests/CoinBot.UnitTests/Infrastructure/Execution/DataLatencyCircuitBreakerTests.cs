using CoinBot.Application.Abstractions.Alerts;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Execution;

public sealed class DataLatencyCircuitBreakerTests
{
    [Fact]
    public async Task GetSnapshotAsync_ReturnsStopped_WhenNoHeartbeatExists()
    {
        await using var harness = CreateHarness();

        var snapshot = await harness.CircuitBreaker.GetSnapshotAsync(correlationId: "corr-latency-001");

        Assert.Equal(DegradedModeStateCode.Stopped, snapshot.StateCode);
        Assert.Equal(DegradedModeReasonCode.MarketDataUnavailable, snapshot.ReasonCode);
        Assert.True(snapshot.SignalFlowBlocked);
        Assert.True(snapshot.ExecutionFlowBlocked);
        Assert.Null(snapshot.LatestDataTimestampAtUtc);
        Assert.Empty(harness.AlertService.Notifications);
    }

    [Fact]
    public async Task GetSnapshotAsync_TransitionsToDegraded_WhenDataAgeReachesThreeSeconds()
    {
        await using var harness = CreateHarness();

        var initialSnapshot = await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat("binance-btcusdt", harness.TimeProvider.GetUtcNow().UtcDateTime),
            correlationId: "corr-latency-002");

        harness.TimeProvider.Advance(TimeSpan.FromSeconds(4));

        var degradedSnapshot = await harness.CircuitBreaker.GetSnapshotAsync(correlationId: "corr-latency-003");

        Assert.Equal(DegradedModeStateCode.Normal, initialSnapshot.StateCode);
        Assert.Equal(DegradedModeStateCode.Degraded, degradedSnapshot.StateCode);
        Assert.Equal(DegradedModeReasonCode.MarketDataLatencyBreached, degradedSnapshot.ReasonCode);
        Assert.True(degradedSnapshot.SignalFlowBlocked);
        Assert.True(degradedSnapshot.ExecutionFlowBlocked);
        Assert.Equal(4000, degradedSnapshot.LatestDataAgeMilliseconds);
        Assert.Contains(harness.AlertService.Notifications, notification => notification.Severity == AlertSeverity.Warning);
    }

    [Fact]
    public async Task GetSnapshotAsync_TransitionsToStopped_WhenDataAgeReachesStopThreshold()
    {
        await using var harness = CreateHarness();

        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat("binance-ethusdt", harness.TimeProvider.GetUtcNow().UtcDateTime),
            correlationId: "corr-latency-004");

        harness.TimeProvider.Advance(TimeSpan.FromSeconds(6));

        var stoppedSnapshot = await harness.CircuitBreaker.GetSnapshotAsync(correlationId: "corr-latency-005");

        Assert.Equal(DegradedModeStateCode.Stopped, stoppedSnapshot.StateCode);
        Assert.Equal(DegradedModeReasonCode.MarketDataLatencyCritical, stoppedSnapshot.ReasonCode);
        Assert.True(stoppedSnapshot.SignalFlowBlocked);
        Assert.True(stoppedSnapshot.ExecutionFlowBlocked);
        Assert.Equal(6000, stoppedSnapshot.LatestDataAgeMilliseconds);
        Assert.Contains(harness.AlertService.Notifications, notification => notification.Severity == AlertSeverity.Critical);
    }

    [Fact]
    public async Task RecordHeartbeatAsync_StopsImmediately_WhenClockDriftThresholdIsExceeded()
    {
        await using var harness = CreateHarness();

        var snapshot = await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "binance-solusdt",
                harness.TimeProvider.GetUtcNow().UtcDateTime.AddSeconds(-5)),
            correlationId: "corr-latency-006");

        Assert.Equal(DegradedModeStateCode.Stopped, snapshot.StateCode);
        Assert.Equal(DegradedModeReasonCode.ClockDriftExceeded, snapshot.ReasonCode);
        Assert.True(snapshot.SignalFlowBlocked);
        Assert.True(snapshot.ExecutionFlowBlocked);
        Assert.Equal(5000, snapshot.LatestClockDriftMilliseconds);
        Assert.Contains(harness.AlertService.Notifications, notification => notification.Severity == AlertSeverity.Critical);
    }

    [Fact]
    public async Task GetSnapshotAsync_KeepsContinuityGuardActive_UntilHealthyHeartbeatClearsIt()
    {
        await using var harness = CreateHarness();
        var nowUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;

        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "binance:kline",
                nowUtc,
                DegradedModeStateCode.Stopped,
                DegradedModeReasonCode.CandleDataDuplicateDetected),
            correlationId: "corr-latency-007");

        harness.TimeProvider.Advance(TimeSpan.FromSeconds(1));

        var persistedSnapshot = await harness.CircuitBreaker.GetSnapshotAsync(correlationId: "corr-latency-008");

        Assert.Equal(DegradedModeStateCode.Stopped, persistedSnapshot.StateCode);
        Assert.Equal(DegradedModeReasonCode.CandleDataDuplicateDetected, persistedSnapshot.ReasonCode);
        Assert.True(persistedSnapshot.SignalFlowBlocked);
        Assert.True(persistedSnapshot.ExecutionFlowBlocked);
        Assert.Equal(nowUtc, persistedSnapshot.LatestContinuityGapStartedAtUtc);
        Assert.Equal(nowUtc.AddSeconds(1), persistedSnapshot.LatestContinuityGapLastSeenAtUtc);
        Assert.Null(persistedSnapshot.LatestContinuityRecoveredAtUtc);

        var recoveredSnapshot = await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "binance:kline",
                harness.TimeProvider.GetUtcNow().UtcDateTime,
                DegradedModeStateCode.Normal,
                DegradedModeReasonCode.None),
            correlationId: "corr-latency-009");

        Assert.Equal(DegradedModeStateCode.Normal, recoveredSnapshot.StateCode);
        Assert.Equal(DegradedModeReasonCode.None, recoveredSnapshot.ReasonCode);
        Assert.False(recoveredSnapshot.SignalFlowBlocked);
        Assert.False(recoveredSnapshot.ExecutionFlowBlocked);
        Assert.Equal(nowUtc, recoveredSnapshot.LatestContinuityGapStartedAtUtc);
        Assert.Equal(nowUtc.AddSeconds(1), recoveredSnapshot.LatestContinuityGapLastSeenAtUtc);
        Assert.Equal(nowUtc.AddSeconds(1), recoveredSnapshot.LatestContinuityRecoveredAtUtc);
    }

    [Fact]
    public async Task GetSnapshotAsync_ReturnsSymbolScopedState_WithoutCrossSymbolContamination()
    {
        await using var harness = CreateHarness();
        var nowUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;

        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "binance:kline",
                nowUtc,
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                ExpectedOpenTimeUtc: nowUtc.AddMinutes(1),
                ContinuityGapCount: 0),
            correlationId: "corr-latency-btc");

        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "binance:kline",
                nowUtc.AddSeconds(-10),
                DegradedModeStateCode.Stopped,
                DegradedModeReasonCode.MarketDataLatencyCritical,
                Symbol: "ETHUSDT",
                Timeframe: "1m",
                ExpectedOpenTimeUtc: nowUtc,
                ContinuityGapCount: 0),
            correlationId: "corr-latency-eth");

        var btcSnapshot = await harness.CircuitBreaker.GetSnapshotAsync(
            correlationId: "corr-latency-btc-read",
            symbol: "BTCUSDT",
            timeframe: "1m");
        var ethSnapshot = await harness.CircuitBreaker.GetSnapshotAsync(
            correlationId: "corr-latency-eth-read",
            symbol: "ETHUSDT",
            timeframe: "1m");
        var globalSnapshot = await harness.CircuitBreaker.GetSnapshotAsync(
            correlationId: "corr-latency-global-read");
        var btcStateId = DegradedModeDefaults.ResolveStateId("BTCUSDT", "1m");
        var ethStateId = DegradedModeDefaults.ResolveStateId("ETHUSDT", "1m");

        Assert.Equal(DegradedModeStateCode.Normal, btcSnapshot.StateCode);
        Assert.Equal(DegradedModeReasonCode.None, btcSnapshot.ReasonCode);
        Assert.Equal("BTCUSDT", btcSnapshot.LatestSymbol);
        Assert.Equal("1m", btcSnapshot.LatestTimeframe);
        Assert.False(btcSnapshot.SignalFlowBlocked);
        Assert.False(btcSnapshot.ExecutionFlowBlocked);

        Assert.Equal(DegradedModeStateCode.Stopped, ethSnapshot.StateCode);
        Assert.Equal(DegradedModeReasonCode.ClockDriftExceeded, ethSnapshot.ReasonCode);
        Assert.Equal("ETHUSDT", ethSnapshot.LatestSymbol);
        Assert.Equal("1m", ethSnapshot.LatestTimeframe);
        Assert.True(ethSnapshot.SignalFlowBlocked);
        Assert.True(ethSnapshot.ExecutionFlowBlocked);

        Assert.Equal(DegradedModeStateCode.Stopped, globalSnapshot.StateCode);
        Assert.Equal(DegradedModeReasonCode.ClockDriftExceeded, globalSnapshot.ReasonCode);
        Assert.Equal("ETHUSDT", globalSnapshot.LatestSymbol);
        Assert.Equal("1m", globalSnapshot.LatestTimeframe);

        Assert.True(await harness.DbContext.DegradedModeStates.AnyAsync(entity => entity.Id == DegradedModeDefaults.SingletonId));
        Assert.True(await harness.DbContext.DegradedModeStates.AnyAsync(entity => entity.Id == btcStateId));
        Assert.True(await harness.DbContext.DegradedModeStates.AnyAsync(entity => entity.Id == ethStateId));
    }

    [Fact]
    public async Task GetSnapshotAsync_UsesSharedKlineCacheMetadata_ForSymbolScopedFreshnessDecision()
    {
        var sharedKline = new MarketCandleSnapshot(
            "btcusdt",
            "1m",
            new DateTime(2026, 3, 22, 11, 59, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 22, 11, 59, 59, 999, DateTimeKind.Utc),
            100m,
            101m,
            99m,
            100m,
            10m,
            true,
            new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
            "Binance.WebSocket.Kline");
        var marketDataService = new FakeMarketDataService(sharedKline);
        await using var harness = CreateHarness(marketDataService);

        var snapshot = await harness.CircuitBreaker.GetSnapshotAsync(
            correlationId: "corr-shared-kline-001",
            symbol: "BTCUSDT",
            timeframe: "1m");

        Assert.Equal(DegradedModeStateCode.Normal, snapshot.StateCode);
        Assert.Equal(DegradedModeReasonCode.None, snapshot.ReasonCode);
        Assert.False(snapshot.SignalFlowBlocked);
        Assert.False(snapshot.ExecutionFlowBlocked);
        Assert.Equal("Binance.WebSocket.Kline", snapshot.LatestHeartbeatSource);
        Assert.Equal("BTCUSDT", snapshot.LatestSymbol);
        Assert.Equal("1m", snapshot.LatestTimeframe);
        Assert.Equal(sharedKline.ReceivedAtUtc, snapshot.LatestDataTimestampAtUtc);
        Assert.Equal(sharedKline.ReceivedAtUtc, snapshot.LatestHeartbeatReceivedAtUtc);
        Assert.Equal(sharedKline.CloseTimeUtc.AddMilliseconds(1), snapshot.LatestExpectedOpenTimeUtc);
        Assert.Equal(1, marketDataService.SharedKlineReadCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_FailsClosedWithMarketDataUnavailable_WhenSharedKlineProviderIsUnavailable()
    {
        var marketDataService = new FakeMarketDataService(
            SharedMarketDataCacheReadResult<MarketCandleSnapshot>.ProviderUnavailable(
                "Redis unavailable."));
        await using var harness = CreateHarness(marketDataService);

        var snapshot = await harness.CircuitBreaker.GetSnapshotAsync(
            correlationId: "corr-shared-kline-002",
            symbol: "BTCUSDT",
            timeframe: "1m");

        Assert.Equal(DegradedModeStateCode.Stopped, snapshot.StateCode);
        Assert.Equal(DegradedModeReasonCode.MarketDataUnavailable, snapshot.ReasonCode);
        Assert.True(snapshot.SignalFlowBlocked);
        Assert.True(snapshot.ExecutionFlowBlocked);
        Assert.Equal("shared-cache:kline:ProviderUnavailable", snapshot.LatestHeartbeatSource);
        Assert.Equal("BTCUSDT", snapshot.LatestSymbol);
        Assert.Equal("1m", snapshot.LatestTimeframe);
        Assert.Null(snapshot.LatestDataTimestampAtUtc);
        Assert.Equal(1, marketDataService.SharedKlineReadCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_DoesNotEscalateSharedKlineAgeToClockDriftExceeded_WhenCachedHeartbeatIsRecent()
    {
        var sharedKline = new MarketCandleSnapshot(
            "btcusdt",
            "1m",
            new DateTime(2026, 3, 22, 11, 59, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 22, 11, 59, 59, 999, DateTimeKind.Utc),
            100m,
            101m,
            99m,
            100m,
            10m,
            true,
            new DateTime(2026, 3, 22, 12, 0, 0, 100, DateTimeKind.Utc),
            "Binance.WebSocket.Kline");
        var marketDataService = new FakeMarketDataService(sharedKline);
        await using var harness = CreateHarness(marketDataService);
        harness.TimeProvider.Advance(TimeSpan.FromSeconds(4));

        var snapshot = await harness.CircuitBreaker.GetSnapshotAsync(
            correlationId: "corr-shared-kline-003",
            symbol: "BTCUSDT",
            timeframe: "1m");

        Assert.Equal(DegradedModeStateCode.Degraded, snapshot.StateCode);
        Assert.Equal(DegradedModeReasonCode.MarketDataLatencyBreached, snapshot.ReasonCode);
        Assert.Equal(0, snapshot.LatestClockDriftMilliseconds);
        Assert.Equal("Binance.WebSocket.Kline", snapshot.LatestHeartbeatSource);
        Assert.Equal(1, marketDataService.SharedKlineReadCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_DoesNotTreatRestBackfillSharedKlineAsClockDriftExceeded()
    {
        var sharedKline = new MarketCandleSnapshot(
            "btcusdt",
            "1m",
            new DateTime(2026, 3, 22, 11, 59, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 22, 11, 59, 59, 999, DateTimeKind.Utc),
            100m,
            101m,
            99m,
            100m,
            10m,
            true,
            new DateTime(2026, 3, 22, 12, 0, 43, 471, DateTimeKind.Utc),
            "Binance.Rest.Kline");
        var marketDataService = new FakeMarketDataService(sharedKline);
        await using var harness = CreateHarness(
            marketDataService,
            new DataLatencyGuardOptions
            {
                StaleDataThresholdSeconds = 60,
                StopDataThresholdSeconds = 120,
                ClockDriftThresholdSeconds = 2
            });
        harness.TimeProvider.Advance(TimeSpan.FromSeconds(44));

        var snapshot = await harness.CircuitBreaker.GetSnapshotAsync(
            correlationId: "corr-shared-kline-rest-001",
            symbol: "BTCUSDT",
            timeframe: "1m");

        Assert.Equal(DegradedModeStateCode.Normal, snapshot.StateCode);
        Assert.Equal(DegradedModeReasonCode.None, snapshot.ReasonCode);
        Assert.Equal(0, snapshot.LatestClockDriftMilliseconds);
        Assert.Equal(sharedKline.ReceivedAtUtc, snapshot.LatestHeartbeatReceivedAtUtc);
        Assert.Equal("Binance.Rest.Kline", snapshot.LatestHeartbeatSource);
        Assert.Equal(1, marketDataService.SharedKlineReadCount);
    }

    [Fact]
    public async Task RecordHeartbeatAsync_DoesNotTreatHistoricalRestBackfillAsClockDriftExceeded()
    {
        await using var harness = CreateHarness(
            guardOptions: new DataLatencyGuardOptions
            {
                StaleDataThresholdSeconds = 60,
                StopDataThresholdSeconds = 120,
                ClockDriftThresholdSeconds = 2
            });
        harness.TimeProvider.Advance(TimeSpan.FromSeconds(44));
        var heartbeatReceivedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;

        var snapshot = await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "binance:rest-backfill",
                new DateTime(2026, 3, 22, 11, 59, 59, 999, DateTimeKind.Utc),
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                ExpectedOpenTimeUtc: new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
                ContinuityGapCount: 0,
                HeartbeatReceivedAtUtc: heartbeatReceivedAtUtc),
            correlationId: "corr-latency-heartbeat-rest-001");

        Assert.Equal(DegradedModeStateCode.Normal, snapshot.StateCode);
        Assert.Equal(DegradedModeReasonCode.None, snapshot.ReasonCode);
        Assert.Equal(0, snapshot.LatestClockDriftMilliseconds);
        Assert.Equal(heartbeatReceivedAtUtc, snapshot.LatestHeartbeatReceivedAtUtc);
        Assert.Equal("binance:rest-backfill", snapshot.LatestHeartbeatSource);
    }

    [Fact]
    public async Task RecordHeartbeatAsync_UsesHeartbeatReceivedAtUtc_ForClockDriftEvaluation()
    {
        await using var harness = CreateHarness();
        var dataTimestampUtc = new DateTime(2026, 3, 22, 11, 59, 59, 999, DateTimeKind.Utc);
        var heartbeatReceivedAtUtc = new DateTime(2026, 3, 22, 12, 0, 0, 100, DateTimeKind.Utc);

        var snapshot = await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "binance:kline",
                dataTimestampUtc,
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                ExpectedOpenTimeUtc: new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
                ContinuityGapCount: 0,
                HeartbeatReceivedAtUtc: heartbeatReceivedAtUtc),
            correlationId: "corr-latency-heartbeat-received-001");

        Assert.Equal(DegradedModeStateCode.Normal, snapshot.StateCode);
        Assert.Equal(DegradedModeReasonCode.None, snapshot.ReasonCode);
        Assert.Equal(101, snapshot.LatestClockDriftMilliseconds);
        Assert.Equal(heartbeatReceivedAtUtc, snapshot.LatestHeartbeatReceivedAtUtc);
        Assert.Equal(dataTimestampUtc, snapshot.LatestDataTimestampAtUtc);
    }

    [Fact]
    public async Task RecordHeartbeatAsync_PersistsLatestMarketDataIdentityAndContinuityMetadata()
    {
        await using var harness = CreateHarness();
        var nowUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;
        var expectedOpenTimeUtc = new DateTime(2026, 3, 22, 11, 59, 0, DateTimeKind.Utc);

        var snapshot = await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "binance:kline",
                new DateTime(2026, 3, 22, 11, 59, 0, DateTimeKind.Utc),
                DegradedModeStateCode.Stopped,
                DegradedModeReasonCode.CandleDataGapDetected,
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                ExpectedOpenTimeUtc: expectedOpenTimeUtc,
                ContinuityGapCount: 2),
            correlationId: "corr-latency-010");

        var stateId = DegradedModeDefaults.ResolveStateId("BTCUSDT", "1m");
        var state = await harness.DbContext.DegradedModeStates.SingleAsync(entity => entity.Id == stateId);

        Assert.Equal(DegradedModeStateCode.Stopped, snapshot.StateCode);
        Assert.Equal(DegradedModeReasonCode.CandleDataGapDetected, snapshot.ReasonCode);
        Assert.Equal("binance:kline", snapshot.LatestHeartbeatSource);
        Assert.Equal("BTCUSDT", snapshot.LatestSymbol);
        Assert.Equal("1m", snapshot.LatestTimeframe);
        Assert.Equal(expectedOpenTimeUtc, snapshot.LatestExpectedOpenTimeUtc);
        Assert.Equal(2, snapshot.LatestContinuityGapCount);
        Assert.Equal(expectedOpenTimeUtc, snapshot.LatestContinuityGapStartedAtUtc);
        Assert.Equal(nowUtc, snapshot.LatestContinuityGapLastSeenAtUtc);
        Assert.Null(snapshot.LatestContinuityRecoveredAtUtc);
        Assert.Equal(60000, snapshot.LatestDataAgeMilliseconds);
        Assert.Equal(nowUtc, snapshot.LatestHeartbeatReceivedAtUtc);
        Assert.Equal("binance:kline", state.LatestHeartbeatSource);
        Assert.Equal("BTCUSDT", state.LatestSymbol);
        Assert.Equal("1m", state.LatestTimeframe);
        Assert.Equal(expectedOpenTimeUtc, state.LatestExpectedOpenTimeUtc);
        Assert.Equal(2, state.LatestContinuityGapCount);
        Assert.Equal(expectedOpenTimeUtc, state.LatestContinuityGapStartedAtUtc);
        Assert.Equal(nowUtc, state.LatestContinuityGapLastSeenAtUtc);
        Assert.Null(state.LatestContinuityRecoveredAtUtc);
    }

    private static TestHarness CreateHarness(IMarketDataService? marketDataService = null, DataLatencyGuardOptions? guardOptions = null)
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var dbContext = new ApplicationDbContext(dbOptions, new TestDataScopeContext());
        var alertService = new FakeAlertService();
        var circuitBreaker = new DataLatencyCircuitBreaker(
            dbContext,
            alertService,
            Options.Create(guardOptions ?? new DataLatencyGuardOptions()),
            timeProvider,
            NullLogger<DataLatencyCircuitBreaker>.Instance,
            marketDataService);

        return new TestHarness(dbContext, circuitBreaker, timeProvider, alertService);
    }

    private sealed class FakeMarketDataService : IMarketDataService
    {
        private readonly SharedMarketDataCacheReadResult<MarketCandleSnapshot> klineReadResult;

        public FakeMarketDataService(MarketCandleSnapshot snapshot)
        {
            var normalizedSnapshot = snapshot with
            {
                Symbol = MarketDataSymbolNormalizer.Normalize(snapshot.Symbol)
            };

            klineReadResult = SharedMarketDataCacheReadResult<MarketCandleSnapshot>.HitFresh(
                new SharedMarketDataCacheEntry<MarketCandleSnapshot>(
                    SharedMarketDataCacheDataType.Kline,
                    normalizedSnapshot.Symbol,
                    normalizedSnapshot.Interval,
                    UpdatedAtUtc: normalizedSnapshot.ReceivedAtUtc,
                    CachedAtUtc: normalizedSnapshot.ReceivedAtUtc,
                    FreshUntilUtc: normalizedSnapshot.ReceivedAtUtc.AddSeconds(15),
                    ExpiresAtUtc: normalizedSnapshot.ReceivedAtUtc.AddMinutes(5),
                    Source: normalizedSnapshot.Source,
                    Payload: normalizedSnapshot));
        }

        public FakeMarketDataService(SharedMarketDataCacheReadResult<MarketCandleSnapshot> klineReadResult)
        {
            this.klineReadResult = klineReadResult;
        }

        public int SharedKlineReadCount { get; private set; }

        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<MarketPriceSnapshot?>(null);

        public ValueTask<SharedMarketDataCacheReadResult<MarketCandleSnapshot>> ReadLatestKlineAsync(
            string symbol,
            string timeframe,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SharedKlineReadCount++;
            return ValueTask.FromResult(klineReadResult);
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<SymbolMetadataSnapshot?>(null);

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeAlertService : IAlertService
    {
        public List<AlertNotification> Notifications { get; } = [];

        public Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default)
        {
            Notifications.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class TestHarness(
        ApplicationDbContext dbContext,
        IDataLatencyCircuitBreaker circuitBreaker,
        AdjustableTimeProvider timeProvider,
        FakeAlertService alertService) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public IDataLatencyCircuitBreaker CircuitBreaker { get; } = circuitBreaker;

        public AdjustableTimeProvider TimeProvider { get; } = timeProvider;

        public FakeAlertService AlertService { get; } = alertService;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }
    }
}







