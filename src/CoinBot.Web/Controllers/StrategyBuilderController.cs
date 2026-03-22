using CoinBot.Contracts.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

[Authorize(Policy = ApplicationPolicies.TradeOperations)]
public class StrategyBuilderController : Controller
{
    public IActionResult Index()
    {
        return View();
    }
}
