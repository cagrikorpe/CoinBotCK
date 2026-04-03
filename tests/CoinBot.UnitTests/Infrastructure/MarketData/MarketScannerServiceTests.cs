using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.MarketData;

public sealed class MarketScannerServiceTests
{
    [Fact]
    public async Task RunOnceAsync_ResolvesUniverseFromRegistryConfigBotsAndHistoricalCandles_AndRanksEligibleCandidatesDeterministically()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        dbContext.TradingBots.Add(new TradingBot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-1",
            Name = "ETH Bot",
            StrategyKey = "breakout",
            Symbol = "ETHUSDT",
            IsEnabled = true
        });
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        SeedCandles(dbContext, "ETHUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        SeedCandles(dbContext, "SOLUSDT", nowUtc.UtcDateTime, closePrice: 25m, volume: 10m);
        SeedCandles(dbContext, "ADAUSDT", nowUtc.UtcDateTime, closePrice: 1m, volume: 3_000_000m);
        await dbContext.SaveChangesAsync();

        var marketDataService = new FakeMarketDataService();
        marketDataService.SetLatestPrice("BTCUSDT", 100m, nowUtc.UtcDateTime);
        marketDataService.SetLatestPrice("ETHUSDT", 100m, nowUtc.UtcDateTime);
        marketDataService.SetLatestPrice("SOLUSDT", 25m, nowUtc.UtcDateTime);
        marketDataService.SetLatestPrice("ADAUSDT", 1m, nowUtc.UtcDateTime);
        var service = new MarketScannerService(
            dbContext,
            marketDataService,
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime),
                new SymbolMetadataSnapshot("ETHUSDT", "Binance", "ETH", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime),
                new SymbolMetadataSnapshot("SOLUSDT", "Binance", "SOL", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 2,
                MaxUniverseSymbols = 50,
                Min24hQuoteVolume = 50_000m,
                MaxDataAgeSeconds = 120,
                AllowedQuoteAssets = ["USDT"]
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT", "   ", "SOLUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance);

        var cycle = await service.RunOnceAsync();

        var persistedCycle = await dbContext.MarketScannerCycles.SingleAsync(entity => entity.Id == cycle.Id);
        var candidates = await dbContext.MarketScannerCandidates
            .Where(entity => entity.ScanCycleId == cycle.Id)
            .OrderBy(entity => entity.Symbol)
            .ToListAsync();
        var heartbeat = await dbContext.WorkerHeartbeats.SingleAsync(entity => entity.WorkerKey == MarketScannerService.WorkerKey);

        Assert.Equal(4, persistedCycle.ScannedSymbolCount);
        Assert.Equal(3, persistedCycle.EligibleCandidateCount);
        Assert.Equal("ADAUSDT", persistedCycle.BestCandidateSymbol);
        Assert.Equal("config+enabled-bot+historical-candles+registry", persistedCycle.UniverseSource);

        var ada = Assert.Single(candidates, candidate => candidate.Symbol == "ADAUSDT");
        var btc = Assert.Single(candidates, candidate => candidate.Symbol == "BTCUSDT");
        var eth = Assert.Single(candidates, candidate => candidate.Symbol == "ETHUSDT");
        var sol = Assert.Single(candidates, candidate => candidate.Symbol == "SOLUSDT");

        Assert.True(ada.IsEligible);
        Assert.Equal(1, ada.Rank);
        Assert.True(ada.IsTopCandidate);
        Assert.Equal("historical-candles", ada.UniverseSource);
        Assert.True(btc.IsEligible);
        Assert.Equal(2, btc.Rank);
        Assert.True(btc.IsTopCandidate);
        Assert.Equal("config+historical-candles+registry", btc.UniverseSource);
        Assert.True(eth.IsEligible);
        Assert.Equal(3, eth.Rank);
        Assert.False(eth.IsTopCandidate);
        Assert.Equal("enabled-bot+historical-candles+registry", eth.UniverseSource);
        Assert.False(sol.IsEligible);
        Assert.Equal("LowQuoteVolume", sol.RejectionReason);
        Assert.Equal(MonitoringHealthState.Healthy, heartbeat.HealthState);
        Assert.Contains("BestCandidate=ADAUSDT", heartbeat.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnceAsync_RejectsDisabledUnsupportedStaleAndMissingMarketData_FailClosed()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        SeedCandles(dbContext, "STALEUSDT", nowUtc.UtcDateTime.AddMinutes(-10), closePrice: 10m, volume: 100_000m);
        SeedCandles(dbContext, "XRPBTC", nowUtc.UtcDateTime, closePrice: 0.00002m, volume: 100_000m);
        SeedCandles(dbContext, "HALTUSDT", nowUtc.UtcDateTime, closePrice: 1m, volume: 100_000m);
        await dbContext.SaveChangesAsync();

        var service = new MarketScannerService(
            dbContext,
            new FakeMarketDataService(),
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("HALTUSDT", "Binance", "HALT", "USDT", 0.1m, 1m, "BREAK", false, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 5,
                MaxUniverseSymbols = 50,
                Min24hQuoteVolume = 100m,
                MaxDataAgeSeconds = 120,
                AllowedQuoteAssets = ["USDT"]
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["STALEUSDT", "XRPBTC", "HALTUSDT", "MISSINGUSDT", " "] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance);

        var cycle = await service.RunOnceAsync();
        var candidates = await dbContext.MarketScannerCandidates
            .Where(entity => entity.ScanCycleId == cycle.Id)
            .ToDictionaryAsync(entity => entity.Symbol);

        Assert.Equal("StaleMarketData", candidates["STALEUSDT"].RejectionReason);
        Assert.Equal("QuoteAssetNotAllowed", candidates["XRPBTC"].RejectionReason);
        Assert.Equal("SymbolTradingDisabled", candidates["HALTUSDT"].RejectionReason);
        Assert.Equal("MissingLastPrice", candidates["MISSINGUSDT"].RejectionReason);
        Assert.All(candidates.Values, candidate => Assert.False(candidate.IsTopCandidate));
    }

    [Fact]
    public async Task RunOnceAsync_UsesStableSymbolTieBreak_WhenScoresAreEqual()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        await using var dbContext = CreateDbContext();
        SeedCandles(dbContext, "ETHUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        await dbContext.SaveChangesAsync();

        var service = new MarketScannerService(
            dbContext,
            new FakeMarketDataService(),
            new FakeSharedSymbolRegistry([]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 2,
                MaxUniverseSymbols = 50,
                Min24hQuoteVolume = 10m,
                MaxDataAgeSeconds = 120,
                AllowedQuoteAssets = ["USDT"]
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["ETHUSDT", "BTCUSDT"] }),
            new AdjustableTimeProvider(nowUtc),
            NullLogger<MarketScannerService>.Instance);

        var cycle = await service.RunOnceAsync();
        var rankedSymbols = await dbContext.MarketScannerCandidates
            .Where(entity => entity.ScanCycleId == cycle.Id && entity.IsEligible)
            .OrderBy(entity => entity.Rank)
            .Select(entity => entity.Symbol)
            .ToListAsync();

        Assert.Equal(["BTCUSDT", "ETHUSDT"], rankedSymbols);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private static void SeedCandles(ApplicationDbContext dbContext, string symbol, DateTime latestCloseTimeUtc, decimal closePrice, decimal volume)
    {
        var normalizedCloseTimeUtc = DateTime.SpecifyKind(latestCloseTimeUtc, DateTimeKind.Utc);
        var firstOpenTimeUtc = normalizedCloseTimeUtc.AddMinutes(-10);

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
                Source = "unit-test"
            });
        }
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
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
        private readonly Dictionary<string, MarketPriceSnapshot> latestPrices = new(StringComparer.Ordinal);

        public void SetLatestPrice(string symbol, decimal price, DateTime observedAtUtc)
        {
            latestPrices[symbol] = new MarketPriceSnapshot(symbol, price, observedAtUtc, observedAtUtc, "unit-test");
        }

        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
        {
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
}
