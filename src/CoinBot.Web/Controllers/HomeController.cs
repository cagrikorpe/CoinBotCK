using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CoinBot.Application.Abstractions.Dashboard;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Settings;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Web.Hubs;
using CoinBot.Web.Models;
using CoinBot.Web.ViewModels.Home;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Web.Controllers;

[Authorize]
public class HomeController(
    IUserExchangeCommandCenterService userExchangeCommandCenterService,
    IUserDashboardPortfolioReadModelService userDashboardPortfolioReadModelService,
    IUserDashboardOperationsReadModelService userDashboardOperationsReadModelService,
    IUserDashboardLiveReadModelService userDashboardLiveReadModelService,
    IUserSettingsService userSettingsService,
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
        UserDashboardLiveSnapshot? liveSnapshot = null;
        OperationsSummaryViewModel operationsSummary = BuildAnonymousOperationsSummary();
        var displayTimeZoneInfo = TimeZoneInfo.Utc;
        var displayTimeZoneJavaScriptId = "UTC";

        if (!string.IsNullOrWhiteSpace(userId))
        {
            ViewData["DashboardExchangeSnapshot"] = await userExchangeCommandCenterService.GetSnapshotAsync(userId, cancellationToken);
            portfolioSnapshot = await userDashboardPortfolioReadModelService.GetSnapshotAsync(userId, cancellationToken);
            liveSnapshot = await userDashboardLiveReadModelService.GetSnapshotAsync(userId, cancellationToken);
            operationsSummary = MapOperationsSummary(
                await userDashboardOperationsReadModelService.GetSnapshotAsync(userId, cancellationToken),
                liveSnapshot);
            var settingsSnapshot = await userSettingsService.GetAsync(userId, cancellationToken);
            displayTimeZoneInfo = ResolveTimeZone(settingsSnapshot?.PreferredTimeZoneId);
            displayTimeZoneJavaScriptId = settingsSnapshot?.PreferredTimeZoneJavaScriptId ?? "UTC";
            logger.LogInformation(
                "Dashboard portfolio snapshot loaded. UserKey={UserKey} ActiveAccounts={ActiveAccounts} Balances={Balances} Positions={Positions} SyncStatus={SyncStatus} RecentAi={RecentAi}.",
                CreateUserKey(userId),
                portfolioSnapshot.ActiveAccountCount,
                portfolioSnapshot.Balances.Count,
                portfolioSnapshot.Positions.Count,
                portfolioSnapshot.SyncStatusLabel,
                liveSnapshot.AiHistory.Count);
        }

        ViewData["DashboardDisplayTimeZone"] = displayTimeZoneInfo;

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

        var kpis = BuildPortfolioKpis(portfolioSnapshot);
        var aiFeed = BuildAiFeed(liveSnapshot, displayTimeZoneInfo);
        var positions = await BuildOpenPositionsAsync(portfolioSnapshot, marketDataService, displayTimeZoneInfo, cancellationToken);
        var recentOrders = BuildRecentOrders(portfolioSnapshot, displayTimeZoneInfo);
        var performance = BuildPerformance(portfolioSnapshot, displayTimeZoneInfo);

        return View(new DashboardViewModel(
            tickers,
            "/hubs/market-data",
            "/hubs/operations",
            displayTimeZoneInfo.Id,
            displayTimeZoneJavaScriptId,
            displayTimeZoneInfo.DisplayName,
            kpis,
            operationsSummary,
            performance,
            aiFeed,
            recentOrders,
            positions));
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
        var liveSnapshot = await userDashboardLiveReadModelService.GetSnapshotAsync(userId, cancellationToken);
        return Json(MapOperationsSummary(snapshot, liveSnapshot));
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
        var syncTag = snapshot.ActiveAccountCount.ToString(CultureInfo.InvariantCulture);
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
                snapshot.Positions.Count.ToString(CultureInfo.InvariantCulture),
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

    private static List<AiFeedItemViewModel> BuildAiFeed(
        UserDashboardLiveSnapshot? liveSnapshot,
        TimeZoneInfo displayTimeZoneInfo)
    {
        if (liveSnapshot is null || liveSnapshot.AiHistory.Count == 0)
        {
            return [];
        }

        return liveSnapshot.AiHistory
            .OrderByDescending(item => item.EvaluatedAtUtc)
            .Take(8)
            .Select(item => new AiFeedItemViewModel(
                FormatTimestamp(item.EvaluatedAtUtc, displayTimeZoneInfo),
                item.Symbol,
                item.Timeframe,
                item.StrategyDirection,
                item.StrategyConfidenceScore.HasValue ? $"{item.StrategyConfidenceScore.Value}%" : "-",
                item.AiDirection,
                $"{item.AiConfidence:P0}",
                item.AiReasonSummary,
                ResolveAiTone(item),
                item.AiIsFallback,
                item.RiskVetoPresent || item.PilotSafetyBlocked,
                item.FinalAction,
                item.AgreementState,
                item.NoSubmitReason,
                item.HypotheticalBlockReason,
                item.FeatureSnapshotId.HasValue ? item.FeatureSnapshotId.Value.ToString("N")[..8].ToUpperInvariant() : null,
                item.FeatureSummary,
                item.TopSignalHints))
            .ToList();
    }

    private static List<RecentOrderViewModel> BuildRecentOrders(
        UserDashboardPortfolioSnapshot? snapshot,
        TimeZoneInfo displayTimeZoneInfo)
    {
        if (snapshot is null || snapshot.TradeHistory.Count == 0)
        {
            return [];
        }

        return snapshot.TradeHistory
            .OrderByDescending(item => item.LastUpdatedAtUtc)
            .Take(8)
            .Select(item => new RecentOrderViewModel(
                FormatTimestamp(item.LastUpdatedAtUtc, displayTimeZoneInfo),
                item.Symbol,
                item.Side,
                item.FinalState,
                ResolveOrderTone(item),
                BuildFillSummary(item),
                BuildPnlSummary(item),
                BuildReconciliationSummary(item),
                item.ExecutionResultCode,
                item.ExecutionResultSummary,
                item.ReasonChainSummary))
            .ToList();
    }

    private static PerformanceViewModel BuildPerformance(
        UserDashboardPortfolioSnapshot? snapshot,
        TimeZoneInfo displayTimeZoneInfo)
    {
        if (snapshot is null)
        {
            return new PerformanceViewModel(
                "-",
                "-",
                "-",
                "-",
                "Henüz canlı portfolio verisi yok.",
                false,
                "Insufficient live data. Portfolio snapshot bekleniyor.",
                []);
        }

        var primaryBalance = SelectPrimaryBalance(snapshot.Balances);
        var walletBalance = primaryBalance is null
            ? (decimal?)null
            : primaryBalance.CrossWalletBalance != 0m
                ? primaryBalance.CrossWalletBalance
                : primaryBalance.WalletBalance;
        var equityEstimate = walletBalance.HasValue
            ? walletBalance.Value + snapshot.UnrealizedPnl
            : snapshot.TotalPnl;
        var startEquity = walletBalance.HasValue
            ? equityEstimate - snapshot.TotalPnl
            : (decimal?)null;
        var referenceDate = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, displayTimeZoneInfo).Date;
        var dailyRealizedPnl = snapshot.TradeHistory
            .Where(item => TimeZoneInfo.ConvertTimeFromUtc((item.ClosedAtUtc ?? item.LastUpdatedAtUtc).ToUniversalTime(), displayTimeZoneInfo).Date == referenceDate)
            .Sum(item => item.RealizedPnl);
        var closedTradeEffect = snapshot.TradeHistory.Sum(item => item.RealizedPnl);
        var points = BuildPerformancePoints(snapshot, startEquity, equityEstimate, displayTimeZoneInfo);
        var hasSufficientData = startEquity.HasValue && snapshot.TradeHistory.Count > 0 && points.Count >= 2;

        return new PerformanceViewModel(
            FormatAssetValue(equityEstimate, primaryBalance?.Asset),
            FormatSignedAmount(dailyRealizedPnl, primaryBalance?.Asset),
            FormatSignedAmount(snapshot.UnrealizedPnl, primaryBalance?.Asset),
            FormatSignedAmount(closedTradeEffect, primaryBalance?.Asset),
            snapshot.PnlConsistencySummary,
            hasSufficientData,
            hasSufficientData ? string.Empty : "Insufficient live data. Equity curve için en az iki gerçek nokta gerekiyor.",
            points);
    }

    private static List<PerformancePointViewModel> BuildPerformancePoints(
        UserDashboardPortfolioSnapshot snapshot,
        decimal? startEquity,
        decimal currentEquity,
        TimeZoneInfo displayTimeZoneInfo)
    {
        if (!startEquity.HasValue)
        {
            return [];
        }

        var orderedHistory = snapshot.TradeHistory
            .OrderBy(item => item.ClosedAtUtc ?? item.LastUpdatedAtUtc)
            .ToArray();
        var points = new List<PerformancePointViewModel>();
        var runningEquity = startEquity.Value;
        var initialTimestamp = orderedHistory.FirstOrDefault()?.OpenedAtUtc ?? snapshot.LastSynchronizedAtUtc ?? DateTime.UtcNow;
        points.Add(new PerformancePointViewModel(
            FormatTimestamp(initialTimestamp, displayTimeZoneInfo),
            runningEquity.ToString("0.####", CultureInfo.InvariantCulture),
            "Start"));

        foreach (var historyRow in orderedHistory)
        {
            runningEquity += historyRow.RealizedPnl;
            var pointTimestamp = historyRow.ClosedAtUtc ?? historyRow.LastUpdatedAtUtc;
            points.Add(new PerformancePointViewModel(
                FormatTimestamp(pointTimestamp, displayTimeZoneInfo),
                runningEquity.ToString("0.####", CultureInfo.InvariantCulture),
                historyRow.Symbol));
        }

        points.Add(new PerformancePointViewModel(
            FormatTimestamp(snapshot.LastSynchronizedAtUtc ?? DateTime.UtcNow, displayTimeZoneInfo),
            currentEquity.ToString("0.####", CultureInfo.InvariantCulture),
            "Current"));

        return points
            .OrderBy(item => item.Time, StringComparer.Ordinal)
            .TakeLast(8)
            .ToList();
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
        if (candidates is null)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
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
        if (!value.HasValue)
        {
            return "-";
        }

        return string.IsNullOrWhiteSpace(asset)
            ? value.Value.ToString("0.####", CultureInfo.InvariantCulture)
            : $"{value.Value:0.####} {asset}";
    }

    private static string FormatSignedAmount(decimal value, string? asset)
    {
        var prefix = value >= 0m ? "+" : string.Empty;
        return string.IsNullOrWhiteSpace(asset)
            ? $"{prefix}{value:0.####}"
            : $"{prefix}{value:0.####} {asset}";
    }

    private async Task<List<OpenPositionViewModel>> BuildOpenPositionsAsync(
        UserDashboardPortfolioSnapshot? snapshot,
        IMarketDataService marketDataService,
        TimeZoneInfo displayTimeZoneInfo,
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
            var effectiveCurrentPrice = latestPrice?.Price ?? position.MarkPrice;
            var realizedPnl = position.RealizedPnl ?? 0m;
            var marginTone = position.MarginType.Equals("isolated", StringComparison.OrdinalIgnoreCase)
                ? "warning"
                : position.MarginType.Equals("spot", StringComparison.OrdinalIgnoreCase)
                    ? "neutral"
                    : "success";

            rows.Add(new OpenPositionViewModel(
                position.Symbol,
                direction,
                direction == "Long" ? "success" : "danger",
                Math.Abs(position.Quantity).ToString("0.####", CultureInfo.InvariantCulture),
                position.EntryPrice.ToString("0.####", CultureInfo.InvariantCulture),
                position.BreakEvenPrice.ToString("0.####", CultureInfo.InvariantCulture),
                effectiveCurrentPrice?.ToString("0.####", CultureInfo.InvariantCulture) ?? "-",
                FormatSignedAmount(position.UnrealizedProfit, null),
                position.UnrealizedProfit >= 0 ? "positive" : "negative",
                FormatSignedAmount(realizedPnl, null),
                realizedPnl >= 0 ? "positive" : "negative",
                NormalizeMarginLabel(position.MarginType),
                marginTone,
                $"Exchange {FormatTimestamp(position.ExchangeUpdatedAtUtc, displayTimeZoneInfo)}",
                $"Sync {FormatTimestamp(position.SyncedAtUtc, displayTimeZoneInfo)}"));
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
        if (marginType.Equals("isolated", StringComparison.OrdinalIgnoreCase))
        {
            return "Isolated";
        }

        if (marginType.Equals("spot", StringComparison.OrdinalIgnoreCase))
        {
            return "Spot";
        }

        return "Cross";
    }

    private static string FormatTimestamp(DateTime timestampUtc, TimeZoneInfo timeZoneInfo)
    {
        var normalizedTimestamp = timestampUtc.Kind == DateTimeKind.Utc
            ? timestampUtc
            : DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc);
        var localTimestamp = TimeZoneInfo.ConvertTimeFromUtc(normalizedTimestamp, timeZoneInfo);
        return $"{localTimestamp:yyyy-MM-dd HH:mm:ss} {timeZoneInfo.StandardName}";
    }

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
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
            "Cooldown bilgisi yok",
            "Henüz drift snapshot yok",
            "Clock drift summary monitoring snapshot geldikten sonra görünür.",
            "Unconfigured",
            "warning",
            "DemoOnly",
            "warning",
            "ShadowOnly",
            "neutral",
            "Unknown",
            "neutral",
            "Market-data readiness snapshot yok.",
            "Unknown",
            "neutral",
            "Private plane snapshot yok.",
            "NoShadowData",
            "neutral",
            null,
            "Henüz no-submit shadow özeti yok.",
            "NoReject",
            "neutral",
            null,
            "Henüz reject/failure execution kaydı yok.",
            null);
    }

    private static OperationsSummaryViewModel MapOperationsSummary(
        UserDashboardOperationsSummarySnapshot snapshot,
        UserDashboardLiveSnapshot? liveSnapshot)
    {
        var dailyLossSummary = snapshot.MaxDailyLossPercentage.HasValue && snapshot.CurrentDailyLossPercentage.HasValue
            ? $"{snapshot.CurrentDailyLossPercentage.Value:0.##}% / {snapshot.MaxDailyLossPercentage.Value:0.##}%"
            : snapshot.MaxDailyLossPercentage.HasValue
                ? $"0% / {snapshot.MaxDailyLossPercentage.Value:0.##}%"
                : "Risk profili yok";
        var positionLimitSummary = snapshot.MaxOpenPositions > 0
            ? $"{snapshot.OpenPositionCount} / {snapshot.MaxOpenPositions}"
            : snapshot.OpenPositionCount.ToString(CultureInfo.InvariantCulture);
        var cooldownSummary = $"Bot {snapshot.ActiveBotCooldownCount} • Symbol {snapshot.ActiveSymbolCooldownCount}";
        var control = liveSnapshot?.Control;
        var latestNoTrade = liveSnapshot?.LatestNoTrade;
        var latestReject = liveSnapshot?.LatestReject;

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
            cooldownSummary,
            snapshot.DriftSummary,
            snapshot.DriftReason,
            control?.TradeMasterLabel ?? "Unconfigured",
            control?.TradeMasterTone ?? "warning",
            control?.TradingModeLabel ?? "DemoOnly",
            control?.TradingModeTone ?? "warning",
            control?.PilotActivationLabel ?? "ShadowOnly",
            control?.PilotActivationTone ?? "neutral",
            control?.MarketDataLabel ?? "Unknown",
            control?.MarketDataTone ?? "neutral",
            control?.MarketDataSummary ?? "Market-data readiness snapshot yok.",
            control?.PrivatePlaneLabel ?? "Unknown",
            control?.PrivatePlaneTone ?? "neutral",
            control?.PrivatePlaneSummary ?? "Private plane snapshot yok.",
            latestNoTrade?.Label ?? "NoShadowData",
            latestNoTrade?.Tone ?? "neutral",
            latestNoTrade?.Code,
            latestNoTrade?.Summary ?? "Henüz no-submit shadow özeti yok.",
            latestReject?.Label ?? "NoReject",
            latestReject?.Tone ?? "neutral",
            latestReject?.Code,
            latestReject?.Summary ?? "Henüz reject/failure execution kaydı yok.",
            latestReject?.ReconciliationLabel);
    }

    private static string ResolveAiTone(UserDashboardAiHistoryRowSnapshot snapshot)
    {
        if (snapshot.AiIsFallback)
        {
            return "warning";
        }

        if (snapshot.RiskVetoPresent || snapshot.PilotSafetyBlocked || string.Equals(snapshot.FinalAction, "NoSubmit", StringComparison.Ordinal))
        {
            return "danger";
        }

        return snapshot.AiDirection switch
        {
            "Long" => "success",
            "Short" => "danger",
            _ => "neutral"
        };
    }

    private static string ResolveOrderTone(UserDashboardTradeHistoryRowSnapshot snapshot)
    {
        if (snapshot.FinalState.Equals("Filled", StringComparison.OrdinalIgnoreCase))
        {
            return "success";
        }

        if (snapshot.FinalState.Equals("Rejected", StringComparison.OrdinalIgnoreCase) ||
            snapshot.FinalState.Equals("Cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return "danger";
        }

        return "warning";
    }

    private static string BuildFillSummary(UserDashboardTradeHistoryRowSnapshot snapshot)
    {
        var quantityLabel = snapshot.FilledQuantity.HasValue
            ? snapshot.FilledQuantity.Value.ToString("0.####", CultureInfo.InvariantCulture)
            : snapshot.Quantity.ToString("0.####", CultureInfo.InvariantCulture);
        var priceLabel = snapshot.AverageFillPrice.HasValue
            ? snapshot.AverageFillPrice.Value.ToString("0.####", CultureInfo.InvariantCulture)
            : "-";

        return $"Qty {quantityLabel} @ {priceLabel} • Fill {snapshot.FillCount}";
    }

    private static string BuildPnlSummary(UserDashboardTradeHistoryRowSnapshot snapshot)
    {
        var unrealized = snapshot.UnrealizedPnlContribution.HasValue
            ? snapshot.UnrealizedPnlContribution.Value.ToString("0.####", CultureInfo.InvariantCulture)
            : "-";
        var fee = snapshot.FeeAmountInQuote.HasValue
            ? snapshot.FeeAmountInQuote.Value.ToString("0.####", CultureInfo.InvariantCulture)
            : "-";

        return $"Realized {snapshot.RealizedPnl:0.####} • Unrealized {unrealized} • Fee {fee}";
    }

    private static string BuildReconciliationSummary(UserDashboardTradeHistoryRowSnapshot snapshot)
    {
        return snapshot.Plane == CoinBot.Domain.Enums.ExchangeDataPlane.Futures
            ? $"{snapshot.ExecutionResultCategory} • {snapshot.ExecutionResultCode}"
            : snapshot.ExecutionResultCategory;
    }
}

