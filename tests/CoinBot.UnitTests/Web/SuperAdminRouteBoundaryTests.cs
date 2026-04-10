using System.Security.Claims;
using CoinBot.Contracts.Common;
using CoinBot.Web.Controllers;
using CoinBot.Web.ViewModels.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace CoinBot.UnitTests.Web;

public sealed class SuperAdminRouteBoundaryTests
{
    [Theory]
    [InlineData(typeof(HomeController))]
    [InlineData(typeof(OnboardingController))]
    [InlineData(typeof(ExchangesController))]
    [InlineData(typeof(BotsController))]
    [InlineData(typeof(StrategyBuilderController))]
    [InlineData(typeof(AiRobotController))]
    [InlineData(typeof(RiskCenterController))]
    [InlineData(typeof(BacktestController))]
    [InlineData(typeof(PaperTradingController))]
    [InlineData(typeof(PositionsController))]
    [InlineData(typeof(NotificationsController))]
    [InlineData(typeof(LogCenterController))]
    public void UserFlowControllers_ApplySuperAdminRedirectGuard(Type controllerType)
    {
        Assert.Contains(
            controllerType.GetCustomAttributes(typeof(RedirectSuperAdminToAdminOverviewAttribute), inherit: true)
                .Cast<RedirectSuperAdminToAdminOverviewAttribute>(),
            _ => true);
    }

    [Fact]
    public void SettingsController_IndexActions_ApplySuperAdminRedirectGuard()
    {
        Assert.Contains(
            typeof(SettingsController).GetMethod(nameof(SettingsController.Index), [typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(RedirectSuperAdminToAdminOverviewAttribute), inherit: true)
                .Cast<RedirectSuperAdminToAdminOverviewAttribute>(),
            _ => true);

        Assert.Contains(
            typeof(SettingsController).GetMethod(nameof(SettingsController.Index), [typeof(TimeZoneSettingsInputModel), typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(RedirectSuperAdminToAdminOverviewAttribute), inherit: true)
                .Cast<RedirectSuperAdminToAdminOverviewAttribute>(),
            _ => true);
    }    [Fact]
    public void SettingsController_MfaActions_DoNotApplySuperAdminRedirectGuard()
    {
        Assert.Empty(
            typeof(SettingsController).GetMethod(nameof(SettingsController.Mfa), [typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(RedirectSuperAdminToAdminOverviewAttribute), inherit: true)
                .Cast<RedirectSuperAdminToAdminOverviewAttribute>());

        Assert.Empty(
            typeof(SettingsController).GetMethod(nameof(SettingsController.StartMfaSetup), [typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(RedirectSuperAdminToAdminOverviewAttribute), inherit: true)
                .Cast<RedirectSuperAdminToAdminOverviewAttribute>());

        Assert.Empty(
            typeof(SettingsController).GetMethod(nameof(SettingsController.EnableMfa), [typeof(string), typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(RedirectSuperAdminToAdminOverviewAttribute), inherit: true)
                .Cast<RedirectSuperAdminToAdminOverviewAttribute>());
    }

    [Fact]
    public void RedirectGuard_WhenUserHasPlatformAdministration_RedirectsToAdminOverview()    {
        var attribute = new RedirectSuperAdminToAdminOverviewAttribute();
        var context = CreateContext(includePlatformAdministration: true);

        attribute.OnActionExecuting(context);

        var redirectResult = Assert.IsType<RedirectToActionResult>(context.Result);
        Assert.Equal("Overview", redirectResult.ActionName);
        Assert.Equal("Admin", redirectResult.ControllerName);
        Assert.Equal("Admin", redirectResult.RouteValues!["area"]);
    }

    [Fact]
    public void RedirectGuard_WhenUserLacksPlatformAdministration_DoesNotRedirect()
    {
        var attribute = new RedirectSuperAdminToAdminOverviewAttribute();
        var context = CreateContext(includePlatformAdministration: false);

        attribute.OnActionExecuting(context);

        Assert.Null(context.Result);
    }

    private static ActionExecutingContext CreateContext(bool includePlatformAdministration)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "user-1"),
            new(ClaimTypes.Name, "user@coinbot.test")
        };

        if (includePlatformAdministration)
        {
            claims.Add(new Claim(ApplicationClaimTypes.Permission, ApplicationPermissions.PlatformAdministration));
        }

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
        };

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), new object());
    }
}