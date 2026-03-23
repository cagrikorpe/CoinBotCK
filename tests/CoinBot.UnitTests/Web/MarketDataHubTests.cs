using System.Security.Claims;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using CoinBot.Web.Hubs;
using CoinBot.Web.ViewModels.Home;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Web;

public sealed class MarketDataHubTests
{
    [Fact]
    public async Task SubscribeSymbolsAsync_NormalizesTracksGroupsAndReturnsCachedSnapshots()
    {
        var marketDataService = new FakeMarketDataService();
        var symbolRegistry = new FakeSharedSymbolRegistry();
        var groups = new TestGroupManager();
        var hub = new MarketDataHub(
            marketDataService,
            symbolRegistry,
            NullLogger<MarketDataHub>.Instance)
        {
            Context = new TestHubCallerContext("conn-market"),
            Groups = groups
        };

        marketDataService.SetLatestPrice("BTCUSDT", 64010.25m, At(0), "unit-test");
        symbolRegistry.SetSymbolMetadata("BTCUSDT", "BTC", "USDT", 0.01m, 0.0001m, "TRADING", true);
        symbolRegistry.SetSymbolMetadata("ETHUSDT", "ETH", "USDT", 0.01m, 0.001m, "BREAK", false);

        var snapshots = await hub.SubscribeSymbolsAsync([" btcusdt ", "ETHUSDT", "BTCUSDT"]);

        var btcSnapshot = Assert.Single(snapshots, snapshot => snapshot.Symbol == "BTCUSDT");
        var ethSnapshot = Assert.Single(snapshots, snapshot => snapshot.Symbol == "ETHUSDT");

        Assert.Equal(["BTCUSDT", "ETHUSDT"], marketDataService.TrackedSymbols);
        Assert.Equal(
            ["market-data:BTCUSDT", "market-data:ETHUSDT"],
            groups.AddedGroups.Select(entry => entry.GroupName).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(64010.25m, btcSnapshot.Price);
        Assert.Equal(0.0001m, btcSnapshot.StepSize);
        Assert.False(ethSnapshot.IsTradingEnabled);
        Assert.Equal("BREAK", ethSnapshot.TradingStatus);
    }

    [Fact]
    public async Task GetChartSeedAsync_BackfillsLoadsCandlesAndPrimesIndicatorSnapshot()
    {
        var marketDataService = new FakeMarketDataService();
        var symbolRegistry = new FakeSharedSymbolRegistry();
        var services = CreateChartSeedServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services
        };
        var hub = new MarketDataHub(
            marketDataService,
            symbolRegistry,
            NullLogger<MarketDataHub>.Instance)
        {
            Context = new TestHubCallerContext("conn-market-seed", httpContext)
        };

        var snapshot = await hub.GetChartSeedAsync(" btcusdt ", "1m", 34);

        Assert.Equal("BTCUSDT", snapshot.Symbol);
        Assert.Equal("1m", snapshot.Timeframe);
        Assert.Equal(34, snapshot.Candles.Count);
        Assert.NotNull(snapshot.IndicatorSnapshot);
        Assert.Equal(IndicatorDataState.Ready, snapshot.IndicatorSnapshot!.State);
        Assert.True(snapshot.IndicatorSnapshot.Macd.IsReady);
        Assert.Equal(["BTCUSDT"], marketDataService.TrackedSymbols);
    }

    private static DateTime At(int minuteOffset)
    {
        return new DateTime(2026, 3, 23, 9, minuteOffset, 0, DateTimeKind.Utc);
    }

    private sealed class FakeMarketDataService : IMarketDataService
    {
        private readonly Dictionary<string, MarketPriceSnapshot> prices = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> TrackedSymbols { get; private set; } = Array.Empty<string>();

        public void SetLatestPrice(string symbol, decimal price, DateTime observedAtUtc, string source)
        {
            prices[symbol] = new MarketPriceSnapshot(symbol, price, observedAtUtc, observedAtUtc, source);
        }

        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TrackedSymbols = [symbol.Trim().ToUpperInvariant()];
            return ValueTask.CompletedTask;
        }

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TrackedSymbols = symbols.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            prices.TryGetValue(symbol, out var snapshot);
            return ValueTask.FromResult<MarketPriceSnapshot?>(snapshot);
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

