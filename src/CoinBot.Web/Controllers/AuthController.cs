using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

public class AuthController : Controller
{
    public IActionResult Login()
    {
        return View();
    }

    public IActionResult Register()
    {
        return View();
    }

    public IActionResult Mfa()
    {
        return View();
    }
}
