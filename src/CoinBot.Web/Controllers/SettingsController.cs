using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

public sealed class SettingsController : Controller
{
    public IActionResult Index() => View();
}
