using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Contracts.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

[Authorize(Policy = ApplicationPolicies.TradeOperations)]
public class StrategyBuilderController(IStrategyTemplateCatalogService templateCatalogService) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        ViewData["StrategyTemplateCatalog"] = await templateCatalogService.ListAsync(cancellationToken);
        return View();
    }
}
