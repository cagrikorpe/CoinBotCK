using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using CoinBot.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.IntegrationTests.Administration;

public sealed class MarketScannerIntegrationTests
{
    [Fact]
    public async Task MarketScannerService_PersistsLatestScanCycleAndAdminReadModel_OnSqlServer()
    {
        var databaseName = $"CoinBotMarketScannerInt_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        await using var dbContext = CreateDbContext(connectionString);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        try
        {
            SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 50_000m);
            SeedCandles(dbContext, "SOLUSDT", nowUtc.UtcDateTime, closePrice: 20m, volume: 10m);
            await dbContext.SaveChangesAsync();

            var service = new MarketScannerService(
                dbContext,
                new FakeMarketDataService(),
                new FakeSharedSymbolRegistry([
                    new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime),
                    new SymbolMetadataSnapshot("SOLUSDT", "Binance", "SOL", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
                ]),
                Options.Create(new MarketScannerOptions
                {
                    TopCandidateCount = 1,
                    MaxUniverseSymbols = 20,
                    Min24hQuoteVolume = 5_000m,
                    MaxDataAgeSeconds = 120,
                    AllowedQuoteAssets = ["USDT"]
                }),
                Options.Create(new BinanceMarketDataOptions
                {
                    KlineInterval = "1m",
                    SeedSymbols = ["BTCUSDT", "SOLUSDT"]
                }),
                new FixedTimeProvider(nowUtc),
                NullLogger<MarketScannerService>.Instance);

            var cycle = await service.RunOnceAsync();

            var persistedCycle = await dbContext.MarketScannerCycles.AsNoTracking().SingleAsync(entity => entity.Id == cycle.Id);
            var persistedCandidates = await dbContext.MarketScannerCandidates
                .AsNoTracking()
                .Where(entity => entity.ScanCycleId == cycle.Id)
                .OrderBy(entity => entity.Symbol)
                .ToListAsync();
            var readModelService = new AdminMonitoringReadModelService(
                dbContext,
                new MemoryCache(new MemoryCacheOptions()),
                new FixedTimeProvider(nowUtc));
            var dashboardSnapshot = await readModelService.GetSnapshotAsync();

            Assert.Equal(2, persistedCycle.ScannedSymbolCount);
            Assert.Equal(1, persistedCycle.EligibleCandidateCount);
            Assert.Equal("BTCUSDT", persistedCycle.BestCandidateSymbol);
            Assert.Equal(2, persistedCandidates.Count);
            Assert.Contains(persistedCandidates, candidate => candidate.Symbol == "BTCUSDT" && candidate.IsEligible && candidate.Rank == 1 && candidate.IsTopCandidate);
            Assert.Contains(persistedCandidates, candidate => candidate.Symbol == "SOLUSDT" && !candidate.IsEligible && candidate.RejectionReason == "LowQuoteVolume");
            Assert.Equal("BTCUSDT", dashboardSnapshot.MarketScanner.BestCandidateSymbol);
            Assert.Equal(2, dashboardSnapshot.MarketScanner.ScannedSymbolCount);
            Assert.Equal(1, dashboardSnapshot.MarketScanner.EligibleCandidateCount);
            Assert.Single(dashboardSnapshot.MarketScanner.TopCandidates);
            Assert.Single(dashboardSnapshot.MarketScanner.RejectedSamples);
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

    private static void SeedCandles(ApplicationDbContext dbContext, string symbol, DateTime latestCloseTimeUtc, decimal closePrice, decimal volume)
    {
        var firstOpenTimeUtc = latestCloseTimeUtc.AddMinutes(-10);

        for (var index = 0; index < 10; index++)
        {
            var openTimeUtc = firstOpenTimeUtc.AddMinutes(index);
            dbContext.HistoricalMarketCandles.Add(new HistoricalMarketCandle
            {
                Id = Guid.NewGuid(),
                Symbol = symbol,
                Interval = "1m",
                OpenTimeUtc = openTimeUtc,
                CloseTimeUtc = openTimeUtc.AddMinutes(1),
                OpenPrice = closePrice,
                HighPrice = closePrice,
                LowPrice = closePrice,
                ClosePrice = closePrice,
                Volume = volume,
                ReceivedAtUtc = openTimeUtc.AddMinutes(1),
                Source = "integration-test"
            });
        }
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

    private sealed class FakeSharedSymbolRegistry(IReadOnlyCollection<SymbolMetadataSnapshot> snapshots) : ISharedSymbolRegistry
    {
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
        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<MarketPriceSnapshot?>(null);
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
}
