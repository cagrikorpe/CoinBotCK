using CoinBot.Application.Abstractions.Autonomy;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Autonomy;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Policy;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Autonomy;

public sealed class MarketAnomalyServiceTests
{
    [Fact]
    public async Task RunOnceAsync_AppliesRestrictionQueuesReviewAndWritesIncident_ForCompoundedAnomaly()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 31, 10, 0, 0, TimeSpan.Zero));
        await using var harness = CreateHarness(timeProvider, ["BTCUSDT"]);
        SeedCandles(harness.DbContext, "BTCUSDT", timeProvider.GetUtcNow().UtcDateTime, stableVolume: 1_000m, lastVolume: 120m, lastHigh: 108m, lastLow: 92m);
        harness.MarketDataService.SetLatestPrice("BTCUSDT", 88m, timeProvider.GetUtcNow().UtcDateTime);

        var result = await harness.Service.RunOnceAsync();
        var evaluation = Assert.Single(result.Evaluations);
        var incident = await harness.DbContext.Incidents.SingleAsync();
        var reviewEntry = await harness.DbContext.AutonomyReviewQueueEntries.SingleAsync();
        var workerHeartbeat = await harness.DbContext.WorkerHeartbeats.SingleAsync(entity => entity.WorkerKey == MarketAnomalyService.WorkerKey);
        var policySnapshot = await harness.PolicyEngine.GetSnapshotAsync();

        Assert.Equal(SymbolRestrictionState.Blocked, evaluation.ProposedState);
        Assert.True(evaluation.PolicyUpdated);
        Assert.True(evaluation.ReviewQueued);
        Assert.True(evaluation.UsedLatestPrice);
        Assert.True(evaluation.UsedHistoricalCandles);
        Assert.True(evaluation.UsedDegradedMode);
        Assert.Contains("BTCUSDT", harness.MarketDataService.RequestedSymbols);
        Assert.Equal(1, result.PolicyUpdatedCount);
        Assert.Equal(1, result.ReviewQueuedCount);
        Assert.Contains(policySnapshot.Policy.SymbolRestrictions, item => item.Symbol == "BTCUSDT" && item.State == SymbolRestrictionState.Blocked);
        Assert.Equal("BTCUSDT", reviewEntry.AffectedSymbolsCsv);
        Assert.Equal("Autonomy", incident.TargetType);
        Assert.Equal("SYMBOL:BTCUSDT", incident.TargetId);
        Assert.Equal(IncidentSeverity.Critical, incident.Severity);
        Assert.Equal(MonitoringHealthState.Critical, workerHeartbeat.HealthState);
    }

    [Fact]
    public async Task RunOnceAsync_DoesNotTriggerFalsePositive_OnNormalMarketConditions()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 31, 10, 5, 0, TimeSpan.Zero));
        await using var harness = CreateHarness(timeProvider, ["BTCUSDT"]);
        SeedCandles(harness.DbContext, "BTCUSDT", timeProvider.GetUtcNow().UtcDateTime, stableVolume: 1_000m, lastVolume: 980m, lastHigh: 101.2m, lastLow: 99.4m);
        harness.MarketDataService.SetLatestPrice("BTCUSDT", 100.4m, timeProvider.GetUtcNow().UtcDateTime);

        var result = await harness.Service.RunOnceAsync();
        var evaluation = Assert.Single(result.Evaluations);
        var workerHeartbeat = await harness.DbContext.WorkerHeartbeats.SingleAsync(entity => entity.WorkerKey == MarketAnomalyService.WorkerKey);

        Assert.Equal(MarketAnomalyDecision.NoAction, evaluation.Decision);
        Assert.Equal(0, result.PolicyUpdatedCount);
        Assert.Equal(0, result.ReviewQueuedCount);
        Assert.Equal(0, result.InsufficientDataCount);
        Assert.Empty(await harness.DbContext.Incidents.ToListAsync());
        Assert.Empty(await harness.DbContext.AutonomyReviewQueueEntries.ToListAsync());
        Assert.Equal(MonitoringHealthState.Healthy, workerHeartbeat.HealthState);
    }

    [Fact]
    public async Task RunOnceAsync_DoesNotQueueStaleReview_WhenRecentClosedCandleIsWithinScannerFreshnessWindow()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 31, 10, 20, 22, TimeSpan.Zero));
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        await using var harness = CreateHarness(
            timeProvider,
            ["BTCUSDT"],
            marketScannerOptions: new MarketScannerOptions
            {
                MaxDataAgeSeconds = 120
            },
            dataLatencyGuardOptions: new DataLatencyGuardOptions
            {
                StaleDataThresholdSeconds = 60,
                StopDataThresholdSeconds = 120
            });
        SeedCandles(harness.DbContext, "BTCUSDT", nowUtc.AddSeconds(-22), stableVolume: 1_000m, lastVolume: 980m, lastHigh: 101.2m, lastLow: 99.4m);
        harness.MarketDataService.SetLatestPrice("BTCUSDT", 100.4m, nowUtc.AddSeconds(-1));

        var result = await harness.Service.RunOnceAsync();
        var evaluation = Assert.Single(result.Evaluations);

        Assert.Equal(MarketAnomalyDecision.NoAction, evaluation.Decision);
        Assert.Equal(0, result.ReviewQueuedCount);
        Assert.Equal(0, result.PolicyUpdatedCount);
        Assert.Null(evaluation.ProposedState);
        Assert.DoesNotContain("StaleData", evaluation.TriggerLabels);
        Assert.Empty(await harness.DbContext.AutonomyReviewQueueEntries.ToListAsync());
        Assert.Empty(await harness.DbContext.Incidents.ToListAsync());
    }

    [Fact]
    public async Task RunOnceAsync_QueuesReviewOnly_WhenAutonomyPolicyRequiresManualApproval()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 31, 10, 7, 0, TimeSpan.Zero));
        await using var harness = CreateHarness(timeProvider, ["BTCUSDT"]);
        SeedCandles(harness.DbContext, "BTCUSDT", timeProvider.GetUtcNow().UtcDateTime, stableVolume: 1_000m, lastVolume: 120m, lastHigh: 108m, lastLow: 92m);
        harness.MarketDataService.SetLatestPrice("BTCUSDT", 88m, timeProvider.GetUtcNow().UtcDateTime);

        var initialPolicy = await harness.PolicyEngine.GetSnapshotAsync();
        await harness.PolicyEngine.UpdateAsync(
            new GlobalPolicyUpdateRequest(
                new RiskPolicySnapshot(
                    initialPolicy.Policy.PolicyKey,
                    initialPolicy.Policy.ExecutionGuardPolicy,
                    new AutonomyPolicy(AutonomyPolicyMode.ManualApprovalRequired, RequireManualApprovalForLive: true),
                    initialPolicy.Policy.SymbolRestrictions),
                "system:test",
                "Switch autonomy mode",
                "corr-policy-manual",
                "UnitTest",
                IpAddress: null,
                UserAgent: null));

        var result = await harness.Service.RunOnceAsync();
        var evaluation = Assert.Single(result.Evaluations);

        Assert.False(evaluation.PolicyUpdated);
        Assert.True(evaluation.ReviewQueued);
        Assert.Equal(0, result.PolicyUpdatedCount);
        Assert.Equal(1, result.ReviewQueuedCount);
        Assert.Empty((await harness.PolicyEngine.GetSnapshotAsync()).Policy.SymbolRestrictions);
        Assert.Single(await harness.DbContext.AutonomyReviewQueueEntries.ToListAsync());
        Assert.Single(await harness.DbContext.Incidents.ToListAsync());
    }

    [Fact]
    public async Task RunOnceAsync_RecordsWarningHeartbeat_WhenInputsAreIncomplete()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 31, 10, 10, 0, TimeSpan.Zero));
        await using var harness = CreateHarness(timeProvider, ["ETHUSDT"]);

        var result = await harness.Service.RunOnceAsync();
        var evaluation = Assert.Single(result.Evaluations);
        var workerHeartbeat = await harness.DbContext.WorkerHeartbeats.SingleAsync(entity => entity.WorkerKey == MarketAnomalyService.WorkerKey);

        Assert.Equal(MarketAnomalyDecision.InsufficientData, evaluation.Decision);
        Assert.Equal(1, result.InsufficientDataCount);
        Assert.Contains("ETHUSDT", harness.MarketDataService.RequestedSymbols);
        Assert.Empty(await harness.DbContext.Incidents.ToListAsync());
        Assert.Empty(await harness.DbContext.AutonomyReviewQueueEntries.ToListAsync());
        Assert.Equal(MonitoringHealthState.Warning, workerHeartbeat.HealthState);
        Assert.Equal("InsufficientData", workerHeartbeat.LastErrorCode);
    }

    [Fact]
    public async Task MarketAnomalyWorker_RecordFailureAsync_PersistsCriticalHeartbeat()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var services = new ServiceCollection();
        services.AddScoped(_ => new ApplicationDbContext(options, new TestDataScopeContext()));
        services.AddLogging();

        using var provider = services.BuildServiceProvider();
        var worker = new MarketAnomalyWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MarketAnomalyOptions()),
            new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 31, 10, 15, 0, TimeSpan.Zero)),
            NullLogger<MarketAnomalyWorker>.Instance);

        await worker.RecordFailureAsync(new InvalidOperationException("market anomaly failure"));

        await using var verifyContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var heartbeat = await verifyContext.WorkerHeartbeats.SingleAsync(entity => entity.WorkerKey == MarketAnomalyService.WorkerKey);

        Assert.Equal(MonitoringHealthState.Critical, heartbeat.HealthState);
        Assert.Equal("InvalidOperationException", heartbeat.LastErrorCode);
        Assert.Contains("market anomaly failure", heartbeat.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    private static TestHarness CreateHarness(
        AdjustableTimeProvider timeProvider,
        IReadOnlyCollection<string> symbols,
        MarketAnomalyOptions? marketAnomalyOptions = null,
        MarketScannerOptions? marketScannerOptions = null,
        DataLatencyGuardOptions? dataLatencyGuardOptions = null)
    {
        var dbContext = CreateDbContext();
        var auditService = new AdminAuditLogService(dbContext, new CorrelationContextAccessor(), timeProvider);
        var policyEngine = new GlobalPolicyEngine(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            auditService,
            timeProvider,
            NullLogger<GlobalPolicyEngine>.Instance);
        var reviewQueueService = new AutonomyReviewQueueService(dbContext, auditService, timeProvider);
        var marketDataService = new FakeMarketDataService();
        var symbolRegistry = new FakeSharedSymbolRegistry(symbols);
        var latencyCircuitBreaker = new FakeDataLatencyCircuitBreaker(
            new DegradedModeSnapshot(
                DegradedModeStateCode.Normal,
                DegradedModeReasonCode.None,
                SignalFlowBlocked: false,
                ExecutionFlowBlocked: false,
                LatestDataTimestampAtUtc: timeProvider.GetUtcNow().UtcDateTime,
                LatestHeartbeatReceivedAtUtc: timeProvider.GetUtcNow().UtcDateTime,
                LatestDataAgeMilliseconds: 0,
                LatestClockDriftMilliseconds: 0,
                LastStateChangedAtUtc: timeProvider.GetUtcNow().UtcDateTime,
                IsPersisted: true));
        var service = new MarketAnomalyService(
            dbContext,
            policyEngine,
            marketDataService,
            symbolRegistry,
            reviewQueueService,
            new AutonomyIncidentHook(dbContext, auditService, timeProvider),
            latencyCircuitBreaker,
            Options.Create(marketAnomalyOptions ?? new MarketAnomalyOptions()),
            Options.Create(new AutonomyOptions()),
            Options.Create(marketScannerOptions ?? new MarketScannerOptions()),
            Options.Create(dataLatencyGuardOptions ?? new DataLatencyGuardOptions()),
            Options.Create(new BinanceMarketDataOptions()),
            timeProvider,
            NullLogger<MarketAnomalyService>.Instance);

        return new TestHarness(dbContext, service, policyEngine, marketDataService);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private static void SeedCandles(
        ApplicationDbContext dbContext,
        string symbol,
        DateTime nowUtc,
        decimal stableVolume,
        decimal lastVolume,
        decimal lastHigh,
        decimal lastLow)
    {
        var openTimeUtc = nowUtc.AddMinutes(-31);

        for (var index = 0; index < 30; index++)
        {
            var closeTimeUtc = openTimeUtc.AddMinutes(1);
            dbContext.HistoricalMarketCandles.Add(new HistoricalMarketCandle
            {
                Id = Guid.NewGuid(),
                Symbol = symbol,
                Interval = "1m",
                OpenTimeUtc = openTimeUtc,
                CloseTimeUtc = closeTimeUtc,
                OpenPrice = 100m,
                HighPrice = 100.8m,
                LowPrice = 99.6m,
                ClosePrice = 100m,
                Volume = stableVolume,
                ReceivedAtUtc = closeTimeUtc,
                Source = "unit-test"
            });

            openTimeUtc = closeTimeUtc;
        }

        var finalCloseTimeUtc = openTimeUtc.AddMinutes(1);
        dbContext.HistoricalMarketCandles.Add(new HistoricalMarketCandle
        {
            Id = Guid.NewGuid(),
            Symbol = symbol,
            Interval = "1m",
            OpenTimeUtc = openTimeUtc,
            CloseTimeUtc = finalCloseTimeUtc,
            OpenPrice = 100m,
            HighPrice = lastHigh,
            LowPrice = lastLow,
            ClosePrice = 100m,
            Volume = lastVolume,
            ReceivedAtUtc = finalCloseTimeUtc,
            Source = "unit-test"
        });

        dbContext.SaveChanges();
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeDataLatencyCircuitBreaker(DegradedModeSnapshot snapshot) : IDataLatencyCircuitBreaker
    {
        public int GetSnapshotCalls { get; private set; }

        public Task<DegradedModeSnapshot> GetSnapshotAsync(string? correlationId = null, string? symbol = null, string? timeframe = null, CancellationToken cancellationToken = default)
        {
            _ = correlationId;
            GetSnapshotCalls++;
            return Task.FromResult(snapshot);
        }

        public Task<DegradedModeSnapshot> RecordHeartbeatAsync(DataLatencyHeartbeat heartbeat, string? correlationId = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeSharedSymbolRegistry(IReadOnlyCollection<string> symbols) : ISharedSymbolRegistry
    {
        private readonly IReadOnlyCollection<SymbolMetadataSnapshot> snapshots = symbols
            .Select(symbol => new SymbolMetadataSnapshot(symbol, "Binance", "BASE", "USDT", 0.01m, 0.001m, "TRADING", true, DateTime.UtcNow))
            .ToArray();

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(snapshots.SingleOrDefault(item => item.Symbol == symbol));
        }

        public ValueTask<IReadOnlyCollection<SymbolMetadataSnapshot>> ListSymbolsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(snapshots);
        }
    }

    private sealed class FakeMarketDataService : IMarketDataService
    {
        private readonly Dictionary<string, MarketPriceSnapshot> latestPrices = new(StringComparer.Ordinal);

        public List<string> RequestedSymbols { get; } = [];

        public void SetLatestPrice(string symbol, decimal price, DateTime observedAtUtc)
        {
            latestPrices[symbol] = new MarketPriceSnapshot(symbol, price, observedAtUtc, observedAtUtc, "unit-test");
        }

        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
        {
            RequestedSymbols.Add(symbol);
            latestPrices.TryGetValue(symbol, out var snapshot);
            return ValueTask.FromResult<MarketPriceSnapshot?>(snapshot);
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(null);
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class TestHarness(
        ApplicationDbContext dbContext,
        MarketAnomalyService service,
        GlobalPolicyEngine policyEngine,
        FakeMarketDataService marketDataService) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public MarketAnomalyService Service { get; } = service;

        public GlobalPolicyEngine PolicyEngine { get; } = policyEngine;

        public FakeMarketDataService MarketDataService { get; } = marketDataService;

        public ValueTask DisposeAsync()
        {
            return DbContext.DisposeAsync();
        }
    }
}
