using System.Runtime.CompilerServices;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Web.Controllers;
using CoinBot.Web.ViewModels.Home;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Web;

public sealed class HomeControllerTests
{
    [Fact]
    public async Task Index_BuildsDashboardMarketTickerModel_FromCentralMarketDataServices()
    {
        var marketDataService = new FakeMarketDataService();
        var symbolRegistry = new FakeSharedSymbolRegistry();
        var controller = new HomeController(
            marketDataService,
            symbolRegistry,
            Options.Create(new BinanceMarketDataOptions
            {
                SeedSymbols = ["btcusdt", "ethusdt", "solusdt"]
            }));

        marketDataService.SetLatestPrice("BTCUSDT", 64000.50m, At(0), "unit-test");
        symbolRegistry.SetSymbolMetadata("BTCUSDT", "BTC", "USDT", 0.01m, 0.0001m, "TRADING", true);
        symbolRegistry.SetSymbolMetadata("ETHUSDT", "ETH", "USDT", 0.01m, 0.001m, "TRADING", true);
        symbolRegistry.SetSymbolMetadata("SOLUSDT", "SOL", "USDT", 0.001m, 0.01m, "BREAK", false);

        var result = await controller.Index(CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DashboardViewModel>(viewResult.Model);
        var btcTicker = Assert.Single(model.MarketTickers, ticker => ticker.Symbol == "BTCUSDT");
        var solTicker = Assert.Single(model.MarketTickers, ticker => ticker.Symbol == "SOLUSDT");

        Assert.Equal("/hubs/market-data", model.MarketDataHubPath);
        Assert.Equal(["BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT"], marketDataService.TrackedSymbols);
        Assert.Equal(64000.50m, btcTicker.Price);
        Assert.Equal("TRADING", btcTicker.TradingStatus);
        Assert.Equal(0.0001m, btcTicker.StepSize);
        Assert.False(solTicker.IsTradingEnabled);
        Assert.Equal("BREAK", solTicker.TradingStatus);
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
            TrackedSymbols = symbols
                .Select(symbol => symbol.Trim().ToUpperInvariant())
                .ToArray();
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
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeSharedSymbolRegistry : ISharedSymbolRegistry
    {
        private readonly Dictionary<string, SymbolMetadataSnapshot> snapshots = new(StringComparer.Ordinal);

        public void SetSymbolMetadata(
            string symbol,
            string baseAsset,
            string quoteAsset,
            decimal tickSize,
            decimal stepSize,
            string tradingStatus,
            bool isTradingEnabled)
        {
            snapshots[symbol] = new SymbolMetadataSnapshot(
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
            snapshots.TryGetValue(symbol, out var snapshot);
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(snapshot);
        }

        public ValueTask<IReadOnlyCollection<SymbolMetadataSnapshot>> ListSymbolsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyCollection<SymbolMetadataSnapshot>>(snapshots.Values.ToArray());
        }
    }
}