    private sealed class FakeSharedSymbolRegistry : ISharedSymbolRegistry
    {
        private readonly Dictionary<string, SymbolMetadataSnapshot> metadata = new(StringComparer.Ordinal);

        public void SetSymbolMetadata(
            string symbol,
            string baseAsset,
            string quoteAsset,
            decimal tickSize,
            decimal stepSize,
            string tradingStatus,
            bool isTradingEnabled)
        {
            metadata[symbol] = new SymbolMetadataSnapshot(
                symbol,
                "Binance",
                baseAsset,
                quoteAsset,
                tickSize,
                stepSize,
                tradingStatus,
                isTradingEnabled,
                At(0));
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            metadata.TryGetValue(symbol, out var snapshot);
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(snapshot);
        }

        public ValueTask<IReadOnlyCollection<SymbolMetadataSnapshot>> ListSymbolsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyCollection<SymbolMetadataSnapshot>>(metadata.Values.ToArray());
        }
    }

    private static ServiceProvider CreateChartSeedServiceProvider()
    {
        var services = new ServiceCollection();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var marketDataService = new FakeMarketDataService();
        var indicatorDataService = new IndicatorDataService(
            marketDataService,
            new IndicatorStreamHub(),
            Options.Create(new IndicatorEngineOptions()),
            NullLogger<IndicatorDataService>.Instance);
        var backfillCandles = Enumerable.Range(0, 34)
            .Select(CreateHistoricalCandle)
            .ToArray();
        var gapFillerService = new HistoricalGapFillerService(
            dbContext,
            new FakeHistoricalKlineClient(backfillCandles),
            indicatorDataService,
            Options.Create(new HistoricalGapFillerOptions
            {
                Enabled = true,
                ScanIntervalMinutes = 5,
                LookbackCandles = 34,
                MaxCandlesPerRequest = 50,
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
            new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 23, 9, 34, 0, TimeSpan.Zero)),
            NullLogger<HistoricalGapFillerService>.Instance);

        services.AddSingleton(dbContext);
        services.AddSingleton<IIndicatorDataService>(indicatorDataService);
        services.AddSingleton(gapFillerService);

        return services.BuildServiceProvider();
    }

    private static MarketCandleSnapshot CreateHistoricalCandle(int minuteOffset)
    {
        var openTimeUtc = At(minuteOffset);
        var closeTimeUtc = openTimeUtc.AddMinutes(1).AddMilliseconds(-1);

        return new MarketCandleSnapshot(
            "BTCUSDT",
            "1m",
            openTimeUtc,
            closeTimeUtc,
            OpenPrice: 64000m,
            HighPrice: 64100m,
            LowPrice: 63950m,
            ClosePrice: 64050m,
            Volume: 12m,
            IsClosed: true,
            ReceivedAtUtc: closeTimeUtc,
            Source: "UnitTest.Backfill");
    }

    private sealed class TestHubCallerContext(string connectionId, HttpContext? httpContext = null) : HubCallerContext
    {
        private readonly IDictionary<object, object?> items = new Dictionary<object, object?>();
        private readonly CancellationTokenSource cancellationTokenSource = new();

        public override string ConnectionId { get; } = connectionId;

        public override string? UserIdentifier => "user-market";

        public override ClaimsPrincipal? User => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-market")], "Test"));

        public override IDictionary<object, object?> Items => items;

        public override IFeatureCollection Features { get; } = CreateFeatures(httpContext);

        public override CancellationToken ConnectionAborted => cancellationTokenSource.Token;

        public override void Abort()
        {
            cancellationTokenSource.Cancel();
        }

        private static IFeatureCollection CreateFeatures(HttpContext? httpContext)
        {
            var features = new FeatureCollection();

            if (httpContext is not null)
            {
                features.Set<IHttpContextFeature>(new TestHttpContextFeature
                {
                    HttpContext = httpContext
                });
            }

            return features;
        }
    }

    private sealed class TestGroupManager : IGroupManager
    {
        public List<(string ConnectionId, string GroupName)> AddedGroups { get; } = [];

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            AddedGroups.Add((connectionId, groupName));
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
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
                .Take(limit)
                .ToArray();

            return Task.FromResult<IReadOnlyCollection<MarketCandleSnapshot>>(results);
        }
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class TestHttpContextFeature : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }
}
