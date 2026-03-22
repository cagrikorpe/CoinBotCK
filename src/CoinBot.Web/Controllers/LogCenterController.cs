using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

public sealed class LogCenterController : Controller
{
    public IActionResult Index() => View();
}
