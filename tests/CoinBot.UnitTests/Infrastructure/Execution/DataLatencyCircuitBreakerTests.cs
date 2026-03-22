using CoinBot.Application.Abstractions.Alerts;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
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

        harness.TimeProvider.Advance(TimeSpan.FromSeconds(3));

        var degradedSnapshot = await harness.CircuitBreaker.GetSnapshotAsync(correlationId: "corr-latency-003");

        Assert.Equal(DegradedModeStateCode.Normal, initialSnapshot.StateCode);
        Assert.Equal(DegradedModeStateCode.Degraded, degradedSnapshot.StateCode);
        Assert.Equal(DegradedModeReasonCode.MarketDataLatencyBreached, degradedSnapshot.ReasonCode);
        Assert.True(degradedSnapshot.SignalFlowBlocked);
        Assert.True(degradedSnapshot.ExecutionFlowBlocked);
        Assert.Equal(3000, degradedSnapshot.LatestDataAgeMilliseconds);
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
    }

    private static TestHarness CreateHarness()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var alertService = new FakeAlertService();
        var circuitBreaker = new DataLatencyCircuitBreaker(
            dbContext,
            alertService,
            Options.Create(new DataLatencyGuardOptions()),
            timeProvider,
            NullLogger<DataLatencyCircuitBreaker>.Instance);

        return new TestHarness(dbContext, circuitBreaker, timeProvider, alertService);
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
