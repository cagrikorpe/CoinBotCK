using System.Security.Claims;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Contracts.Common;
using CoinBot.Domain.Enums;
using CoinBot.Web.Controllers;
using CoinBot.Web.ViewModels.Exchanges;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace CoinBot.UnitTests.Web;

public sealed class ExchangeConnectionControllerTests
{
    [Fact]
    public async Task OnboardingExchangeConnect_LoadsRealSnapshotIntoViewModel()
    {
        var snapshot = new UserExchangeCommandCenterSnapshot(
            "user-01",
            "User One",
            new UserExchangeEnvironmentSummary(ExecutionEnvironment.Demo, "Demo", "info", "Global varsayılan", "Demo mode", false),
            new UserExchangeRiskOverrideSummary("Core", 2m, 10m, 3m, false, false, false, null, null, null, "Risk hazır", "healthy", "Profil hazır"),
            [],
            [],
            new DateTime(2026, 3, 31, 10, 0, 0, DateTimeKind.Utc));
        var service = new FakeUserExchangeCommandCenterService { Snapshot = snapshot };
        var controller = CreateOnboardingController(service, "user-01");

        var result = await controller.ExchangeConnect(CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<UserExchangeCommandCenterPageViewModel>(viewResult.Model);
        Assert.Same(snapshot, model.Snapshot);
        Assert.Equal("Onboarding", model.SubmitController);
        Assert.Equal(nameof(OnboardingController.ConnectBinance), model.SubmitAction);
        Assert.True(model.ShowOnboardingFooterActions);
    }

    [Fact]
    public async Task ExchangesConnectBinance_PostsToBackend_AndSurfacesSafeFailure()
    {
        var service = new FakeUserExchangeCommandCenterService
        {
            Result = new ConnectUserBinanceCredentialResult(
                Guid.NewGuid(),
                false,
                "Geçersiz",
                "critical",
                "Validation failed.",
                "Withdraw izni açık olduğu için anahtar reddedildi.",
                "Trade=Y; Withdraw=Y; Spot=Y; Futures=N; Env=Demo",
                "Demo")
        };
        var controller = CreateExchangesController(service, "user-02", traceIdentifier: "trace-ex-001");

        var result = await controller.ConnectBinance(
            new BinanceCredentialConnectInputModel
            {
                RequestedEnvironment = ExecutionEnvironment.Demo,
                RequestedTradeMode = ExchangeTradeModeSelection.Spot,
                ApiKey = "api-key",
                ApiSecret = "api-secret"
            },
            CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        var request = Assert.Single(service.ConnectRequests);

        Assert.Equal(nameof(ExchangesController.Index), redirectResult.ActionName);
        Assert.Equal("user-02", request.UserId);
        Assert.Equal("user:user-02", request.Actor);
        Assert.Equal("trace-ex-001", request.CorrelationId);
        Assert.Equal("Withdraw izni açık olduğu için anahtar reddedildi.", controller.TempData["ExchangeConnectError"]);
    }

    [Fact]
    public void OnboardingExchangeConnectActions_RequireExchangeManagementPolicy()
    {
        var getAttribute = Assert.Single(
            typeof(OnboardingController)
                .GetMethod(nameof(OnboardingController.ExchangeConnect), [typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>());
        var postAttribute = Assert.Single(
            typeof(OnboardingController)
                .GetMethod(nameof(OnboardingController.ConnectBinance), [typeof(BinanceCredentialConnectInputModel), typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>());

        Assert.Equal(ApplicationPolicies.ExchangeManagement, getAttribute.Policy);
        Assert.Equal(ApplicationPolicies.ExchangeManagement, postAttribute.Policy);
    }

    private static OnboardingController CreateOnboardingController(
        FakeUserExchangeCommandCenterService service,
        string userId)
    {
        var controller = new OnboardingController(service);
        ApplyControllerContext(controller, userId, "trace-onb-001");
        return controller;
    }

    private static ExchangesController CreateExchangesController(
        FakeUserExchangeCommandCenterService service,
        string userId,
        string traceIdentifier)
    {
        var controller = new ExchangesController(service);
        ApplyControllerContext(controller, userId, traceIdentifier);
        return controller;
    }

    private static void ApplyControllerContext(Controller controller, string userId, string traceIdentifier)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.TraceIdentifier = traceIdentifier;
        httpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId)],
                "TestAuth"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        controller.TempData = new TempDataDictionary(httpContext, new TestTempDataProvider());
    }

    private sealed class FakeUserExchangeCommandCenterService : IUserExchangeCommandCenterService
    {
        public UserExchangeCommandCenterSnapshot Snapshot { get; set; } = new(
            "user-01",
            "User",
            new UserExchangeEnvironmentSummary(ExecutionEnvironment.Demo, "Demo", "info", "Global varsayılan", "Demo mode", false),
            new UserExchangeRiskOverrideSummary("Tanımsız", null, null, null, false, false, false, null, null, null, "Risk profili eksik", "warning", "Eksik"),
            [],
            [],
            DateTime.UtcNow);

        public ConnectUserBinanceCredentialResult Result { get; set; } = new(
            Guid.NewGuid(),
            true,
            "Aktif",
            "healthy",
            "Bağlandı.",
            null,
            "Trade=Y; Withdraw=N; Spot=Y; Futures=N; Env=Demo",
            "Demo");

        public List<ConnectUserBinanceCredentialRequest> ConnectRequests { get; } = [];

        public Task<UserExchangeCommandCenterSnapshot> GetSnapshotAsync(string userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Snapshot);
        }

        public Task<ConnectUserBinanceCredentialResult> ConnectBinanceAsync(ConnectUserBinanceCredentialRequest request, CancellationToken cancellationToken = default)
        {
            ConnectRequests.Add(request);
            return Task.FromResult(Result);
        }
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        private readonly Dictionary<string, object> values = new(StringComparer.Ordinal);

        public IDictionary<string, object> LoadTempData(HttpContext context)
        {
            return new Dictionary<string, object>(values, StringComparer.Ordinal);
        }

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
            this.values.Clear();

            foreach (var pair in values)
            {
                this.values[pair.Key] = pair.Value;
            }
        }
    }
}
