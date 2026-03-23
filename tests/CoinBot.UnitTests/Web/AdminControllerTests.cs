using System.Security.Claims;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Enums;
using CoinBot.Web.Areas.Admin.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace CoinBot.UnitTests.Web;

public sealed class AdminControllerTests
{
    [Fact]
    public async Task Settings_LoadsExecutionSwitchSnapshotIntoViewData()
    {
        var snapshot = new GlobalExecutionSwitchSnapshot(
            TradeMasterSwitchState.Armed,
            DemoModeEnabled: true,
            IsPersisted: true);
        var switchService = new FakeGlobalExecutionSwitchService
        {
            Snapshot = snapshot
        };
        var controller = CreateController(switchService);

        var result = await controller.Settings(CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.Equal(1, switchService.GetSnapshotCalls);
        Assert.Same(snapshot, controller.ViewData["AdminExecutionSwitchSnapshot"]);
    }

    [Fact]
    public async Task SetTradeMasterState_PassesActorContextAndCorrelation_ToSwitchService()
    {
        var switchService = new FakeGlobalExecutionSwitchService();
        var controller = CreateController(switchService, userId: "ops-admin", traceIdentifier: "trace-trade-1");

        var result = await controller.SetTradeMasterState(
            isArmed: true,
            reason: "Controlled enablement",
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        var call = Assert.Single(switchService.TradeMasterCalls);

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Equal(TradeMasterSwitchState.Armed, call.TradeMasterState);
        Assert.Equal("admin:ops-admin", call.Actor);
        Assert.Equal("trace-trade-1", call.CorrelationId);
        Assert.Contains("AdminSettings.TradeMaster", call.Context, StringComparison.Ordinal);
        Assert.Contains("Reason=Controlled enablement", call.Context, StringComparison.Ordinal);
        Assert.Equal("TradeMaster armed. Emir zinciri backend hard gate uzerinden acildi.", controller.TempData["AdminExecutionSwitchSuccess"]);
    }

    [Fact]
    public async Task SetDemoMode_DisableFlow_RequiresApprovalReferenceAndPassesAuditContext()
    {
        var switchService = new FakeGlobalExecutionSwitchService();
        var controller = CreateController(switchService, userId: "ops-admin", traceIdentifier: "trace-demo-1");

        var result = await controller.SetDemoMode(
            isEnabled: false,
            reason: "Planned live window",
            liveApprovalReference: "chg-9001",
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        var call = Assert.Single(switchService.DemoModeCalls);

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.False(call.IsEnabled);
        Assert.NotNull(call.LiveApproval);
        Assert.Equal("chg-9001", call.LiveApproval!.ApprovalReference);
        Assert.Equal("admin:ops-admin", call.Actor);
        Assert.Equal("trace-demo-1", call.CorrelationId);
        Assert.Contains("AdminSettings.DemoMode", call.Context, StringComparison.Ordinal);
        Assert.Contains("Reason=Planned live window", call.Context, StringComparison.Ordinal);
        Assert.Equal("DemoMode disabled. Live execution yalnizca approval reference ile acildi.", controller.TempData["AdminExecutionSwitchSuccess"]);
    }

    [Fact]
    public async Task SetDemoMode_WhenSwitchServiceRejects_SurfacesErrorWithoutThrowing()
    {
        var switchService = new FakeGlobalExecutionSwitchService
        {
            SetDemoModeException = new InvalidOperationException("Explicit live approval is required before the global default mode can switch to Live.")
        };
        var controller = CreateController(switchService);

        var result = await controller.SetDemoMode(
            isEnabled: false,
            reason: "Live request without approval",
            liveApprovalReference: null,
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Equal(
            "Explicit live approval is required before the global default mode can switch to Live.",
            controller.TempData["AdminExecutionSwitchError"]);
    }

    private static AdminController CreateController(
        FakeGlobalExecutionSwitchService switchService,
        string userId = "admin-01",
        string traceIdentifier = "trace-001")
    {
        var httpContext = new DefaultHttpContext
        {
            TraceIdentifier = traceIdentifier,
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId)],
                    "TestAuth"))
        };

        return new AdminController(switchService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            },
            TempData = new TempDataDictionary(httpContext, new TestTempDataProvider())
        };
    }

    private sealed class FakeGlobalExecutionSwitchService : IGlobalExecutionSwitchService
    {
        public GlobalExecutionSwitchSnapshot Snapshot { get; set; } = new(
            TradeMasterSwitchState.Disarmed,
            DemoModeEnabled: true,
            IsPersisted: true);

        public List<TradeMasterCall> TradeMasterCalls { get; } = [];

        public List<DemoModeCall> DemoModeCalls { get; } = [];

        public int GetSnapshotCalls { get; private set; }

        public Exception? SetDemoModeException { get; set; }

        public Task<GlobalExecutionSwitchSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            GetSnapshotCalls++;
            return Task.FromResult(Snapshot);
        }

        public Task<GlobalExecutionSwitchSnapshot> SetTradeMasterStateAsync(
            TradeMasterSwitchState tradeMasterState,
            string actor,
            string? context = null,
            string? correlationId = null,
            CancellationToken cancellationToken = default)
        {
            TradeMasterCalls.Add(new TradeMasterCall(tradeMasterState, actor, context, correlationId));
            Snapshot = Snapshot with
            {
                TradeMasterState = tradeMasterState,
                IsPersisted = true
            };

            return Task.FromResult(Snapshot);
        }

        public Task<GlobalExecutionSwitchSnapshot> SetDemoModeAsync(
            bool isEnabled,
            string actor,
            TradingModeLiveApproval? liveApproval = null,
            string? context = null,
            string? correlationId = null,
            CancellationToken cancellationToken = default)
        {
            DemoModeCalls.Add(new DemoModeCall(isEnabled, actor, liveApproval, context, correlationId));

            if (SetDemoModeException is not null)
            {
                throw SetDemoModeException;
            }

            Snapshot = Snapshot with
            {
                DemoModeEnabled = isEnabled,
                IsPersisted = true,
                LiveModeApprovedAtUtc = isEnabled ? null : DateTime.UtcNow
            };

            return Task.FromResult(Snapshot);
        }
    }

    private sealed record TradeMasterCall(
        TradeMasterSwitchState TradeMasterState,
        string Actor,
        string? Context,
        string? CorrelationId);

    private sealed record DemoModeCall(
        bool IsEnabled,
        string Actor,
        TradingModeLiveApproval? LiveApproval,
        string? Context,
        string? CorrelationId);

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>(StringComparer.Ordinal);

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
