using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

public class BotsController : Controller
{
    public IActionResult Index()
    {
        return View();
    }
}
