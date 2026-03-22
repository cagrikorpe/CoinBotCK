using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

public sealed class AiRobotController : Controller
{
    public IActionResult Index() => View();
}
