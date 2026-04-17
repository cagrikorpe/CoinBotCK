using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.MarketData;

public sealed class HistoricalGapFillerServiceTests
{
    [Fact]
    public async Task DetectGapsAsync_DetectsMissingIntervalInsideConfiguredWindow()
    {
        await using var harness = CreateHarness(
            now: new DateTimeOffset(2026, 3, 22, 12, 3, 30, TimeSpan.Zero),
            clientSnapshots: []);
        await SeedCandlesAsync(
            harness.Context,
            CreateEntity("BTCUSDT", new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc)),
            CreateEntity("BTCUSDT", new DateTime(2026, 3, 22, 12, 2, 0, DateTimeKind.Utc)));

        var gaps = await harness.Service.DetectGapsAsync();

        var gap = Assert.Single(gaps);
        Assert.Equal("BTCUSDT", gap.Symbol);
        Assert.Equal("1m", gap.Interval);
        Assert.Equal(new DateTime(2026, 3, 22, 12, 1, 0, DateTimeKind.Utc), gap.StartOpenTimeUtc);
        Assert.Equal(new DateTime(2026, 3, 22, 12, 1, 0, DateTimeKind.Utc), gap.EndOpenTimeUtc);
        Assert.Equal(1, gap.MissingCandleCount);
    }

    [Fact]
    public async Task BackfillAsync_FillsMissingCandles_PreventsDuplicates_AndVerifiesContinuity()
    {
        await using var harness = CreateHarness(
            now: new DateTimeOffset(2026, 3, 22, 12, 3, 30, TimeSpan.Zero),
            clientSnapshots:
            [
                CreateSnapshot("BTCUSDT", new DateTime(2026, 3, 22, 12, 1, 0, DateTimeKind.Utc), 64050m),
                CreateSnapshot("BTCUSDT", new DateTime(2026, 3, 22, 12, 1, 0, DateTimeKind.Utc), 64050m)
            ]);
        await SeedCandlesAsync(
            harness.Context,
            CreateEntity("BTCUSDT", new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc)),
            CreateEntity("BTCUSDT", new DateTime(2026, 3, 22, 12, 2, 0, DateTimeKind.Utc)));

        var summary = await harness.Service.BackfillAsync();
        var storedOpenTimes = await harness.Context.HistoricalMarketCandles
            .OrderBy(entity => entity.OpenTimeUtc)
            .Select(entity => entity.OpenTimeUtc)
            .ToListAsync();

        Assert.Equal(1, summary.ScannedSymbolCount);
        Assert.Equal(1, summary.DetectedGapCount);
        Assert.Equal(1, summary.InsertedCandleCount);
        Assert.Equal(1, summary.SkippedDuplicateCount);
        Assert.Equal(1, summary.ContinuityVerifiedSymbolCount);
        Assert.Equal(
        [
            new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 22, 12, 1, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 22, 12, 2, 0, DateTimeKind.Utc)
        ], storedOpenTimes);
    }

    [Fact]
    public async Task BackfillAsync_ReactivatesSoftDeletedCandle_WhenReplacementSnapshotExists()
    {
        var deletedOpenTimeUtc = new DateTime(2026, 3, 22, 12, 1, 0, DateTimeKind.Utc);
        await using var harness = CreateHarness(
            now: new DateTimeOffset(2026, 3, 22, 12, 3, 30, TimeSpan.Zero),
            clientSnapshots:
            [
                CreateSnapshot("BTCUSDT", deletedOpenTimeUtc, 64055m)
            ]);
        var deletedCandle = CreateEntity("BTCUSDT", deletedOpenTimeUtc);
        await SeedCandlesAsync(
            harness.Context,
            CreateEntity("BTCUSDT", new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc)),
            deletedCandle,
            CreateEntity("BTCUSDT", new DateTime(2026, 3, 22, 12, 2, 0, DateTimeKind.Utc)));
        deletedCandle.IsDeleted = true;
        await harness.Context.SaveChangesAsync();

        var summary = await harness.Service.BackfillAsync();
        var storedCandles = await harness.Context.HistoricalMarketCandles
            .IgnoreQueryFilters()
            .Where(entity => entity.Symbol == "BTCUSDT" && entity.Interval == "1m")
            .OrderBy(entity => entity.OpenTimeUtc)
            .ToListAsync();
        var restoredCandle = Assert.Single(storedCandles, entity => entity.OpenTimeUtc == deletedOpenTimeUtc);

        Assert.Equal(3, storedCandles.Count);
        Assert.Equal(1, summary.InsertedCandleCount);
        Assert.False(restoredCandle.IsDeleted);
        Assert.Equal("Binance.Rest.Kline", restoredCandle.Source);
        Assert.Equal(64055m, restoredCandle.ClosePrice);
    }

    [Fact]
    public async Task BackfillAsync_ThrowsWhenContinuityCannotBeRestored()
    {
        await using var harness = CreateHarness(
            now: new DateTimeOffset(2026, 3, 22, 12, 3, 30, TimeSpan.Zero),
            clientSnapshots: []);
        await SeedCandlesAsync(
            harness.Context,
            CreateEntity("BTCUSDT", new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc)),
            CreateEntity("BTCUSDT", new DateTime(2026, 3, 22, 12, 2, 0, DateTimeKind.Utc)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.BackfillAsync());

        Assert.Contains("BTCUSDT", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadRecentCandlesAsync_ReturnsAscendingBoundedWindow_ForChartSeed()
    {
        await using var harness = CreateHarness(
            now: new DateTimeOffset(2026, 3, 22, 12, 5, 0, TimeSpan.Zero),
            clientSnapshots: []);
        await SeedCandlesAsync(
            harness.Context,
            CreateEntity("BTCUSDT", new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc)),
            CreateEntity("BTCUSDT", new DateTime(2026, 3, 22, 12, 1, 0, DateTimeKind.Utc)),
            CreateEntity("BTCUSDT", new DateTime(2026, 3, 22, 12, 2, 0, DateTimeKind.Utc)),
            CreateEntity("BTCUSDT", new DateTime(2026, 3, 22, 12, 3, 0, DateTimeKind.Utc)));

        var candles = await harness.Service.LoadRecentCandlesAsync("btcusdt", "1m", 2);

        Assert.Equal(2, candles.Count);
        Assert.Equal(new DateTime(2026, 3, 22, 12, 2, 0, DateTimeKind.Utc), candles.First().OpenTimeUtc);
        Assert.Equal(new DateTime(2026, 3, 22, 12, 3, 0, DateTimeKind.Utc), candles.Last().OpenTimeUtc);
    }

    [Fact]
    public async Task WarmIndicatorsAsync_PrimesLatestIndicatorSnapshot_FromHistoricalCandles()
    {
        await using var harness = CreateHarness(
            now: new DateTimeOffset(2026, 3, 22, 12, 40, 0, TimeSpan.Zero),
            clientSnapshots: [],
            lookbackCandles: 34);
        var historicalCandles = Enumerable.Range(0, 34)
            .Select(index => CreateEntity(
                "BTCUSDT",
                new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc).AddMinutes(index)))
            .ToArray();
        await SeedCandlesAsync(harness.Context, historicalCandles);

        var summary = await harness.Service.WarmIndicatorsAsync();
        var latestSnapshot = await harness.IndicatorDataService.GetLatestAsync("BTCUSDT", "1m");

        Assert.Equal("1m", summary.Interval);
        Assert.Equal(1, summary.RequestedSymbolCount);
        Assert.Equal(1, summary.PrimedSymbolCount);
        Assert.Equal(34, summary.LoadedCandleCount);
        Assert.NotNull(latestSnapshot);
        Assert.Equal(IndicatorDataState.Ready, latestSnapshot!.State);
        Assert.Equal(34, latestSnapshot.SampleCount);
        Assert.True(latestSnapshot.Macd.IsReady);
    }

    private static async Task SeedCandlesAsync(ApplicationDbContext context, params HistoricalMarketCandle[] entities)
    {
        context.HistoricalMarketCandles.AddRange(entities);
        await context.SaveChangesAsync();
    }

    private static TestHarness CreateHarness(
        DateTimeOffset now,
        IReadOnlyCollection<MarketCandleSnapshot> clientSnapshots,
        int lookbackCandles = 3)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var context = new ApplicationDbContext(options, new TestDataScopeContext());
        var marketDataService = new FakeMarketDataService();
        var indicatorDataService = new IndicatorDataService(
            marketDataService,
            new IndicatorStreamHub(),
            Options.Create(new IndicatorEngineOptions()),
            NullLogger<IndicatorDataService>.Instance);
        var service = new HistoricalGapFillerService(
            context,
            new FakeHistoricalKlineClient(clientSnapshots),
            indicatorDataService,
            Options.Create(new HistoricalGapFillerOptions
            {
                Enabled = true,
                ScanIntervalMinutes = 5,
                LookbackCandles = lookbackCandles,
                MaxCandlesPerRequest = 10,
                MaxRetryAttempts = 0,
                RetryDelaySeconds = 1,
                Symbols = ["BTCUSDT"]
            }),
            Options.Create(new BinanceMarketDataOptions
            {
                Enabled = false,
                RestBaseUrl = "https://api.binance.com",
                WebSocketBaseUrl = "wss://stream.binance.com:9443",
                KlineInterval = "1m",
                SeedSymbols = []
            }),
            new AdjustableTimeProvider(now),
            NullLogger<HistoricalGapFillerService>.Instance);

        return new TestHarness(context, service, indicatorDataService);
    }

    private static HistoricalMarketCandle CreateEntity(string symbol, DateTime openTimeUtc)
    {
        return new HistoricalMarketCandle
        {
            Symbol = symbol,
            Interval = "1m",
            OpenTimeUtc = openTimeUtc,
            CloseTimeUtc = openTimeUtc.AddMinutes(1).AddMilliseconds(-1),
            OpenPrice = 64000m,
            HighPrice = 64100m,
            LowPrice = 63900m,
            ClosePrice = 64050m,
            Volume = 12.5m,
            ReceivedAtUtc = openTimeUtc.AddMinutes(1),
            Source = "Seed"
        };
    }

    private static MarketCandleSnapshot CreateSnapshot(string symbol, DateTime openTimeUtc, decimal closePrice)
    {
        return new MarketCandleSnapshot(
            symbol,
            "1m",
            openTimeUtc,
            openTimeUtc.AddMinutes(1).AddMilliseconds(-1),
            OpenPrice: closePrice,
            HighPrice: closePrice,
            LowPrice: closePrice,
            ClosePrice: closePrice,
            Volume: 10m,
            IsClosed: true,
            ReceivedAtUtc: openTimeUtc.AddMinutes(1),
            Source: "Binance.Rest.Kline");
    }

    private sealed class FakeHistoricalKlineClient(IReadOnlyCollection<MarketCandleSnapshot> snapshots) : IBinanceHistoricalKlineClient
    {
        public Task<IReadOnlyCollection<MarketCandleSnapshot>> GetClosedCandlesAsync(
            string symbol,
            string interval,
            DateTime startOpenTimeUtc,
            DateTime endOpenTimeUtc,
            int limit,
            CancellationToken cancellationToken = default)
        {
            var results = snapshots
                .Where(snapshot =>
                    snapshot.Symbol == symbol &&
                    snapshot.Interval == interval &&
                    snapshot.OpenTimeUtc >= startOpenTimeUtc &&
                    snapshot.OpenTimeUtc <= endOpenTimeUtc)
                .OrderBy(snapshot => snapshot.OpenTimeUtc)
                .ToArray();

            return Task.FromResult<IReadOnlyCollection<MarketCandleSnapshot>>(results);
        }
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeMarketDataService : IMarketDataService
    {
        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<MarketPriceSnapshot?>(null);
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(null);
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class TestHarness(
        ApplicationDbContext context,
        HistoricalGapFillerService service,
        IIndicatorDataService indicatorDataService) : IAsyncDisposable
    {
        public ApplicationDbContext Context { get; } = context;

        public HistoricalGapFillerService Service { get; } = service;

        public IIndicatorDataService IndicatorDataService { get; } = indicatorDataService;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
        }
    }
}
