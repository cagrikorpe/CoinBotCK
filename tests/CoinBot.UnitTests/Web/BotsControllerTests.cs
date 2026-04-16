using System.Security.Claims;
using CoinBot.Application.Abstractions.Bots;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.Settings;
using CoinBot.Contracts.Common;
using CoinBot.Domain.Enums;
using CoinBot.Web.Controllers;
using CoinBot.Web.ViewModels.Bots;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace CoinBot.UnitTests.Web;

public sealed class BotsControllerTests
{
    [Fact]
    public async Task Index_ReturnsPageModel_FromReadService()
    {
        var managementService = new FakeBotManagementService
        {
            PageSnapshot = new BotManagementPageSnapshot(
                [
                    new BotManagementBotSnapshot(
                        Guid.NewGuid(),
                        "Pilot A",
                        "strategy-a",
                        "Strategy A",
                        true,
                        "BTCUSDT",
                        null,
                        null,
                        "Pilot Futures",
                        true,
                        true,
                        1m,
                        "ISOLATED",
                        false,
                        0,
                        0,
                        "Pending",
                        null,
                        "Rejected",
                        "UserExecutionBotCooldownActive",
                        "Execution blocked because the bot cooldown is still active.",
                        "PreSubmit",
                        false,
                        false,
                        true,
                        true,
                        true,
                        false,
                        true,
                        "UserExecutionOverrideBlocked",
                        "corr-user-page",
                        "cb_aaaaaaaaaaaaaaaaaaaaaa",
                        new DateTime(2026, 4, 2, 12, 2, 0, DateTimeKind.Utc),
                        90,
                        DateTime.UtcNow,
                        DateTime.UtcNow,
                        LastExecutionLastCandleAtUtc: new DateTime(2026, 4, 2, 12, 0, 59, 999, DateTimeKind.Utc),
                        LastExecutionDataAgeMilliseconds: 20265,
                        LastExecutionContinuityState: "Continuity OK",
                        LastExecutionContinuityGapCount: 0,
                        LastExecutionStaleReason: "Clock drift exceeded",
                        LastExecutionAffectedSymbol: "BTCUSDT",
                        LastExecutionAffectedTimeframe: "1m",
                        LastExecutionDecisionOutcome: "Block",
                        LastExecutionDecisionAtUtc: new DateTime(2026, 4, 2, 12, 1, 5, DateTimeKind.Utc),
                        LastExecutionDecisionReasonType: "StaleData",
                        LastExecutionDecisionReasonCode: "ClockDriftExceeded",
                        LastExecutionDecisionSummary: "Execution blocked because clock drift exceeded the safety threshold.",
                        LastExecutionStaleThresholdMilliseconds: 3000,
                        LastExecutionContinuityGapStartedAtUtc: new DateTime(2026, 4, 2, 11, 58, 0, DateTimeKind.Utc),
                        LastExecutionContinuityGapLastSeenAtUtc: new DateTime(2026, 4, 2, 11, 59, 0, DateTimeKind.Utc),
                        LastExecutionContinuityRecoveredAtUtc: new DateTime(2026, 4, 2, 11, 59, 30, DateTimeKind.Utc))
                ])
        };
        var controller = CreateController(managementService, new FakeBotPilotControlService(), new FakeUserSettingsService(), "user-bot-01", "trace-bot-001");

        var result = await controller.Index(CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<BotManagementIndexViewModel>(viewResult.Model);

        var row = Assert.Single(model.Bots);
        Assert.Equal("Execution blocked because the bot cooldown is still active.", row.LastExecutionBlockDetail);
        Assert.Equal("Stage: PreSubmit", row.LastExecutionStageText);
        Assert.Equal("Submitted: No", row.LastExecutionSubmitStatusText);
        Assert.Equal("Retry: No | Cooldown: Applied", row.LastExecutionRetryText);
        Assert.Equal("ReduceOnly: Yes | SL: Yes | TP: No", row.LastExecutionProtectionText);
        Assert.Equal("Transition: UserExecutionOverrideBlocked", row.LastExecutionTransitionText);
        Assert.Equal("Correlation: corr-user-page", row.LastExecutionCorrelationText);
        Assert.Equal("ClientOrderId: cb_aaaaaaaaaaaaaaaaaaaaaa", row.LastExecutionClientOrderText);
        Assert.Equal("Duplicate suppressed", row.LastExecutionDuplicateText);
        Assert.True(row.IsCooldownActive);
        Assert.Equal("90 sn", row.CooldownRemainingText);
        Assert.Equal("Data latency high", row.MarketDataBadgeText);
        Assert.Equal("2026-04-02 12:00:59 Coordinated Universal Time", row.LastCandleAtLabel);
        Assert.Equal("20265 ms", row.DataAgeText);
        Assert.Equal("Continuity OK", row.ContinuityStateText);
        Assert.Equal("0", row.ContinuityGapText);
        Assert.Equal("BTCUSDT / 1m", row.AffectedMarketText);
        Assert.Equal("Clock drift exceeded", row.StaleReasonText);
        Assert.Equal("Decision: Block / StaleData", row.DecisionText);
        Assert.Equal("ReasonCode: ClockDriftExceeded", row.DecisionReasonCodeText);
        Assert.Equal("Execution blocked because clock drift exceeded the safety threshold.", row.DecisionSummaryText);
        Assert.Equal("3000 ms", row.MarketStaleThresholdText);
        Assert.Equal("2026-04-02 11:58:00 Coordinated Universal Time", row.ContinuityGapStartedText);
        Assert.Equal("2026-04-02 11:59:00 Coordinated Universal Time", row.ContinuityGapLastSeenText);
        Assert.Equal("2026-04-02 11:59:30 Coordinated Universal Time", row.ContinuityRecoveryText);
        Assert.Equal("PAUSED", row.PilotStateLabel);
        Assert.Equal("warning", row.PilotStateTone);
        Assert.Equal("Bot kapali.", row.PilotStateSummary);
        Assert.Equal("LongOnly", row.DirectionModeLabel);
    }

    [Fact]
    public async Task Index_MapsShadowOnlyDecisions_ToShadowBadge()
    {
        var managementService = new FakeBotManagementService
        {
            PageSnapshot = new BotManagementPageSnapshot(
                [
                    new BotManagementBotSnapshot(
                        Guid.NewGuid(),
                        "Pilot Shadow",
                        "strategy-shadow",
                        "Strategy Shadow",
                        true,
                        "BTCUSDT",
                        0.001m,
                        Guid.NewGuid(),
                        "Pilot Futures",
                        true,
                        true,
                        1m,
                        "ISOLATED",
                        true,
                        0,
                        0,
                        "Succeeded",
                        null,
                        "N/A",
                        null,
                        null,
                        null,
                        false,
                        false,
                        false,
                        false,
                        false,
                        false,
                        false,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        DateTime.UtcNow,
                        LastShadowFinalAction: "ShadowOnly",
                        LastShadowNoSubmitReason: "ShadowModeActive",
                        LastShadowEvaluatedAtUtc: new DateTime(2026, 4, 2, 12, 5, 0, DateTimeKind.Utc))
                ])
        };
        var controller = CreateController(managementService, new FakeBotPilotControlService(), new FakeUserSettingsService(), "user-bot-shadow", "trace-bot-shadow");

        var result = await controller.Index(CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<BotManagementIndexViewModel>(viewResult.Model);
        var row = Assert.Single(model.Bots);
        Assert.Equal("SHADOW", row.PilotStateLabel);
        Assert.Equal("info", row.PilotStateTone);
        Assert.Equal("Karar var, emir gonderimi yok.", row.PilotStateSummary);
    }

    [Fact]
    public async Task Index_MapsSubmittedOrders_ToLiveBadge()
    {
        var managementService = new FakeBotManagementService
        {
            PageSnapshot = new BotManagementPageSnapshot(
                [
                    new BotManagementBotSnapshot(
                        Guid.NewGuid(),
                        "Pilot Live",
                        "strategy-live",
                        "Strategy Live",
                        true,
                        "BTCUSDT",
                        0.001m,
                        Guid.NewGuid(),
                        "Pilot Futures",
                        true,
                        true,
                        1m,
                        "ISOLATED",
                        true,
                        0,
                        1,
                        "Succeeded",
                        null,
                        "Submitted",
                        null,
                        null,
                        null,
                        true,
                        false,
                        false,
                        false,
                        false,
                        false,
                        false,
                        null,
                        null,
                        null,
                        null,
                        null,
                        DateTime.UtcNow,
                        DateTime.UtcNow)
                ])
        };
        var controller = CreateController(managementService, new FakeBotPilotControlService(), new FakeUserSettingsService(), "user-bot-live", "trace-bot-live");

        var result = await controller.Index(CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<BotManagementIndexViewModel>(viewResult.Model);
        var row = Assert.Single(model.Bots);
        Assert.Equal("LIVE", row.PilotStateLabel);
        Assert.Equal("critical", row.PilotStateTone);
        Assert.Equal("Emir gonderimi acik.", row.PilotStateSummary);
    }

    [Fact]
    public async Task Index_MapsRiskProfileMissingFeatureDecision_ToPausedReason()
    {
        var managementService = new FakeBotManagementService
        {
            PageSnapshot = new BotManagementPageSnapshot(
                [
                    new BotManagementBotSnapshot(
                        Guid.NewGuid(),
                        "Pilot Risk",
                        "strategy-risk",
                        "Strategy Risk",
                        true,
                        "BTCUSDT",
                        0.001m,
                        Guid.NewGuid(),
                        "Pilot Futures",
                        true,
                        true,
                        1m,
                        "ISOLATED",
                        true,
                        0,
                        0,
                        "Succeeded",
                        null,
                        "Vetoed",
                        null,
                        null,
                        null,
                        false,
                        false,
                        false,
                        false,
                        false,
                        false,
                        false,
                        null,
                        null,
                        null,
                        null,
                        null,
                        DateTime.UtcNow,
                        DateTime.UtcNow,
                        LastExecutionDecisionOutcome: "Blocked",
                        LastExecutionDecisionReasonType: "RiskVeto",
                        LastExecutionDecisionReasonCode: "RiskProfileMissing",
                        LastExecutionDecisionSummary: "Risk veto blocked execution.")
                ])
        };
        var controller = CreateController(managementService, new FakeBotPilotControlService(), new FakeUserSettingsService(), "user-bot-risk", "trace-bot-risk");

        var result = await controller.Index(CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<BotManagementIndexViewModel>(viewResult.Model);
        var row = Assert.Single(model.Bots);
        Assert.Equal("PAUSED", row.PilotStateLabel);
        Assert.Equal("Risk tercihi eksik.", row.PilotStateSummary);
    }
    [Fact]
    public async Task Index_MapsAccountEquityUnavailable_ToBalanceUnavailableReason()
    {
        var managementService = new FakeBotManagementService
        {
            PageSnapshot = new BotManagementPageSnapshot(
                [
                    new BotManagementBotSnapshot(
                        Guid.NewGuid(),
                        "Pilot Balance Missing",
                        "strategy-balance",
                        "Strategy Balance",
                        true,
                        "BTCUSDT",
                        0.001m,
                        Guid.NewGuid(),
                        "Pilot Futures",
                        true,
                        true,
                        1m,
                        "ISOLATED",
                        true,
                        0,
                        0,
                        "Succeeded",
                        null,
                        "N/A",
                        null,
                        null,
                        null,
                        false,
                        false,
                        false,
                        false,
                        false,
                        false,
                        false,
                        null,
                        null,
                        null,
                        null,
                        null,
                        DateTime.UtcNow,
                        DateTime.UtcNow,
                        LastExecutionDecisionOutcome: "Blocked",
                        LastExecutionDecisionReasonType: "RiskVeto",
                        LastExecutionDecisionReasonCode: "AccountEquityUnavailable",
                        LastExecutionDecisionSummary: "Account equity unavailable.")
                ])
        };
        var controller = CreateController(managementService, new FakeBotPilotControlService(), new FakeUserSettingsService(), "user-bot-balance", "trace-bot-balance");

        var result = await controller.Index(CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<BotManagementIndexViewModel>(viewResult.Model);
        var row = Assert.Single(model.Bots);
        Assert.Equal("PAUSED", row.PilotStateLabel);
        Assert.Equal("Bakiye okunamiyor.", row.PilotStateSummary);
    }
    [Fact]
    public async Task Index_MapsReadyBotWithoutEntrySignal_ToWaitingForSignalReason()
    {
        var managementService = new FakeBotManagementService
        {
            PageSnapshot = new BotManagementPageSnapshot(
                [
                    new BotManagementBotSnapshot(
                        Guid.NewGuid(),
                        "Pilot Waiting",
                        "strategy-waiting",
                        "Strategy Waiting",
                        true,
                        "BTCUSDT",
                        0.001m,
                        Guid.NewGuid(),
                        "Pilot Futures",
                        true,
                        true,
                        1m,
                        "ISOLATED",
                        true,
                        0,
                        0,
                        "Succeeded",
                        null,
                        "N/A",
                        null,
                        null,
                        null,
                        false,
                        false,
                        false,
                        false,
                        false,
                        false,
                        false,
                        null,
                        null,
                        null,
                        null,
                        null,
                        DateTime.UtcNow,
                        DateTime.UtcNow,
                        LastExecutionDecisionOutcome: "None")
                ])
        };
        var controller = CreateController(managementService, new FakeBotPilotControlService(), new FakeUserSettingsService(), "user-bot-waiting", "trace-bot-waiting");

        var result = await controller.Index(CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<BotManagementIndexViewModel>(viewResult.Model);
        var row = Assert.Single(model.Bots);
        Assert.Equal("PAUSED", row.PilotStateLabel);
        Assert.Equal("Sinyal bekleniyor.", row.PilotStateSummary);
    }
    [Fact]
    public async Task Create_Get_ReturnsEditorView()
    {
        var controller = CreateController(CreateManagementService(), new FakeBotPilotControlService(), new FakeUserSettingsService(), "user-bot-02", "trace-bot-002");

        var result = await controller.Create(CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<BotManagementEditorViewModel>(viewResult.Model);

        Assert.Equal("Editor", viewResult.ViewName);
        Assert.Equal(TradingBotDirectionMode.LongOnly, model.Form.DirectionMode);
        Assert.False(model.IsEditMode);
    }

    [Fact]
    public async Task Edit_Get_ReturnsEditorView_WhenBotExists()
    {
        var managementService = CreateManagementService();
        var botId = managementService.EditBotId;
        var controller = CreateController(managementService, new FakeBotPilotControlService(), new FakeUserSettingsService(), "user-bot-03", "trace-bot-003");

        var result = await controller.Edit(botId, CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<BotManagementEditorViewModel>(viewResult.Model);

        Assert.Equal("Editor", viewResult.ViewName);
        Assert.True(model.IsEditMode);
        Assert.Equal(botId, model.BotId);
    }

    [Fact]
    public async Task Create_Post_PersistsBot_AndRedirectsOnSuccess()
    {
        var managementService = CreateManagementService();
        managementService.CreateResult = new BotManagementSaveResult(Guid.NewGuid(), true, true, false, null, null);
        var controller = CreateController(managementService, new FakeBotPilotControlService(), new FakeUserSettingsService(), "user-bot-04", "trace-bot-004");
        var form = CreateInput();

        var result = await controller.Create(form, CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        var request = Assert.Single(managementService.CreateRequests);

        Assert.Equal(nameof(BotsController.Index), redirectResult.ActionName);
        Assert.Equal("Pilot A", request.Name);
        Assert.Equal("strategy-a", request.StrategyKey);
        Assert.Equal("BTCUSDT", request.Symbol);
        Assert.Equal(TradingBotDirectionMode.LongShort, request.DirectionMode);
        Assert.Equal("Pilot bot kaydedildi.", controller.TempData["BotControlSuccess"]);
    }

    [Fact]
    public async Task Create_Post_ReturnsView_WhenModelStateIsInvalid()
    {
        var managementService = CreateManagementService();
        var controller = CreateController(managementService, new FakeBotPilotControlService(), new FakeUserSettingsService(), "user-bot-05", "trace-bot-005");
        controller.ModelState.AddModelError("Form.Name", "required");

        var result = await controller.Create(CreateInput(), CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<BotManagementEditorViewModel>(viewResult.Model);

        Assert.Equal("Editor", viewResult.ViewName);
        Assert.False(model.IsEditMode);
        Assert.Empty(managementService.CreateRequests);
    }

    [Fact]
    public async Task Edit_Post_PersistsBot_AndRedirectsOnSuccess()
    {
        var managementService = CreateManagementService();
        managementService.UpdateResult = new BotManagementSaveResult(managementService.EditBotId, true, true, true, null, null);
        var controller = CreateController(managementService, new FakeBotPilotControlService(), new FakeUserSettingsService(), "user-bot-06", "trace-bot-006");
        var form = CreateInput();

        var result = await controller.Edit(managementService.EditBotId, form, CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        var request = Assert.Single(managementService.UpdateRequests);

        Assert.Equal(nameof(BotsController.Index), redirectResult.ActionName);
        Assert.Equal(managementService.EditBotId, request.BotId);
        Assert.Equal(TradingBotDirectionMode.LongShort, request.Command.DirectionMode);
        Assert.Equal("Pilot bot guncellendi.", controller.TempData["BotControlSuccess"]);
    }

    [Fact]
    public async Task Edit_Post_ReturnsNotFound_WhenBotIsMissing()
    {
        var managementService = new FakeBotManagementService();
        var controller = CreateController(managementService, new FakeBotPilotControlService(), new FakeUserSettingsService(), "user-bot-07", "trace-bot-007");

        var result = await controller.Edit(Guid.NewGuid(), CreateInput(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task SetEnabled_PostsToWriteService_AndRedirectsToIndex()
    {
        var service = new FakeBotPilotControlService
        {
            Result = new BotPilotToggleResult(Guid.NewGuid(), true, true, null, null)
        };
        var controller = CreateController(CreateManagementService(), service, new FakeUserSettingsService(), "user-bot-08", "trace-bot-008");
        var botId = Guid.NewGuid();

        var result = await controller.SetEnabled(botId, true, CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        var request = Assert.Single(service.Requests);

        Assert.Equal(nameof(BotsController.Index), redirectResult.ActionName);
        Assert.Equal(botId, request.BotId);
        Assert.True(request.IsEnabled);
        Assert.Equal("user-bot-08", request.OwnerUserId);
        Assert.Equal("user:user-bot-08", request.Actor);
        Assert.Equal("trace-bot-008", request.CorrelationId);
    }

    [Fact]
    public void Actions_RequireTradeOperationsPolicy()
    {
        var authorizeAttribute = Assert.Single(
            typeof(BotsController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>());

        Assert.Equal(ApplicationPolicies.TradeOperations, authorizeAttribute.Policy);
    }

    [Fact]
    public void PostActions_RequireAntiForgeryToken()
    {
        var createAttribute = Assert.Single(
            typeof(BotsController)
                .GetMethod(nameof(BotsController.Create), [typeof(BotManagementInputModel), typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true)
                .Cast<ValidateAntiForgeryTokenAttribute>());
        var editAttribute = Assert.Single(
            typeof(BotsController)
                .GetMethod(nameof(BotsController.Edit), [typeof(Guid), typeof(BotManagementInputModel), typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true)
                .Cast<ValidateAntiForgeryTokenAttribute>());
        var toggleAttribute = Assert.Single(
            typeof(BotsController)
                .GetMethod(nameof(BotsController.SetEnabled), [typeof(Guid), typeof(bool), typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true)
                .Cast<ValidateAntiForgeryTokenAttribute>());

        Assert.NotNull(createAttribute);
        Assert.NotNull(editAttribute);
        Assert.NotNull(toggleAttribute);
    }

    private static FakeBotManagementService CreateManagementService()
    {
        var editBotId = Guid.NewGuid();
        var editorSnapshot = new BotManagementEditorSnapshot(
            editBotId,
            new BotManagementDraftSnapshot("Pilot A", "strategy-a", "BTCUSDT", null, null, 1m, "ISOLATED", false, TradingBotDirectionMode.LongOnly),
            ["BTCUSDT", "ETHUSDT"],
            [new BotStrategyOptionSnapshot("strategy-a", "Strategy A", true)],
            [new BotExchangeAccountOptionSnapshot(Guid.NewGuid(), "Pilot Futures", true, true)]);

        return new FakeBotManagementService
        {
            EditBotId = editBotId,
            CreateSnapshot = editorSnapshot with { BotId = null },
            EditSnapshot = editorSnapshot
        };
    }

    private static BotManagementInputModel CreateInput()
    {
        return new BotManagementInputModel
        {
            Name = "Pilot A",
            StrategyKey = "strategy-a",
            Symbol = "BTCUSDT",
            Quantity = 0.001m,
            Leverage = 1m,
            MarginType = "ISOLATED",
            IsEnabled = false
        };
    }

    private static BotsController CreateController(
        FakeBotManagementService managementService,
        FakeBotPilotControlService pilotControlService,
        FakeUserSettingsService userSettingsService,
        string userId,
        string traceIdentifier)
    {
        var controller = new BotsController(managementService, pilotControlService, userSettingsService);
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
        return controller;
    }

    private sealed class FakeBotManagementService : IBotManagementService
    {
        public BotManagementPageSnapshot PageSnapshot { get; set; } = new([]);

        public BotManagementEditorSnapshot CreateSnapshot { get; set; } = new(
            null,
            new BotManagementDraftSnapshot(string.Empty, string.Empty, "BTCUSDT", null, null, 1m, "ISOLATED", false, TradingBotDirectionMode.LongOnly),
            ["BTCUSDT"],
            [],
            []);

        public BotManagementEditorSnapshot? EditSnapshot { get; set; }

        public Guid EditBotId { get; set; }

        public BotManagementSaveResult CreateResult { get; set; } = new(Guid.NewGuid(), true, true, false, null, null);

        public BotManagementSaveResult UpdateResult { get; set; } = new(Guid.NewGuid(), true, true, false, null, null);

        public List<BotManagementSaveCommand> CreateRequests { get; } = [];

        public List<(Guid BotId, BotManagementSaveCommand Command)> UpdateRequests { get; } = [];

        public Task<BotManagementPageSnapshot> GetPageAsync(string ownerUserId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PageSnapshot);
        }

        public Task<BotManagementEditorSnapshot> GetCreateEditorAsync(string ownerUserId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CreateSnapshot);
        }

        public Task<BotManagementEditorSnapshot?> GetEditEditorAsync(string ownerUserId, Guid botId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(botId == EditBotId ? EditSnapshot : null);
        }

        public Task<BotManagementSaveResult> CreateAsync(string ownerUserId, BotManagementSaveCommand command, string actor, string? correlationId = null, CancellationToken cancellationToken = default)
        {
            CreateRequests.Add(command);
            return Task.FromResult(CreateResult);
        }

        public Task<BotManagementSaveResult> UpdateAsync(string ownerUserId, Guid botId, BotManagementSaveCommand command, string actor, string? correlationId = null, CancellationToken cancellationToken = default)
        {
            UpdateRequests.Add((botId, command));
            return Task.FromResult(UpdateResult);
        }
    }

    private sealed class FakeBotPilotControlService : IBotPilotControlService
    {
        public BotPilotToggleResult Result { get; set; } = new(Guid.NewGuid(), true, true, null, null);

        public List<Request> Requests { get; } = [];

        public Task<BotPilotToggleResult> SetEnabledAsync(
            string ownerUserId,
            Guid botId,
            bool isEnabled,
            string actor,
            string? correlationId = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(new Request(ownerUserId, botId, isEnabled, actor, correlationId));
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeUserSettingsService : IUserSettingsService
    {
        public UserSettingsSnapshot Snapshot { get; set; } = new(
            "UTC",
            "UTC",
            "UTC",
            [new UserTimeZoneOptionSnapshot("UTC", "UTC")],
            new BinanceTimeSyncSnapshot(
                DateTime.UtcNow,
                DateTime.UtcNow,
                0,
                10,
                DateTime.UtcNow,
                "Synchronized",
                null));

        public Task<UserSettingsSnapshot?> GetAsync(string userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<UserSettingsSnapshot?>(Snapshot);
        }

        public Task<UserSettingsSaveResult> SaveAsync(string userId, UserSettingsSaveCommand command, string actor, string? correlationId = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed record Request(
        string OwnerUserId,
        Guid BotId,
        bool IsEnabled,
        string Actor,
        string? CorrelationId);

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
