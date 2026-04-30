using System.Security.Claims;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.Dashboard;
using CoinBot.Contracts.Common;
using CoinBot.Domain.Enums;
using CoinBot.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace CoinBot.UnitTests.Web;

public sealed class PositionsControllerTests
{
    [Fact]
    public void Controller_UsesTradeOperationsPolicy_KeepsSuperAdminRedirect_AndManualCloseRequiresAntiForgery()
    {
        var redirectAttribute = Assert.Single(
            typeof(PositionsController)
                .GetCustomAttributes(typeof(RedirectSuperAdminToAdminOverviewAttribute), inherit: true)
                .Cast<RedirectSuperAdminToAdminOverviewAttribute>());
        var authorizeAttribute = Assert.Single(
            typeof(PositionsController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>());
        var antiForgeryAttribute = Assert.Single(
            typeof(PositionsController)
                .GetMethod(nameof(PositionsController.ManualClose), [typeof(string), typeof(string), typeof(string), typeof(bool), typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true)
                .Cast<ValidateAntiForgeryTokenAttribute>());

        Assert.NotNull(redirectAttribute);
        Assert.Equal(ApplicationPolicies.TradeOperations, authorizeAttribute.Policy);
        Assert.NotEqual(ApplicationPolicies.AdminPortalAccess, authorizeAttribute.Policy);
        Assert.NotNull(antiForgeryAttribute);
    }

    [Fact]
    public async Task ManualClose_Challenges_WhenUserMissing()
    {
        var controller = CreateController();

        var result = await controller.ManualClose(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), "SOLUSDT", true, CancellationToken.None);

        Assert.IsType<ChallengeResult>(result);
    }

    [Fact]
    public async Task ManualClose_RedirectsWithError_WhenConfirmationMissing()
    {
        var botId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        var dashboard = new FakeUserDashboardPortfolioReadModelService
        {
            Snapshot = CreateSnapshot(botId, exchangeAccountId, canManualClose: true)
        };
        var manualCloseService = new FakeAdminManualCloseService();
        var auditLogService = new FakeAuditLogService();
        var controller = CreateController(dashboard, manualCloseService, auditLogService, "user-positions-01", "trace-pos-001");

        var result = await controller.ManualClose(botId.ToString("D"), exchangeAccountId.ToString("D"), "SOLUSDT", false, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(PositionsController.Index), redirect.ActionName);
        Assert.Equal("Reduce-only close icin onay kutusunu isaretleyin.", controller.TempData["PositionsManualCloseError"]);
        Assert.Empty(manualCloseService.Requests);
        var audit = Assert.Single(auditLogService.Requests);
        Assert.Equal("User.Positions.ManualCloseBlocked", audit.Action);
        Assert.Equal("Blocked", audit.Outcome);
    }

    [Fact]
    public async Task ManualClose_RedirectsWithSuccess_WhenOwnPositionSubmitsReduceOnlyClose()
    {
        var botId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        var dashboard = new FakeUserDashboardPortfolioReadModelService
        {
            Snapshot = CreateSnapshot(botId, exchangeAccountId, canManualClose: true)
        };
        var manualCloseService = new FakeAdminManualCloseService
        {
            Result = new AdminManualCloseResult(
                true,
                "ManualCloseSubmitted",
                "ManualClose=True | ReduceOnly=True | ExitSource=Manual | Environment=BinanceTestnet",
                "Reduce-only manual close emri gonderildi.")
        };
        var auditLogService = new FakeAuditLogService();
        var controller = CreateController(dashboard, manualCloseService, auditLogService, "user-positions-02", "trace-pos-002");

        var result = await controller.ManualClose(botId.ToString("D"), exchangeAccountId.ToString("D"), "SOLUSDT", true, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(PositionsController.Index), redirect.ActionName);
        Assert.Equal("Reduce-only manual close emri gonderildi.", controller.TempData["PositionsManualCloseSuccess"]);
        var request = Assert.Single(manualCloseService.Requests);
        Assert.Equal(botId, request.BotId);
        Assert.Equal(exchangeAccountId, request.ExchangeAccountId);
        Assert.Equal("SOLUSDT", request.Symbol);
        Assert.Equal("user-positions-02", request.ActorUserId);
        Assert.Equal("user:user-positions-02", request.ExecutionActor);
        var audit = Assert.Single(auditLogService.Requests);
        Assert.Equal("User.Positions.ManualClose", audit.Action);
        Assert.Equal("Allowed", audit.Outcome);
        Assert.Equal("Position/SOLUSDT", audit.Target);
    }

    [Fact]
    public async Task ManualClose_RedirectsWithError_WhenPositionScopeDoesNotBelongToCurrentUser()
    {
        var dashboard = new FakeUserDashboardPortfolioReadModelService
        {
            Snapshot = CreateSnapshot(Guid.NewGuid(), Guid.NewGuid(), canManualClose: false)
        };
        var manualCloseService = new FakeAdminManualCloseService();
        var auditLogService = new FakeAuditLogService();
        var controller = CreateController(dashboard, manualCloseService, auditLogService, "user-positions-03", "trace-pos-003");

        var result = await controller.ManualClose(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), "SOLUSDT", true, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(PositionsController.Index), redirect.ActionName);
        Assert.Equal("Secilen pozisyon icin manuel close kullanilamiyor.", controller.TempData["PositionsManualCloseError"]);
        Assert.Empty(manualCloseService.Requests);
        var audit = Assert.Single(auditLogService.Requests);
        Assert.Equal("User.Positions.ManualCloseBlocked", audit.Action);
    }

