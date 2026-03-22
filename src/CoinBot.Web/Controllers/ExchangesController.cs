using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

public class ExchangesController : Controller
{
    public IActionResult Index()
    {
        return View();
    }
}
