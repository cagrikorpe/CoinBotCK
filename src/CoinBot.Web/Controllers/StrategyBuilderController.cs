using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

public class StrategyBuilderController : Controller
{
    public IActionResult Index()
    {
        return View();
    }
}
