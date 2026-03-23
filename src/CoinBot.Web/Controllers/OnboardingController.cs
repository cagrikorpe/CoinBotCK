using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace CoinBot.Web.Controllers;

[Authorize]
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
