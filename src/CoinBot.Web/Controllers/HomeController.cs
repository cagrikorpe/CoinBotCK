using System.Diagnostics;
using System.Security.Claims;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Web.Hubs;
using Microsoft.AspNetCore.Mvc;
using CoinBot.Web.Models;
using CoinBot.Web.ViewModels.Home;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace CoinBot.Web.Controllers;

[Authorize]
public class HomeController(
    IUserExchangeCommandCenterService userExchangeCommandCenterService,
    IMarketDataService marketDataService,
    ISharedSymbolRegistry symbolRegistry,
    IOptions<BinanceMarketDataOptions> marketDataOptions) : Controller
{
    private static readonly string[] DefaultDashboardSymbols = ["BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT"];

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var userId = HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            ViewData["DashboardExchangeSnapshot"] = await userExchangeCommandCenterService.GetSnapshotAsync(userId, cancellationToken);
        }

        // 1. Market Tickers (Canlı Fiyatlar)
        var symbols = ResolveDashboardSymbols(marketDataOptions.Value.SeedSymbols);
        await marketDataService.TrackSymbolsAsync(symbols, cancellationToken);

        var tickers = new List<DashboardMarketTickerViewModel>(symbols.Count);
        foreach (var symbol in symbols)
        {
            var latestPrice = await marketDataService.GetLatestPriceAsync(symbol, cancellationToken);
            var metadata = await symbolRegistry.GetSymbolAsync(symbol, cancellationToken);
            tickers.Add(MarketDataHub.CreateSnapshot(symbol, latestPrice, metadata));
        }

        // 2. KPI Kartları (Üst Özet Alanı)
        var kpis = new List<KpiItemViewModel>
        {
            new("Toplam bakiye", "$ 24,860.42", "Başlangıçtan +4.8%", "positive", "Equity"),
            new("Toplam PnL", "+$ 2,184.30", "Realized + Açık", "positive", "+9.6%"),
            new("Günlük PnL", "+$ 412.08", "Bugün 7 sinyal", "positive", "+1.7%"),
            new("Aktif Bot", "3", "1 paper • 2 live", "neutral", "Bots")
        };

        // 3. AI Akışı (Sinyal Takip)
        var aiFeed = new List<AiFeedItemViewModel>
        {
            new("09:41", "BTCUSDT", "Long", "84%", "Trend devamı + hacim teyidi", "success", false),
            new("09:33", "ETHUSDT", "Watch", "61%", "Momentum var, giriş bekleniyor", "warning", false),
            new("09:25", "SOLUSDT", "Short", "72%", "Direnç reddi + zayıf funding", "danger", true)
        };

        // 4. Açık Pozisyonlar (İşlem Takip)
        var positions = new List<OpenPositionViewModel>
        {
            new OpenPositionViewModel("BTCUSDT", "Long", "success", "3x", "63,540", "64,180", "+$ 142.70", "positive", "Düşük", "success", "12 sn önce"),
            new OpenPositionViewModel("ETHUSDT", "Short", "danger", "5x", "3,212", "3,168", "+$ 58.14", "positive", "Orta", "warning", "27 sn önce")
        };
        
        return View(new DashboardViewModel(tickers, "/hubs/market-data", kpis, aiFeed, positions));
    }

    [AllowAnonymous]
    public IActionResult Privacy() => View();

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });

    private static IReadOnlyCollection<string> ResolveDashboardSymbols(IEnumerable<string>? configuredSymbols)
    {
        var symbols = new List<string>(DefaultDashboardSymbols.Length);
        var seenSymbols = new HashSet<string>(StringComparer.Ordinal);
        AppendSymbols(configuredSymbols, symbols, seenSymbols);
        AppendSymbols(DefaultDashboardSymbols, symbols, seenSymbols);
        return symbols.Take(DefaultDashboardSymbols.Length).ToArray();
    }

    private static void AppendSymbols(IEnumerable<string>? candidates, List<string> symbols, HashSet<string> seenSymbols)
    {
        if (candidates is null) return;
        foreach (var candidate in candidates)
        {
            if (symbols.Count >= DefaultDashboardSymbols.Length) return;
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var normalizedSymbol = candidate.Trim().ToUpperInvariant();
            if (seenSymbols.Add(normalizedSymbol)) symbols.Add(normalizedSymbol);
        }
    }
}
