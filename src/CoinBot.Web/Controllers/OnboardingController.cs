using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

public class OnboardingController : Controller
{
    public IActionResult Index()
    {
        return View();
    }

    public IActionResult ExchangeSecurity()
    {
        return View();
    }

    public IActionResult ExchangeConnect()
    {
        return View();
    }
}
