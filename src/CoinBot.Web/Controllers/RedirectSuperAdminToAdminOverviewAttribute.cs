using CoinBot.Contracts.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CoinBot.Web.Controllers;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RedirectSuperAdminToAdminOverviewAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated != true ||
            !context.HttpContext.User.HasClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.PlatformAdministration))
        {
            return;
        }

        context.Result = new RedirectToActionResult("Overview", "Admin", new { area = "Admin" });
    }
}