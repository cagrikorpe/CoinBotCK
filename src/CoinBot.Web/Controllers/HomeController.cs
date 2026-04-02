using System.Diagnostics;
using System.Security.Claims;
using CoinBot.Application.Abstractions.Dashboard;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Web.Hubs;
using Microsoft.AspNetCore.Mvc;
using CoinBot.Web.Models;
using CoinBot.Web.ViewModels.Home;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace CoinBot.Web.Controllers;

[Authorize]
public class HomeController(
    IUserExchangeCommandCenterService userExchangeCommandCenterService,
    IUserDashboardPortfolioReadModelService userDashboardPortfolioReadModelService,
    IUserDashboardOperationsReadModelService userDashboardOperationsReadModelService,
    IMarketDataService marketDataService,
    ISharedSymbolRegistry symbolRegistry,
    IOptions<BinanceMarketDataOptions> marketDataOptions,
    ILogger<HomeController> logger) : Controller
{
    private static readonly string[] DefaultDashboardSymbols = ["BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT"];

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var userId = HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        UserDashboardPortfolioSnapshot? portfolioSnapshot = null;
        OperationsSummaryViewModel operationsSummary = BuildAnonymousOperationsSummary();
        if (!string.IsNullOrWhiteSpace(userId))
        {
            ViewData["DashboardExchangeSnapshot"] = await userExchangeCommandCenterService.GetSnapshotAsync(userId, cancellationToken);
            portfolioSnapshot = await userDashboardPortfolioReadModelService.GetSnapshotAsync(userId, cancellationToken);
            operationsSummary = MapOperationsSummary(
                await userDashboardOperationsReadModelService.GetSnapshotAsync(userId, cancellationToken));
            logger.LogInformation(
                "Dashboard portfolio snapshot loaded. UserKey={UserKey} ActiveAccounts={ActiveAccounts} Balances={Balances} Positions={Positions} SyncStatus={SyncStatus}.",
                CreateUserKey(userId),
                portfolioSnapshot.ActiveAccountCount,
                portfolioSnapshot.Balances.Count,
                portfolioSnapshot.Positions.Count,
                portfolioSnapshot.SyncStatusLabel);
        }

        // 1. Market Tickers (Canlı Fiyatlar)
        var symbols = ResolveDashboardSymbols(
            marketDataOptions.Value.SeedSymbols,
            portfolioSnapshot?.Positions.Select(item => item.Symbol));
        await marketDataService.TrackSymbolsAsync(symbols, cancellationToken);

        var tickers = new List<DashboardMarketTickerViewModel>(symbols.Count);
        foreach (var symbol in symbols)
        {
            var latestPrice = await marketDataService.GetLatestPriceAsync(symbol, cancellationToken);
            var metadata = await symbolRegistry.GetSymbolAsync(symbol, cancellationToken);
            tickers.Add(MarketDataHub.CreateSnapshot(symbol, latestPrice, metadata));
        }

        // 2. KPI Kartları (Üst Özet Alanı)
        var kpis = BuildPortfolioKpis(portfolioSnapshot);

        // 3. AI Akışı (Sinyal Takip)
        var aiFeed = new List<AiFeedItemViewModel>
        {
            new("09:41", "BTCUSDT", "Long", "84%", "Trend devamı + hacim teyidi", "success", false),
            new("09:33", "ETHUSDT", "Watch", "61%", "Momentum var, giriş bekleniyor", "warning", false),
            new("09:25", "SOLUSDT", "Short", "72%", "Direnç reddi + zayıf funding", "danger", true)
        };

        // 4. Açık Pozisyonlar (İşlem Takip)
        var positions = await BuildOpenPositionsAsync(portfolioSnapshot, marketDataService, cancellationToken);
        
        return View(new DashboardViewModel(tickers, "/hubs/market-data", "/hubs/operations", kpis, operationsSummary, aiFeed, positions));
    }

    [HttpGet]
    public async Task<IActionResult> OperationsSummary(CancellationToken cancellationToken)
    {
        var userId = HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var snapshot = await userDashboardOperationsReadModelService.GetSnapshotAsync(userId, cancellationToken);
        return Json(MapOperationsSummary(snapshot));
    }

    [AllowAnonymous]
    public IActionResult Privacy() => View();

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });

    private static List<KpiItemViewModel> BuildPortfolioKpis(UserDashboardPortfolioSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return
            [
                new("Cuzdan bakiyesi", "-", "Kullanici oturumu olmadan private portfoy okunmaz.", "neutral", "Balance"),
                new("Kullanilabilir bakiye", "-", "Aktif Binance hesabi baglandiginda burada gorunur.", "neutral", "Available"),
                new("Acik pozisyon", "0", "Gercek exchange pozisyonlari buraya yazilir.", "neutral", "Positions"),
                new("Hesap senkronu", "Yok", "Canli private sync bekleniyor.", "neutral", "Sync")
            ];
        }

        var primaryBalance = SelectPrimaryBalance(snapshot.Balances);
        var availableBalance = primaryBalance?.AvailableBalance ?? primaryBalance?.CrossWalletBalance;
        var longCount = snapshot.Positions.Count(item => ResolveDirection(item) == "Long");
        var shortCount = snapshot.Positions.Count(item => ResolveDirection(item) == "Short");
        var syncTag = snapshot.ActiveAccountCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var syncValue = snapshot.LastSynchronizedAtUtc.HasValue ? "Hazir" : "Bekliyor";

        return
        [
            new(
                "Cuzdan bakiyesi",
                FormatAssetValue(primaryBalance?.WalletBalance, primaryBalance?.Asset),
                primaryBalance is null
                    ? "Exchange balance sync henuz veri yazmadi."
                    : $"{snapshot.Balances.Count} bakiye satiri · {snapshot.SyncStatusLabel}",
                snapshot.Balances.Count > 0 ? "positive" : "neutral",
                primaryBalance?.Asset ?? "Balance"),
            new(
                "Kullanilabilir bakiye",
                FormatAssetValue(availableBalance, primaryBalance?.Asset),
                primaryBalance is null
                    ? "Available balance henuz gelmedi."
                    : $"Max withdraw {(primaryBalance.MaxWithdrawAmount.HasValue ? primaryBalance.MaxWithdrawAmount.Value.ToString("0.####") : "-")} {primaryBalance.Asset}",
                availableBalance.HasValue ? "positive" : "neutral",
                "Available"),
            new(
                "Acik pozisyon",
                snapshot.Positions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                snapshot.Positions.Count == 0
                    ? "Aktif futures pozisyonu yok."
                    : $"{longCount} long • {shortCount} short",
                snapshot.Positions.Count > 0 ? "positive" : "neutral",
                "Positions"),
            new(
                "Hesap senkronu",
                syncValue,
                snapshot.SyncStatusLabel,
                snapshot.SyncStatusTone,
                $"{syncTag} hesap")
        ];
    }

    private static IReadOnlyCollection<string> ResolveDashboardSymbols(
        IEnumerable<string>? configuredSymbols,
        IEnumerable<string>? dynamicSymbols = null)
    {
        var symbols = new List<string>(DefaultDashboardSymbols.Length);
        var seenSymbols = new HashSet<string>(StringComparer.Ordinal);
        AppendSymbols(configuredSymbols, symbols, seenSymbols);
        AppendSymbols(dynamicSymbols, symbols, seenSymbols);
        AppendSymbols(DefaultDashboardSymbols, symbols, seenSymbols);
        return symbols.ToArray();
    }

    private static void AppendSymbols(IEnumerable<string>? candidates, List<string> symbols, HashSet<string> seenSymbols)
    {
        if (candidates is null) return;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var normalizedSymbol = candidate.Trim().ToUpperInvariant();
            if (seenSymbols.Add(normalizedSymbol)) symbols.Add(normalizedSymbol);
        }
    }

    private static UserDashboardBalanceSnapshot? SelectPrimaryBalance(IReadOnlyCollection<UserDashboardBalanceSnapshot> balances)
    {
        var preferredAssets = new[] { "USDT", "USDC", "FDUSD", "BUSD" };

        foreach (var asset in preferredAssets)
        {
            var match = balances.FirstOrDefault(item => string.Equals(item.Asset, asset, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return balances
            .OrderByDescending(item => item.WalletBalance)
            .ThenBy(item => item.Asset, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static string FormatAssetValue(decimal? value, string? asset)
    {
        if (!value.HasValue || string.IsNullOrWhiteSpace(asset))
        {
            return "-";
        }

        return $"{value.Value:0.####} {asset}";
    }

    private async Task<List<OpenPositionViewModel>> BuildOpenPositionsAsync(
        UserDashboardPortfolioSnapshot? snapshot,
        IMarketDataService marketDataService,
        CancellationToken cancellationToken)
    {
        if (snapshot is null || snapshot.Positions.Count == 0)
        {
            return [];
        }

        var rows = new List<OpenPositionViewModel>(snapshot.Positions.Count);

        foreach (var position in snapshot.Positions)
        {
            var latestPrice = await marketDataService.GetLatestPriceAsync(position.Symbol, cancellationToken);
            var direction = ResolveDirection(position);
            rows.Add(new OpenPositionViewModel(
                position.Symbol,
                direction,
                direction == "Long" ? "success" : "danger",
                $"{Math.Abs(position.Quantity):0.####}",
                position.EntryPrice.ToString("0.####"),
                latestPrice?.Price.ToString("0.####") ?? "-",
                $"{(position.UnrealizedProfit >= 0 ? "+" : string.Empty)}{position.UnrealizedProfit:0.####}",
                position.UnrealizedProfit >= 0 ? "positive" : "negative",
                NormalizeMarginLabel(position.MarginType),
                position.MarginType.Equals("isolated", StringComparison.OrdinalIgnoreCase) ? "warning" : "success",
                BuildRelativeLabel(position.SyncedAtUtc)));
        }

        return rows;
    }

    private static string ResolveDirection(UserDashboardPositionSnapshot snapshot)
    {
        if (snapshot.PositionSide.Equals("LONG", StringComparison.OrdinalIgnoreCase))
        {
            return "Long";
        }

        if (snapshot.PositionSide.Equals("SHORT", StringComparison.OrdinalIgnoreCase))
        {
            return "Short";
        }

        return snapshot.Quantity >= 0 ? "Long" : "Short";
    }

    private static string NormalizeMarginLabel(string marginType)
    {
        return marginType.Equals("isolated", StringComparison.OrdinalIgnoreCase)
            ? "Isolated"
            : "Cross";
    }

    private static string BuildRelativeLabel(DateTime timestampUtc)
    {
        var age = DateTime.UtcNow - timestampUtc;

        if (age.TotalMinutes < 1)
        {
            return "az once";
        }

        if (age.TotalHours < 1)
        {
            return $"{Math.Max(1, (int)Math.Floor(age.TotalMinutes))} dk once";
        }

        return $"{Math.Max(1, (int)Math.Floor(age.TotalHours))} sa once";
    }

    private static string CreateUserKey(string userId)
    {
        var normalizedValue = userId.Trim();
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedValue));
        return Convert.ToHexString(hashBytes[..6]);
    }

    private static OperationsSummaryViewModel BuildAnonymousOperationsSummary()
    {
        return new OperationsSummaryViewModel(
            0,
            0,
            0,
            "Idle",
            null,
            "N/A",
            null,
            "Unknown",
            "neutral",
            "Unknown",
            "neutral",
            "Closed",
            "neutral",
            0,
            "Risk profili okunamadi",
            "Pozisyon limiti bekleniyor",
            "Cooldown bilgisi yok");
    }

    private static OperationsSummaryViewModel MapOperationsSummary(UserDashboardOperationsSummarySnapshot snapshot)
    {
        var dailyLossSummary = snapshot.MaxDailyLossPercentage.HasValue && snapshot.CurrentDailyLossPercentage.HasValue
            ? $"{snapshot.CurrentDailyLossPercentage.Value:0.##}% / {snapshot.MaxDailyLossPercentage.Value:0.##}%"
            : snapshot.MaxDailyLossPercentage.HasValue
                ? $"0% / {snapshot.MaxDailyLossPercentage.Value:0.##}%"
                : "Risk profili yok";
        var positionLimitSummary = snapshot.MaxOpenPositions > 0
            ? $"{snapshot.OpenPositionCount} / {snapshot.MaxOpenPositions}"
            : snapshot.OpenPositionCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var cooldownSummary = $"Bot {snapshot.ActiveBotCooldownCount} • Symbol {snapshot.ActiveSymbolCooldownCount}";

        return new OperationsSummaryViewModel(
            snapshot.EnabledBotCount,
            snapshot.EnabledSymbolCount,
            snapshot.ConflictedSymbolCount,
            snapshot.LastJobStatus,
            snapshot.LastJobErrorCode,
            snapshot.LastExecutionState,
            snapshot.LastExecutionFailureCode,
            snapshot.WorkerHealthLabel,
            snapshot.WorkerHealthTone,
            snapshot.PrivateStreamHealthLabel,
            snapshot.PrivateStreamHealthTone,
            snapshot.BreakerLabel,
            snapshot.BreakerTone,
            snapshot.OpenCircuitBreakerCount,
            dailyLossSummary,
            positionLimitSummary,
            cooldownSummary);
    }
}