    [Fact]
    public async Task ManualClose_RedirectsWithError_WhenServiceBlocksPrivatePlaneStale()
    {
        var botId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        var dashboard = new FakeUserDashboardPortfolioReadModelService
        {
            Snapshot = CreateSnapshot(botId, exchangeAccountId, canManualClose: true)
        };
        var manualCloseService = new FakeAdminManualCloseService
        {
            Result = new AdminManualCloseResult(
                false,
                "ManualCloseBlockedPrivatePlaneStale",
                "ManualClose=True | ReasonCode=ManualCloseBlockedPrivatePlaneStale",
                "Private plane stale oldugu icin manuel close bloklandi.")
        };
        var auditLogService = new FakeAuditLogService();
        var controller = CreateController(dashboard, manualCloseService, auditLogService, "user-positions-04", "trace-pos-004");

        var result = await controller.ManualClose(botId.ToString("D"), exchangeAccountId.ToString("D"), "SOLUSDT", true, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(PositionsController.Index), redirect.ActionName);
        Assert.Equal("Private plane stale oldugu icin manuel close bloklandi.", controller.TempData["PositionsManualCloseError"]);
        Assert.Single(manualCloseService.Requests);
        var audit = Assert.Single(auditLogService.Requests);
        Assert.Equal("User.Positions.ManualCloseBlocked", audit.Action);
        Assert.Equal("Blocked", audit.Outcome);
    }

    private static PositionsController CreateController(
        FakeUserDashboardPortfolioReadModelService? dashboard = null,
        FakeAdminManualCloseService? manualCloseService = null,
        FakeAuditLogService? auditLogService = null,
        string? userId = null,
        string traceIdentifier = "trace-positions")
    {
        var controller = new PositionsController(
            dashboard ?? new FakeUserDashboardPortfolioReadModelService(),
            manualCloseService ?? new FakeAdminManualCloseService(),
            auditLogService ?? new FakeAuditLogService());
        var httpContext = new DefaultHttpContext
        {
            TraceIdentifier = traceIdentifier,
            User = string.IsNullOrWhiteSpace(userId)
                ? new ClaimsPrincipal(new ClaimsIdentity())
                : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "TestAuth"))
        };

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        controller.TempData = new TempDataDictionary(httpContext, new FakeTempDataProvider());
        return controller;
    }

    private static UserDashboardPortfolioSnapshot CreateSnapshot(Guid botId, Guid exchangeAccountId, bool canManualClose)
    {
        return new UserDashboardPortfolioSnapshot(
            1,
            "Canli senkron bagli",
            "positive",
            new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc),
            0m,
            15m,
            15m,
            "PnL snapshot ready.",
            Array.Empty<UserDashboardBalanceSnapshot>(),
            [
                new UserDashboardPositionSnapshot(
                    "SOLUSDT",
                    "LONG",
                    0.5m,
                    100m,
                    100m,
                    15m,
                    "isolated",
                    0m,
                    new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc),
                    ExchangeDataPlane.Futures,
                    null,
                    null,
                    103m,
                    null,
                    null,
                    exchangeAccountId,
                    "Testnet",
                    "info",
                    true,
                    canManualClose,
                    canManualClose ? botId : null,
                    canManualClose ? null : "Pozisyon icin tekil bot eslesmesi bulunamadi.")
            ],
            Array.Empty<UserDashboardTradeHistoryRowSnapshot>());
    }

    private sealed class FakeUserDashboardPortfolioReadModelService : IUserDashboardPortfolioReadModelService
    {
        public UserDashboardPortfolioSnapshot Snapshot { get; set; } = new(
            0,
            "Henüz senkron yok",
            "neutral",
            null,
            0m,
            0m,
            0m,
            "PnL snapshot unavailable.",
            Array.Empty<UserDashboardBalanceSnapshot>(),
            Array.Empty<UserDashboardPositionSnapshot>(),
            Array.Empty<UserDashboardTradeHistoryRowSnapshot>());

        public Task<UserDashboardPortfolioSnapshot> GetSnapshotAsync(string userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class FakeAdminManualCloseService : IAdminManualCloseService
    {
        public AdminManualCloseResult Result { get; set; } =
            new(true, "ManualCloseSubmitted", "ManualClose=True", "Reduce-only manual close emri gonderildi.");

        public List<AdminManualCloseRequest> Requests { get; } = [];

        public Task<AdminManualCloseResult> CloseAsync(AdminManualCloseRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeAuditLogService : IAuditLogService
    {
        public List<AuditLogWriteRequest> Requests { get; } = [];

        public Task WriteAsync(AuditLogWriteRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
