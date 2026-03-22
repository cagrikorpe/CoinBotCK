using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

public sealed class PaperTradingController : Controller
{
    public IActionResult Index() => View();
}
