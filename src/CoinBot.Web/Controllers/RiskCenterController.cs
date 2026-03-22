using CoinBot.Contracts.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

[Authorize(Policy = ApplicationPolicies.RiskManagement)]
public sealed class RiskCenterController : Controller
{
    public IActionResult Index() => View();
}
