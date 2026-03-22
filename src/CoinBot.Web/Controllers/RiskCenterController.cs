using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

public sealed class RiskCenterController : Controller
{
    public IActionResult Index() => View();
}
