using CoinBot.Contracts.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

[Authorize(Policy = ApplicationPolicies.ExchangeManagement)]
public class ExchangesController : Controller
{
    public IActionResult Index()
    {
        return View();
    }
}
