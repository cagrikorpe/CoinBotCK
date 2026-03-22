using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

public sealed class BacktestController : Controller
{
    public IActionResult Index() => View();
}
