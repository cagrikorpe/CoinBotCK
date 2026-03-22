using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

public sealed class NotificationsController : Controller
{
    public IActionResult Index() => View();
}
