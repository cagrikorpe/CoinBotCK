using CoinBot.Contracts.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

[Authorize(Policy = ApplicationPolicies.TradeOperations)]
public sealed class PositionsController : Controller
{
    public IActionResult Index()
    {
        ViewData["Title"] = "Pozisyonlar";
        ViewData["ActiveNav"] = "Positions";
        ViewData["PageDescription"] = "Açık ve kapalı pozisyonları, unrealized/realized PnL özetini ve emir akışını tek operasyondan izlemek için foundation ekranı.";
        ViewData["BreadcrumbItems"] = new[] { "Execution", "Pozisyonlar" };
        ViewData["DefaultTab"] = "positions";
        return View();
    }

    public IActionResult History()
    {
        ViewData["Title"] = "Emir Geçmişi";
        ViewData["ActiveNav"] = "OrderHistory";
        ViewData["PageDescription"] = "Pozisyonlar ve emir geçmişi foundation ekranı içinde order history sekmesine odaklı görünüm.";
        ViewData["BreadcrumbItems"] = new[] { "Execution", "Emir Geçmişi" };
        ViewData["DefaultTab"] = "history";
        return View("Index");
    }
}
