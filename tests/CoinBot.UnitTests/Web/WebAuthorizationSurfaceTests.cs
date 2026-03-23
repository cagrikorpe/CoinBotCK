using CoinBot.Web.Controllers;
using CoinBot.Web.ViewModels.Auth;
using Microsoft.AspNetCore.Authorization;

namespace CoinBot.UnitTests.Web;

public sealed class WebAuthorizationSurfaceTests
{
    [Theory]
    [InlineData(typeof(HomeController))]
    [InlineData(typeof(OnboardingController))]
    [InlineData(typeof(BacktestController))]
    [InlineData(typeof(PaperTradingController))]
    [InlineData(typeof(SettingsController))]
    [InlineData(typeof(NotificationsController))]
    [InlineData(typeof(LogCenterController))]
    [InlineData(typeof(AiRobotController))]
    public void ProtectedControllers_RequireAuthenticatedUser(Type controllerType)
    {
        Assert.Contains(
            controllerType.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>(),
            attribute => string.IsNullOrWhiteSpace(attribute.Policy));
    }

    [Fact]
    public void AuthController_PublicEndpointsRemainAllowAnonymous_AndLogoutRequiresAuthorization()
    {
        Assert.Contains(GetAttributes(nameof(AuthController.Login), [typeof(string)]), attribute => attribute is AllowAnonymousAttribute);
        Assert.Contains(GetAttributes(nameof(AuthController.Login), [typeof(LoginViewModel)]), attribute => attribute is AllowAnonymousAttribute);
        Assert.Contains(GetAttributes(nameof(AuthController.Register), [typeof(string)]), attribute => attribute is AllowAnonymousAttribute);
        Assert.Contains(GetAttributes(nameof(AuthController.Register), [typeof(RegisterViewModel)]), attribute => attribute is AllowAnonymousAttribute);
        Assert.Contains(GetAttributes(nameof(AuthController.Mfa), [typeof(string), typeof(bool)]), attribute => attribute is AllowAnonymousAttribute);
        Assert.Contains(GetAttributes(nameof(AuthController.Mfa), [typeof(MfaViewModel)]), attribute => attribute is AllowAnonymousAttribute);
        Assert.Contains(GetAttributes(nameof(AuthController.AccessDenied), Type.EmptyTypes), attribute => attribute is AllowAnonymousAttribute);
        Assert.Contains(GetAttributes(nameof(AuthController.Logout), Type.EmptyTypes), attribute => attribute is AuthorizeAttribute);
    }

    private static object[] GetAttributes(string methodName, Type[] parameterTypes)
    {
        return typeof(AuthController)
            .GetMethod(methodName, parameterTypes)!
            .GetCustomAttributes(inherit: true);
    }
}
