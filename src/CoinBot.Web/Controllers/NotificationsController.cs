using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace CoinBot.Web.Controllers;

[Authorize]
public sealed class NotificationsController : Controller
{
    public IActionResult Index() => View();
}
