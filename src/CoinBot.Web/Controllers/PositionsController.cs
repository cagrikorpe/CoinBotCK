using System.Security.Claims;
using CoinBot.Application.Abstractions.Dashboard;
using CoinBot.Contracts.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

[Authorize(Policy = ApplicationPolicies.TradeOperations)]
public sealed class PositionsController(
    IUserDashboardPortfolioReadModelService userDashboardPortfolioReadModelService) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Pozisyonlar";
        ViewData["ActiveNav"] = "Positions";
        ViewData["PageDescription"] = "Açık ve kapalı pozisyonları, unrealized/realized PnL özetini ve emir akışını tek operasyondan izlemek için foundation ekranı.";
        ViewData["BreadcrumbItems"] = new[] { "Execution", "Pozisyonlar" };
        ViewData["DefaultTab"] = "positions";
        return View(await GetPortfolioSnapshotAsync(cancellationToken));
    }

    public async Task<IActionResult> History(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Emir Geçmişi";
        ViewData["ActiveNav"] = "OrderHistory";
        ViewData["PageDescription"] = "Pozisyonlar ve emir geçmişi foundation ekranı içinde order history sekmesine odaklı görünüm.";
        ViewData["BreadcrumbItems"] = new[] { "Execution", "Emir Geçmişi" };
        ViewData["DefaultTab"] = "history";
        return View("Index", await GetPortfolioSnapshotAsync(cancellationToken));
    }

    private async Task<UserDashboardPortfolioSnapshot> GetPortfolioSnapshotAsync(CancellationToken cancellationToken)
    {
        var userId = HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return new UserDashboardPortfolioSnapshot(
                0,
                "Henüz senkron yok",
                "neutral",
                null,
                0m,
                0m,
                0m,
                "PnL snapshot unavailable.",
                Array.Empty<UserDashboardBalanceSnapshot>(),
                Array.Empty<UserDashboardPositionSnapshot>(),
                Array.Empty<UserDashboardTradeHistoryRowSnapshot>());
        }

        return await userDashboardPortfolioReadModelService.GetSnapshotAsync(userId, cancellationToken);
    }
}
