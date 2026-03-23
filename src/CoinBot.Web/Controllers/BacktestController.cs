using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace CoinBot.Web.Controllers;

[Authorize]
public sealed class BacktestController : Controller
{
    public IActionResult Index() => View();
}
