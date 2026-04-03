using System.Runtime.CompilerServices;
using System.Security.Claims;
using CoinBot.Application.Abstractions.Dashboard;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Settings;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Web.Controllers;
using CoinBot.Web.ViewModels.Home;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Web;

public sealed class HomeControllerTests
{
    [Fact]
    public async Task Index_BuildsDashboardMarketTickerModel_FromCentralMarketDataServices()
    {
        var marketDataService = new FakeMarketDataService();
        var symbolRegistry = new FakeSharedSymbolRegistry();
        var exchangeService = new FakeUserExchangeCommandCenterService();
        var dashboardService = new FakeUserDashboardPortfolioReadModelService();
        var operationsService = new FakeUserDashboardOperationsReadModelService();
        var settingsService = new FakeUserSettingsService();
        var controller = new HomeController(
            exchangeService,
            dashboardService,
            operationsService,
            settingsService,
            marketDataService,
            symbolRegistry,
            Options.Create(new BinanceMarketDataOptions
            {
                SeedSymbols = ["btcusdt", "ethusdt", "solusdt"]
            }),
            NullLogger<HomeController>.Instance);
        controller.ControllerContext = CreateControllerContext("user-01");

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
        Assert.Equal("/hubs/operations", model.OperationsHubPath);
        Assert.Equal(["BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT"], marketDataService.TrackedSymbols);
        Assert.Same(exchangeService.Snapshot, controller.ViewData["DashboardExchangeSnapshot"]);
        Assert.Equal("Cuzdan bakiyesi", model.Kpis[0].Label);
        Assert.Equal(2, model.OperationsSummary.EnabledBotCount);
        Assert.Contains("Heartbeat drift", model.OperationsSummary.DriftSummary, StringComparison.Ordinal);
        Assert.Single(model.OpenPositions);
        Assert.Equal("XRPUSDT", model.OpenPositions[0].Symbol);
        Assert.Equal(64000.50m, btcTicker.Price);
        Assert.Equal("TRADING", btcTicker.TradingStatus);
        Assert.Equal(0.0001m, btcTicker.StepSize);
        Assert.False(solTicker.IsTradingEnabled);
        Assert.Equal("BREAK", solTicker.TradingStatus);
    }

    [Fact]
    public async Task Index_SkipsExchangeSnapshot_WhenUserIdIsMissing()
    {
        var controller = new HomeController(
            new FakeUserExchangeCommandCenterService(),
            new FakeUserDashboardPortfolioReadModelService(),
            new FakeUserDashboardOperationsReadModelService(),
            new FakeUserSettingsService(),
            new FakeMarketDataService(),
            new FakeSharedSymbolRegistry(),
            Options.Create(new BinanceMarketDataOptions()),
            NullLogger<HomeController>.Instance);

        var result = await controller.Index(CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.Null(controller.ViewData["DashboardExchangeSnapshot"]);
    }

    private static DateTime At(int minuteOffset)
    {
        return new DateTime(2026, 3, 23, 9, minuteOffset, 0, DateTimeKind.Utc);
    }

    private static ControllerContext CreateControllerContext(string userId)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId)],
                "TestAuth"));
        return new ControllerContext
        {
            HttpContext = httpContext
        };
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

    private sealed class FakeUserExchangeCommandCenterService : IUserExchangeCommandCenterService
    {
        public UserExchangeCommandCenterSnapshot Snapshot { get; } = new(
            "user-01",
            "User One",
            new UserExchangeEnvironmentSummary(ExecutionEnvironment.Demo, "Demo", "info", "Global varsayılan", "Demo mode", false),
            new UserExchangeRiskOverrideSummary("Core", 2m, 10m, 3m, false, false, false, null, null, null, "Risk tamam", "healthy", "Profil tamam"),
            [],
            [],
            At(0));

        public Task<UserExchangeCommandCenterSnapshot> GetSnapshotAsync(string userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Snapshot);
        }

        public Task<ConnectUserBinanceCredentialResult> ConnectBinanceAsync(ConnectUserBinanceCredentialRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeUserDashboardPortfolioReadModelService : IUserDashboardPortfolioReadModelService
    {
        public UserDashboardPortfolioSnapshot Snapshot { get; } = new(
            1,
            "Canli senkron bagli",
            "positive",
            At(0),
            12.5m,
            15.5m,
            28m,
            "PnL consistent. Realized=12.5; Unrealized=15.5; Total=28; LedgerDelta=12.5.",
            [
                new UserDashboardBalanceSnapshot("USDT", 1500m, 1490m, 1200m, 1200m, At(0), At(0))
            ],
            [
                new UserDashboardPositionSnapshot("XRPUSDT", "LONG", 25m, 0.51m, 0.51m, 15.5m, "cross", 0m, At(0), At(0))
            ],
            []);

        public Task<UserDashboardPortfolioSnapshot> GetSnapshotAsync(string userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class FakeUserDashboardOperationsReadModelService : IUserDashboardOperationsReadModelService
    {
        public UserDashboardOperationsSummarySnapshot Snapshot { get; } = new(
            2,
            2,
            0,
            "Succeeded",
            null,
            "Filled",
            null,
            "Healthy",
            "positive",
            "Healthy",
            "positive",
            "Closed",
            "positive",
            0,
            1.5m,
            10m,
            1,
            3,
            1,
            1,
            At(0),
            "Heartbeat drift 2234 / 2000 ms • Server probe 80 ms • Last probe 09:00:00 UTC",
            "Execution block kaynağı market-data heartbeat. Server-time refresh signed REST offset'ini yeniler.");

        public Task<UserDashboardOperationsSummarySnapshot> GetSnapshotAsync(string userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class FakeUserSettingsService : IUserSettingsService
    {
        public UserSettingsSnapshot Snapshot { get; } = new(
            "UTC",
            "UTC",
            "UTC",
            [new UserTimeZoneOptionSnapshot("UTC", "UTC")],
            new BinanceTimeSyncSnapshot(
                At(0),
                At(0),
                0,
                10,
                At(0),
                "Synchronized",
                null));

        public Task<UserSettingsSnapshot?> GetAsync(string userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<UserSettingsSnapshot?>(Snapshot);
        }

        public Task<UserSettingsSaveResult> SaveAsync(string userId, UserSettingsSaveCommand command, string actor, string? correlationId = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
