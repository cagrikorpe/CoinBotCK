using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Autonomy;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Policy;
using CoinBot.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.IntegrationTests.Administration;

public sealed class MarketAnomalyIntegrationTests
{
    [Fact]
    public async Task MarketAnomalyService_PersistsPolicyReviewIncidentAndHeartbeat_OnSqlServer()
    {
        var databaseName = $"CoinBotMarketAnomalyInt_{Guid.NewGuid():N}";
        var connectionString = ResolveConnectionString(databaseName);
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 3, 31, 11, 0, 0, TimeSpan.Zero));

        await using var dbContext = CreateDbContext(connectionString);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        try
        {
            SeedCandles(dbContext, "BTCUSDT", timeProvider.GetUtcNow().UtcDateTime, stableVolume: 1_000m, lastVolume: 100m, lastHigh: 108m, lastLow: 91m);

            var auditService = new AdminAuditLogService(dbContext, new CorrelationContextAccessor(), timeProvider);
            var reviewQueueService = new AutonomyReviewQueueService(dbContext, auditService, timeProvider);
            using var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var policyEngine = new GlobalPolicyEngine(
                dbContext,
                memoryCache,
                auditService,
                timeProvider,
                NullLogger<GlobalPolicyEngine>.Instance);
            var service = new MarketAnomalyService(
                dbContext,
                policyEngine,
                new FakeMarketDataService("BTCUSDT", 87m, timeProvider.GetUtcNow().UtcDateTime),
                new EmptySharedSymbolRegistry(),
                reviewQueueService,
                new AutonomyIncidentHook(dbContext, auditService, timeProvider),
                new FakeDataLatencyCircuitBreaker(
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
                        IsPersisted: true)),
                Options.Create(new MarketAnomalyOptions()),
                Options.Create(new AutonomyOptions()),
                Options.Create(new MarketScannerOptions()),
                Options.Create(new DataLatencyGuardOptions()),
                Options.Create(new BinanceMarketDataOptions()),
                timeProvider,
                NullLogger<MarketAnomalyService>.Instance);

            var result = await service.RunOnceAsync();
            var policySnapshot = await policyEngine.GetSnapshotAsync();
            var versions = await dbContext.RiskPolicyVersions.AsNoTracking().OrderBy(entity => entity.Version).ToListAsync();
            var reviewEntry = await dbContext.AutonomyReviewQueueEntries.AsNoTracking().SingleAsync();
            var incident = await dbContext.Incidents.AsNoTracking().SingleAsync();
            var incidentEvent = await dbContext.IncidentEvents.AsNoTracking().SingleAsync();
            var workerHeartbeat = await dbContext.WorkerHeartbeats.AsNoTracking().SingleAsync(entity => entity.WorkerKey == MarketAnomalyService.WorkerKey);
            var auditActions = await dbContext.AdminAuditLogs
                .AsNoTracking()
                .OrderBy(entity => entity.CreatedAtUtc)
                .Select(entity => entity.ActionType)
                .ToListAsync();

            Assert.Single(result.Evaluations);
            Assert.Equal(1, result.PolicyUpdatedCount);
            Assert.Equal(1, result.ReviewQueuedCount);
            Assert.Contains(policySnapshot.Policy.SymbolRestrictions, item => item.Symbol == "BTCUSDT" && item.State == SymbolRestrictionState.Blocked);
            Assert.Equal(2, versions.Count);
            Assert.Equal("SYMBOL:BTCUSDT", reviewEntry.ScopeKey);
            Assert.Equal("Autonomy", incident.TargetType);
            Assert.Equal("SYMBOL:BTCUSDT", incident.TargetId);
            Assert.Equal(IncidentEventType.IncidentCreated, incidentEvent.EventType);
            Assert.Equal(MonitoringHealthState.Critical, workerHeartbeat.HealthState);
            Assert.Contains("GlobalPolicy.Update", auditActions);
            Assert.Contains("Admin.Autonomy.Incident", auditActions);
            Assert.Contains("Autonomy.ReviewQueue.Enqueue", auditActions);
        }
        finally
        {
            await dbContext.Database.EnsureDeletedAsync();
        }
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
        return SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
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
                Source = "integration-test"
            });

            openTimeUtc = closeTimeUtc;
        }

        dbContext.HistoricalMarketCandles.Add(new HistoricalMarketCandle
        {
            Id = Guid.NewGuid(),
            Symbol = symbol,
            Interval = "1m",
            OpenTimeUtc = openTimeUtc,
            CloseTimeUtc = openTimeUtc.AddMinutes(1),
            OpenPrice = 100m,
            HighPrice = lastHigh,
            LowPrice = lastLow,
            ClosePrice = 100m,
            Volume = lastVolume,
            ReceivedAtUtc = openTimeUtc.AddMinutes(1),
            Source = "integration-test"
        });

        dbContext.SaveChanges();
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

    private sealed class EmptySharedSymbolRegistry : ISharedSymbolRegistry
    {
        public ValueTask<SymbolMetadataSnapshot?> GetSymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(null);
        }

        public ValueTask<IReadOnlyCollection<SymbolMetadataSnapshot>> ListSymbolsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyCollection<SymbolMetadataSnapshot>>(Array.Empty<SymbolMetadataSnapshot>());
        }
    }

    private sealed class FakeMarketDataService(string symbol, decimal price, DateTime observedAtUtc) : IMarketDataService
    {
        public ValueTask TrackSymbolAsync(string trackedSymbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string requestedSymbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<MarketPriceSnapshot?>(
                string.Equals(requestedSymbol, symbol, StringComparison.Ordinal)
                    ? new MarketPriceSnapshot(symbol, price, observedAtUtc, observedAtUtc, "integration-test")
                    : null);
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string requestedSymbol, CancellationToken cancellationToken = default)
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

    private sealed class FakeDataLatencyCircuitBreaker(DegradedModeSnapshot snapshot) : IDataLatencyCircuitBreaker
    {
        public Task<DegradedModeSnapshot> GetSnapshotAsync(string? correlationId = null, string? symbol = null, string? timeframe = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(snapshot);
        }

        public Task<DegradedModeSnapshot> RecordHeartbeatAsync(DataLatencyHeartbeat heartbeat, string? correlationId = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
