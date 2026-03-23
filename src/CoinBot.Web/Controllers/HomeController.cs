using System.Diagnostics;
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
    IMarketDataService marketDataService,
    ISharedSymbolRegistry symbolRegistry,
    IOptions<BinanceMarketDataOptions> marketDataOptions) : Controller
{
    private static readonly string[] DefaultDashboardSymbols = ["BTCUSDT", "ETHUSDT", "SOLUSDT"];

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var symbols = ResolveDashboardSymbols(marketDataOptions.Value.SeedSymbols);

        await marketDataService.TrackSymbolsAsync(symbols, cancellationToken);

        var tickers = new List<DashboardMarketTickerViewModel>(symbols.Count);

        foreach (var symbol in symbols)
        {
            var latestPrice = await marketDataService.GetLatestPriceAsync(symbol, cancellationToken);
            var metadata = await symbolRegistry.GetSymbolAsync(symbol, cancellationToken);

            tickers.Add(MarketDataHub.CreateSnapshot(symbol, latestPrice, metadata));
        }

        return View(new DashboardViewModel(tickers, "/hubs/market-data"));
    }

    [AllowAnonymous]
    public IActionResult Privacy()
    {
        return View();
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }

    private static IReadOnlyCollection<string> ResolveDashboardSymbols(IEnumerable<string>? configuredSymbols)
    {
        var symbols = new List<string>(DefaultDashboardSymbols.Length);
        var seenSymbols = new HashSet<string>(StringComparer.Ordinal);

        AppendSymbols(configuredSymbols, symbols, seenSymbols);
        AppendSymbols(DefaultDashboardSymbols, symbols, seenSymbols);

        return symbols
            .Take(DefaultDashboardSymbols.Length)
            .ToArray();
    }

    private static void AppendSymbols(
        IEnumerable<string>? candidates,
        List<string> symbols,
        HashSet<string> seenSymbols)
    {
        if (candidates is null)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            if (symbols.Count >= DefaultDashboardSymbols.Length)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var normalizedSymbol = candidate.Trim().ToUpperInvariant();

            if (seenSymbols.Add(normalizedSymbol))
            {
                symbols.Add(normalizedSymbol);
            }
        }
    }
}
