using System.Security.Claims;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Contracts.Common;
using CoinBot.Domain.Enums;
using CoinBot.Web.Areas.Admin.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace CoinBot.UnitTests.Web;

public sealed class AdminControllerTests
{
    [Fact]
    public async Task Settings_LoadsSnapshots_AndMarksOpsAdminAsReadOnly()
    {
        var executionSnapshot = new GlobalExecutionSwitchSnapshot(
            TradeMasterSwitchState.Armed,
            DemoModeEnabled: true,
            IsPersisted: true);
        var globalSystemStateSnapshot = new GlobalSystemStateSnapshot(
            GlobalSystemStateKind.Active,
            "SYSTEM_ACTIVE",
            Message: null,
            "SystemDefault",
            CorrelationId: null,
            IsManualOverride: false,
            ExpiresAtUtc: null,
            UpdatedAtUtc: new DateTime(2026, 3, 24, 10, 0, 0, DateTimeKind.Utc),
            UpdatedByUserId: "ops-admin",
            UpdatedFromIp: "ip:masked",
            Version: 3,
            IsPersisted: true);
        var switchService = new FakeGlobalExecutionSwitchService { Snapshot = executionSnapshot };
        var stateService = new FakeGlobalSystemStateService { Snapshot = globalSystemStateSnapshot };
        var controller = CreateController(
            switchService,
            stateService,
            roles: [ApplicationRoles.OpsAdmin]);

        var result = await controller.Settings(CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.Equal(1, switchService.GetSnapshotCalls);
        Assert.Equal(1, stateService.GetSnapshotCalls);
        Assert.Same(executionSnapshot, controller.ViewData["AdminExecutionSwitchSnapshot"]);
        Assert.Same(globalSystemStateSnapshot, controller.ViewData["AdminGlobalSystemStateSnapshot"]);
        Assert.IsType<GlobalPolicySnapshot>(controller.ViewData["AdminGlobalPolicySnapshot"]);
        Assert.Equal(false, controller.ViewData["AdminCanEditGlobalPolicy"]);
        Assert.Equal("OpsAdmin", controller.ViewData["AdminRoleKey"]);
    }

    [Fact]
    public async Task SetTradeMasterState_PassesActorContextCorrelation_AndCompletesIdempotentCommand()
    {
        var switchService = new FakeGlobalExecutionSwitchService();
        var stateService = new FakeGlobalSystemStateService();
        var commandRegistry = new FakeAdminCommandRegistry();
        var auditLogService = new FakeAdminAuditLogService();
        var controller = CreateController(
            switchService,
            stateService,
            commandRegistry,
            auditLogService,
            userId: "super-admin",
            traceIdentifier: "trace-trade-1",
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.SetTradeMasterState(
            isArmed: true,
            reason: "Controlled enablement",
            commandId: "cmd-trade-001",
            reauthToken: "reauth-hook",
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        var call = Assert.Single(switchService.TradeMasterCalls);
        var completion = Assert.Single(commandRegistry.CompletionRequests);
        var auditLog = Assert.Single(auditLogService.Requests);

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Equal(TradeMasterSwitchState.Armed, call.TradeMasterState);
        Assert.Equal("admin:super-admin", call.Actor);
        Assert.Equal("trace-trade-1", call.CorrelationId);
        Assert.Contains("CommandId=cmd-trade-001", call.Context, StringComparison.Ordinal);
        Assert.Contains("Reason=Controlled enablement", call.Context, StringComparison.Ordinal);
        Assert.Equal("cmd-trade-001", commandRegistry.LastStartRequest!.CommandId);
        Assert.Equal(AdminCommandStatus.Completed, completion.Status);
        Assert.Equal("super-admin", auditLog.ActorUserId);
        Assert.Equal("Admin.Settings.TradeMaster.Update", auditLog.ActionType);
        Assert.Equal("TradeMaster armed. Emir zinciri backend hard gate uzerinden acildi.", controller.TempData["AdminExecutionSwitchSuccess"]);
    }

    [Fact]
    public async Task SetTradeMasterState_WhenReasonMissing_FailsClosedWithoutCallingServices()
    {
        var switchService = new FakeGlobalExecutionSwitchService();
        var stateService = new FakeGlobalSystemStateService();
        var commandRegistry = new FakeAdminCommandRegistry();
        var auditLogService = new FakeAdminAuditLogService();
        var controller = CreateController(
            switchService,
            stateService,
            commandRegistry,
            auditLogService,
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.SetTradeMasterState(
            isArmed: true,
            reason: "   ",
            commandId: "cmd-missing-reason",
            reauthToken: null,
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Empty(switchService.TradeMasterCalls);
        Assert.Null(commandRegistry.LastStartRequest);
        Assert.Empty(auditLogService.Requests);
        Assert.Equal("Audit reason zorunludur.", controller.TempData["AdminExecutionSwitchError"]);
    }

    [Fact]
    public async Task SetDemoMode_DisableFlow_RequiresApprovalReferenceAndPassesAuditContext()
    {
        var switchService = new FakeGlobalExecutionSwitchService();
        var stateService = new FakeGlobalSystemStateService();
        var commandRegistry = new FakeAdminCommandRegistry();
        var auditLogService = new FakeAdminAuditLogService();
        var controller = CreateController(
            switchService,
            stateService,
            commandRegistry,
            auditLogService,
            userId: "super-admin",
            traceIdentifier: "trace-demo-1",
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.SetDemoMode(
            isEnabled: false,
            reason: "Planned live window",
            commandId: "cmd-demo-001",
            reauthToken: "reauth-hook",
            liveApprovalReference: "chg-9001",
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        var call = Assert.Single(switchService.DemoModeCalls);
        var completion = Assert.Single(commandRegistry.CompletionRequests);

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.False(call.IsEnabled);
        Assert.NotNull(call.LiveApproval);
        Assert.Equal("chg-9001", call.LiveApproval!.ApprovalReference);
        Assert.Equal("admin:super-admin", call.Actor);
        Assert.Equal("trace-demo-1", call.CorrelationId);
        Assert.Contains("CommandId=cmd-demo-001", call.Context, StringComparison.Ordinal);
        Assert.Equal(AdminCommandStatus.Completed, completion.Status);
        Assert.Equal("DemoMode disabled. Live execution yalnizca approval reference ile acildi.", controller.TempData["AdminExecutionSwitchSuccess"]);
    }

    [Fact]
    public async Task SetGlobalSystemState_WritesAudit_AndReturnsSuccess()
    {
        var switchService = new FakeGlobalExecutionSwitchService();
        var stateService = new FakeGlobalSystemStateService();
        var commandRegistry = new FakeAdminCommandRegistry();
        var auditLogService = new FakeAdminAuditLogService();
        var controller = CreateController(
            switchService,
            stateService,
            commandRegistry,
            auditLogService,
            userId: "super-admin",
            traceIdentifier: "trace-gs-1",
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.SetGlobalSystemState(
            state: GlobalSystemStateKind.Maintenance,
            reason: "Planned maintenance",
            reasonCode: "PLANNED_MAINTENANCE",
            message: "Exchange sync freeze",
            expiresAtUtc: new DateTime(2026, 3, 24, 23, 0, 0, DateTimeKind.Utc),
            commandId: "cmd-gs-001",
            reauthToken: "reauth-hook",
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        var request = Assert.Single(stateService.SetRequests);
        var completion = Assert.Single(commandRegistry.CompletionRequests);
        var auditLog = Assert.Single(auditLogService.Requests);

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Equal(GlobalSystemStateKind.Maintenance, request.State);
        Assert.Equal("PLANNED_MAINTENANCE", request.ReasonCode);
        Assert.Equal("AdminPortal.Settings", request.Source);
        Assert.Equal("super-admin", request.UpdatedByUserId);
        Assert.Equal(AdminCommandStatus.Completed, completion.Status);
        Assert.Equal("Admin.Settings.GlobalSystemState.Update", auditLog.ActionType);
        Assert.Equal("Global system state set to Maintenance.", controller.TempData["AdminGlobalSystemStateSuccess"]);
    }

    [Fact]
    public async Task SetTradeMasterState_WhenCommandAlreadyCompleted_ReturnsPreviousResultWithoutExecutingAgain()
    {
        var switchService = new FakeGlobalExecutionSwitchService();
        var stateService = new FakeGlobalSystemStateService();
        var commandRegistry = new FakeAdminCommandRegistry
        {
            StartResult = new AdminCommandStartResult(
                AdminCommandStartDisposition.AlreadyCompleted,
                AdminCommandStatus.Completed,
                "Previous result reused.")
        };
        var auditLogService = new FakeAdminAuditLogService();
        var controller = CreateController(
            switchService,
            stateService,
            commandRegistry,
            auditLogService,
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.SetTradeMasterState(
            isArmed: true,
            reason: "Retry same command",
            commandId: "cmd-trade-002",
            reauthToken: null,
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        var auditLog = Assert.Single(auditLogService.Requests);

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Empty(switchService.TradeMasterCalls);
        Assert.Empty(commandRegistry.CompletionRequests);
        Assert.Equal("Previous result reused.", controller.TempData["AdminExecutionSwitchSuccess"]);
        Assert.Equal("Admin.Settings.TradeMaster.Update.IdempotentHit", auditLog.ActionType);
    }

    [Fact]
    public async Task Audit_ReturnsTraceRows_FromReadModelSearch()
    {
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            traceService: new FakeTraceService
            {
                SearchResults =
                [
                    new AdminTraceListItem(
                        "corr-admin-1",
                        "user-01",
                        "BTCUSDT",
                        "1m",
                        "StrategyVersion:abc",
                        "Persisted",
                        null,
                        "Binance.PrivateRest",
                        1,
                        1,
                        new DateTime(2026, 3, 24, 12, 0, 0, DateTimeKind.Utc))
                ]
            });

        var result = await controller.Audit("corr-admin-1", null, null, null, null, CancellationToken.None);
        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<IReadOnlyCollection<AdminTraceListItem>>(viewResult.Model);

        Assert.Single(model);
        Assert.Equal("corr-admin-1", model.Single().CorrelationId);
    }

    [Fact]
    public async Task ExchangeAccounts_ReturnsMaskedCredentialSummaries()
    {
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            apiCredentialValidationService: new FakeApiCredentialValidationService
            {
                Summaries =
                [
                    new ApiCredentialAdminSummary(
                        Guid.NewGuid(),
                        "user-02",
                        "Binance",
                        "Main",
                        IsReadOnly: false,
                        "ABC123***DEF4",
                        "Valid",
                        "Trade=Y; Withdraw=N",
                        new DateTime(2026, 3, 24, 12, 5, 0, DateTimeKind.Utc),
                        null)
                ]
            });

        var result = await controller.ExchangeAccounts(CancellationToken.None);
        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<IReadOnlyCollection<ApiCredentialAdminSummary>>(viewResult.Model);

        Assert.Single(model);
        Assert.Equal("ABC123***DEF4", model.Single().MaskedFingerprint);
    }

    [Fact]
    public async Task Settings_UsesGlobalPolicyReadModel_WhenAvailable()
    {
        var now = new DateTime(2026, 3, 24, 12, 30, 0, DateTimeKind.Utc);
        var policy = new RiskPolicySnapshot(
            "GlobalRiskPolicy",
            new ExecutionGuardPolicy(250_000m, 500_000m, 20, CloseOnlyBlocksNewPositions: true),
            new AutonomyPolicy(AutonomyPolicyMode.LowRiskAutoAct, RequireManualApprovalForLive: true),
            [
                new SymbolRestriction("BTCUSDT", SymbolRestrictionState.CloseOnly, "manual review", now, "super-admin")
            ]);
        var policySnapshot = new GlobalPolicySnapshot(
            policy,
            CurrentVersion: 7,
            LastUpdatedAtUtc: now,
            LastUpdatedByUserId: "super-admin",
            LastChangeSummary: "Manual policy update",
            IsPersisted: true,
            Versions:
            [
                new GlobalPolicyVersionSnapshot(
                    7,
                    now,
                    "super-admin",
                    "Manual policy update",
                    Array.Empty<GlobalPolicyDiffEntry>(),
                    policy)
            ]);
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            globalPolicyEngine: new FakeGlobalPolicyEngine(policySnapshot));

        var result = await controller.Settings(CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.Same(policySnapshot, controller.ViewData["AdminGlobalPolicySnapshot"]);
    }

    [Fact]
    public async Task PreviewCrisisEscalation_StoresPreviewState_AndSettingsLoadsIt()
    {
        var crisisService = new FakeCrisisEscalationService
        {
            PreviewResult = new CrisisEscalationPreview(
                CrisisEscalationLevel.OrderPurge,
                "PURGE:USER:user-01",
                AffectedUserCount: 1,
                AffectedSymbolCount: 2,
                OpenPositionCount: 3,
                PendingOrderCount: 4,
                EstimatedExposure: 1250.5m,
                RequiresReauth: false,
                RequiresSecondApproval: false,
                PreviewStamp: "preview-stamp-1")
        };
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            crisisEscalationService: crisisService,
            roles: [ApplicationRoles.SuperAdmin]);

        var previewResult = await controller.PreviewCrisisEscalation(
            CrisisEscalationLevel.OrderPurge,
            "PURGE:USER:user-01",
            "USER_TARGETED_PURGE",
            "Operator preview note",
            CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(previewResult);
        var previewRequest = Assert.Single(crisisService.PreviewRequests);

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Equal("PURGE:USER:user-01", previewRequest.Scope);

        await controller.Settings(CancellationToken.None);

        var viewModel = Assert.IsType<CoinBot.Web.ViewModels.Admin.AdminCrisisEscalationPreviewViewModel>(
            controller.ViewData["AdminCrisisEscalationPreview"]);
        Assert.Equal("USER_TARGETED_PURGE", viewModel.ReasonCode);
        Assert.Equal("Operator preview note", viewModel.Message);
        Assert.Equal(4, viewModel.PendingOrderCount);
        Assert.Equal("preview-stamp-1", viewModel.PreviewStamp);
    }

    [Fact]
    public async Task ExecuteCrisisEscalation_CompletesRegistry_AndStoresSuccessMessage()
    {
        var commandRegistry = new FakeAdminCommandRegistry();
        var auditLogService = new FakeAdminAuditLogService();
        var crisisService = new FakeCrisisEscalationService
        {
            ExecutionResult = new CrisisEscalationExecutionResult(
                new CrisisEscalationPreview(
                    CrisisEscalationLevel.OrderPurge,
                    "GLOBAL_PURGE",
                    AffectedUserCount: 2,
                    AffectedSymbolCount: 3,
                    OpenPositionCount: 1,
                    PendingOrderCount: 4,
                    EstimatedExposure: 2500m,
                    RequiresReauth: false,
                    RequiresSecondApproval: false,
                    PreviewStamp: "preview-stamp-2"),
                PurgedOrderCount: 4,
                FlattenAttemptCount: 0,
                FlattenReuseCount: 0,
                FailedOperationCount: 0,
                Summary: "Level=OrderPurge | Scope=GLOBAL_PURGE | PurgedOrders=4")
        };
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            commandRegistry: commandRegistry,
            auditLogService: auditLogService,
            crisisEscalationService: crisisService,
            userId: "super-admin",
            traceIdentifier: "trace-crisis-1",
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.ExecuteCrisisEscalation(
            CrisisEscalationLevel.OrderPurge,
            "GLOBAL_PURGE",
            "CRISIS_ORDER_PURGE",
            "Operator note",
            "Market integrity protection",
            "preview-stamp-2",
            "cmd-crisis-001",
            reauthToken: null,
            secondApprovalReference: null,
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        var executeRequest = Assert.Single(crisisService.ExecuteRequests);
        var completion = Assert.Single(commandRegistry.CompletionRequests);
        var auditLog = Assert.Single(auditLogService.Requests);

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Equal("cmd-crisis-001", executeRequest.CommandId);
        Assert.Equal("trace-crisis-1", executeRequest.CorrelationId);
        Assert.Equal(AdminCommandStatus.Completed, completion.Status);
        Assert.Equal("Admin.Settings.CrisisEscalation.Execute", auditLog.ActionType);
        Assert.Equal("Level=OrderPurge | Scope=GLOBAL_PURGE | PurgedOrders=4", controller.TempData["AdminCrisisEscalationSuccess"]);
    }

    [Fact]
    public async Task SystemHealth_UsesMonitoringReadModel_WhenAvailable()
    {
        var now = new DateTime(2026, 3, 24, 12, 45, 0, DateTimeKind.Utc);
        var dashboard = new MonitoringDashboardSnapshot(
            [
                new HealthSnapshot(
                    "market-watchdog",
                    "MarketWatchdog",
                    "Market Watchdog",
                    MonitoringHealthState.Healthy,
                    MonitoringFreshnessTier.Hot,
                    CircuitBreakerStateCode.Closed,
                    now,
                    new MonitoringMetricsSnapshot(
                        BinancePingMs: 12,
                        WebSocketStaleDurationSeconds: 1,
                        LastMessageAgeSeconds: 1,
                        ReconnectCount: 0,
                        StreamGapCount: 0,
                        RateLimitUsage: 17,
                        DbLatencyMs: 5,
                        RedisLatencyMs: null,
                        SignalRActiveConnectionCount: 3,
                        WorkerLastHeartbeatAtUtc: now,
                        ConsecutiveFailureCount: 0,
                        SnapshotAgeSeconds: 1),
                    "State=Normal",
                    now)
            ],
            [
                new WorkerHeartbeat(
                    "monitoring-worker",
                    "Monitoring Worker",
                    MonitoringHealthState.Healthy,
                    MonitoringFreshnessTier.Hot,
                    CircuitBreakerStateCode.Closed,
                    now,
                    now,
                    0,
                    null,
                    "Monitoring cycle completed",
                    0,
                    "Monitoring cycle completed")
            ],
            now);
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            monitoringReadModelService: new FakeAdminMonitoringReadModelService(dashboard));

        var result = await controller.SystemHealth(CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.Same(dashboard, controller.ViewData["AdminMonitoringDashboardSnapshot"]);
    }

    [Fact]
    public async Task SetGlobalSystemState_QueuesApproval_WhenWorkflowServiceAvailable()
    {
        var approvalWorkflowService = new FakeApprovalWorkflowService
        {
            EnqueueResult = CreateApprovalDetailSnapshot("APR-queue-1")
        };
        var commandRegistry = new FakeAdminCommandRegistry();
        var stateService = new FakeGlobalSystemStateService();
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            stateService,
            commandRegistry,
            approvalWorkflowService: approvalWorkflowService,
            userId: "super-admin",
            traceIdentifier: "trace-gs-approval-1",
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.SetGlobalSystemState(
            state: GlobalSystemStateKind.Maintenance,
            reason: "Planned maintenance",
            reasonCode: "PLANNED_MAINTENANCE",
            message: "Approval queue test",
            expiresAtUtc: new DateTime(2026, 3, 24, 23, 0, 0, DateTimeKind.Utc),
            commandId: "cmd-gs-approval-1",
            reauthToken: "reauth-hook",
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        var approvalRequest = Assert.Single(approvalWorkflowService.EnqueueRequests);

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Equal(ApprovalQueueOperationType.GlobalSystemStateUpdate, approvalRequest.OperationType);
        Assert.Equal(2, approvalRequest.RequiredApprovals);
        Assert.Equal("GlobalSystemState", approvalRequest.TargetType);
        Assert.Equal("Singleton", approvalRequest.TargetId);
        Assert.Empty(stateService.SetRequests);
        Assert.Empty(commandRegistry.CompletionRequests);
        Assert.Contains("queued", controller.TempData["AdminGlobalSystemStateSuccess"]?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Approvals_ReturnsPendingItems_AndApprovalDetail()
    {
        var approvalDetail = CreateApprovalDetailSnapshot("APR-queue-2");
        var approvalWorkflowService = new FakeApprovalWorkflowService
        {
            EnqueueResult = approvalDetail,
            PendingItems =
            [
                new ApprovalQueueListItem(
                    "APR-queue-2",
                    ApprovalQueueOperationType.GlobalSystemStateUpdate,
                    ApprovalQueueStatus.Pending,
                    IncidentSeverity.Warning,
                    "Global system state update",
                    "Maintenance request",
                    "GlobalSystemState",
                    "Singleton",
                    "requestor-1",
                    2,
                    1,
                    new DateTime(2026, 3, 24, 22, 0, 0, DateTimeKind.Utc),
                    "corr-approval-1",
                    "cmd-approval-1",
                    "INC-approval-1",
                    new DateTime(2026, 3, 24, 20, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 3, 24, 20, 5, 0, DateTimeKind.Utc))
            ],
            Detail = approvalDetail
        };
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            approvalWorkflowService: approvalWorkflowService,
            userId: "super-admin",
            roles: [ApplicationRoles.SuperAdmin]);

        var listResult = await controller.Approvals(cancellationToken: CancellationToken.None);
        var listView = Assert.IsType<ViewResult>(listResult);
        var listModel = Assert.IsAssignableFrom<IReadOnlyCollection<ApprovalQueueListItem>>(listView.Model);

        Assert.Single(listModel);
        Assert.True(controller.ViewData["AdminCanManageApprovals"] is bool canManage && canManage);

        var detailResult = await controller.ApprovalDetail("APR-queue-2", CancellationToken.None);
        var detailView = Assert.IsType<ViewResult>(detailResult);
        Assert.Same(approvalDetail, detailView.Model);
    }

    [Fact]
    public async Task GovernancePages_ReturnReadModels()
    {
        var now = new DateTime(2026, 3, 24, 13, 15, 0, DateTimeKind.Utc);
        var incidentDetail = CreateIncidentDetailSnapshot(now);
        var stateDetail = CreateStateHistoryDetailSnapshot(now);
        var policySnapshot = CreatePolicySnapshot(now);
        var governanceService = new FakeAdminGovernanceReadModelService
        {
            IncidentItems =
            [
                new IncidentListItem(
                    "INC-9001",
                    IncidentSeverity.Critical,
                    IncidentStatus.Resolved,
                    "Emergency flatten",
                    "Flatten completed",
                    ApprovalQueueOperationType.CrisisEscalationExecute,
                    "Crisis",
                    "GLOBAL_FLATTEN",
                    "corr-incident-1",
                    "cmd-incident-1",
                    "APR-9001",
                    4,
                    now,
                    now)
            ],
            IncidentDetail = incidentDetail,
            StateHistoryItems =
            [
                new SystemStateHistoryListItem(
                    "GST-000123",
                    12,
                    GlobalSystemStateKind.Maintenance,
                    "PLANNED_MAINTENANCE",
                    "AdminPortal.Settings",
                    true,
                    now.AddHours(1),
                    "corr-state-1",
                    "APR-9001",
                    "INC-9001",
                    now)
            ],
            StateHistoryDetail = stateDetail
        };
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            governanceReadModelService: governanceService,
            globalPolicyEngine: new FakeGlobalPolicyEngine(policySnapshot));

        var incidentsResult = await controller.Incidents(cancellationToken: CancellationToken.None);
        var incidentsView = Assert.IsType<ViewResult>(incidentsResult);
        var incidentsModel = Assert.IsAssignableFrom<IReadOnlyCollection<IncidentListItem>>(incidentsView.Model);
        Assert.Single(incidentsModel);

        var incidentDetailResult = await controller.IncidentDetail("INC-9001", CancellationToken.None);
        var incidentDetailView = Assert.IsType<ViewResult>(incidentDetailResult);
        Assert.Same(incidentDetail, incidentDetailView.Model);

        var stateHistoryResult = await controller.SystemStateHistory(cancellationToken: CancellationToken.None);
        var stateHistoryView = Assert.IsType<ViewResult>(stateHistoryResult);
        var stateHistoryModel = Assert.IsAssignableFrom<IReadOnlyCollection<SystemStateHistoryListItem>>(stateHistoryView.Model);
        Assert.Single(stateHistoryModel);

        var stateHistoryDetailResult = await controller.SystemStateHistoryDetail("GST-000123", CancellationToken.None);
        var stateHistoryDetailView = Assert.IsType<ViewResult>(stateHistoryDetailResult);
        Assert.Same(stateDetail, stateHistoryDetailView.Model);

        var configHistoryResult = await controller.ConfigHistory(CancellationToken.None);
        Assert.IsType<ViewResult>(configHistoryResult);
        Assert.Same(policySnapshot, controller.ViewData["AdminGlobalPolicySnapshot"]);

        var configHistoryDetailResult = await controller.ConfigHistoryDetail(12, CancellationToken.None);
        var configHistoryDetailView = Assert.IsType<ViewResult>(configHistoryDetailResult);
        Assert.Same(policySnapshot, configHistoryDetailView.Model);
        Assert.True(controller.ViewData["AdminSelectedConfigVersion"] is GlobalPolicyVersionSnapshot selectedVersion && selectedVersion.Version == 12);
    }

    private static AdminController CreateController(
        FakeGlobalExecutionSwitchService switchService,
        FakeGlobalSystemStateService? stateService = null,
        FakeAdminCommandRegistry? commandRegistry = null,
        FakeAdminAuditLogService? auditLogService = null,
        FakeTraceService? traceService = null,
        FakeApiCredentialValidationService? apiCredentialValidationService = null,
        FakeApprovalWorkflowService? approvalWorkflowService = null,
        FakeAdminGovernanceReadModelService? governanceReadModelService = null,
        FakeAdminMonitoringReadModelService? monitoringReadModelService = null,
        FakeGlobalPolicyEngine? globalPolicyEngine = null,
        FakeCrisisEscalationService? crisisEscalationService = null,
        string userId = "admin-01",
        string traceIdentifier = "trace-001",
        string[]? roles = null)
    {
        roles ??= [ApplicationRoles.SuperAdmin];
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var httpContext = new DefaultHttpContext
        {
            TraceIdentifier = traceIdentifier,
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
        };

        return new AdminController(
            globalExecutionSwitchService: switchService,
            globalSystemStateService: stateService ?? new FakeGlobalSystemStateService(),
            adminCommandRegistry: commandRegistry ?? new FakeAdminCommandRegistry(),
            adminAuditLogService: auditLogService ?? new FakeAdminAuditLogService(),
            traceService: traceService ?? new FakeTraceService(),
            apiCredentialValidationService: apiCredentialValidationService ?? new FakeApiCredentialValidationService(),
            approvalWorkflowService: approvalWorkflowService,
            adminGovernanceReadModelService: governanceReadModelService,
            adminMonitoringReadModelService: monitoringReadModelService ?? new FakeAdminMonitoringReadModelService(),
            globalPolicyEngine: globalPolicyEngine ?? new FakeGlobalPolicyEngine(),
            crisisEscalationService: crisisEscalationService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            },
            TempData = new TempDataDictionary(httpContext, new TestTempDataProvider())
        };
    }

    private static ApprovalQueueDetailSnapshot CreateApprovalDetailSnapshot(string approvalReference)
    {
        var now = new DateTime(2026, 3, 24, 12, 30, 0, DateTimeKind.Utc);

        return new ApprovalQueueDetailSnapshot(
            approvalReference,
            ApprovalQueueOperationType.GlobalSystemStateUpdate,
            ApprovalQueueStatus.Pending,
            IncidentSeverity.Warning,
            "Global system state update",
            "Maintenance request",
            "GlobalSystemState",
            "Singleton",
            "requestor-1",
            "Maintenance required",
            "{\"state\":\"Maintenance\"}",
            2,
            1,
            now.AddHours(1),
            "corr-approval-1",
            "cmd-approval-1",
            "dec-approval-1",
            "exe-approval-1",
            "INC-approval-1",
            "GST-000123",
            null,
            now,
            now,
            null,
            null,
            null,
            null,
            null,
            null,
            [
                new ApprovalActionSnapshot(
                    1,
                    ApprovalActionType.Approved,
                    "approver-1",
                    "Initial approval",
                    "corr-approval-1",
                    "cmd-approval-1",
                    "dec-approval-1",
                    "exe-approval-1",
                    now)
            ]);
    }

    private static IncidentDetailSnapshot CreateIncidentDetailSnapshot(DateTime now)
    {
        return new IncidentDetailSnapshot(
            "INC-9001",
            IncidentSeverity.Critical,
            IncidentStatus.Resolved,
            "Emergency flatten",
            "Flatten completed",
            "Detailed incident payload",
            ApprovalQueueOperationType.CrisisEscalationExecute,
            "Crisis",
            "GLOBAL_FLATTEN",
            "corr-incident-1",
            "cmd-incident-1",
            "dec-incident-1",
            "exe-incident-1",
            "APR-9001",
            "GST-000123",
            "BREAKER-001",
            "security-admin",
            now,
            now.AddMinutes(5),
            "security-admin",
            "Incident resolved",
            [
                new IncidentEventSnapshot(
                    IncidentEventType.IncidentCreated,
                    "Incident created",
                    "requestor-1",
                    "corr-incident-1",
                    "cmd-incident-1",
                    "dec-incident-1",
                    "exe-incident-1",
                    "APR-9001",
                    "GST-000123",
                    null,
                    "{\"kind\":\"incident\"}",
                    now),
                new IncidentEventSnapshot(
                    IncidentEventType.ApprovalQueued,
                    "Queued for approval",
                    "requestor-1",
                    "corr-incident-1",
                    "cmd-incident-1",
                    "dec-incident-1",
                    "exe-incident-1",
                    "APR-9001",
                    "GST-000123",
                    null,
                    "{\"kind\":\"incident\"}",
                    now.AddMinutes(1))
            ]);
    }

    private static SystemStateHistoryDetailSnapshot CreateStateHistoryDetailSnapshot(DateTime now)
    {
        return new SystemStateHistoryDetailSnapshot(
            "GST-000123",
            12,
            GlobalSystemStateKind.Maintenance,
            "PLANNED_MAINTENANCE",
            "Planned maintenance window",
            "AdminPortal.Settings",
            true,
            now.AddHours(1),
            "corr-state-1",
            "cmd-state-1",
            "APR-9001",
            "INC-9001",
            "BREAKER-001",
            "Dependency",
            "Open",
            "super-admin",
            "ip:masked",
            "Active",
            "State moved to maintenance",
            now);
    }

    private static GlobalPolicySnapshot CreatePolicySnapshot(DateTime now)
    {
        var policy = new RiskPolicySnapshot(
            "GlobalRiskPolicy",
            new ExecutionGuardPolicy(250_000m, 500_000m, 20, CloseOnlyBlocksNewPositions: true),
            new AutonomyPolicy(AutonomyPolicyMode.ManualApprovalRequired, RequireManualApprovalForLive: true),
            [
                new SymbolRestriction("BTCUSDT", SymbolRestrictionState.CloseOnly, "manual review", now, "super-admin")
            ]);

        var version = new GlobalPolicyVersionSnapshot(
            12,
            now,
            "super-admin",
            "Manual policy update",
            [
                new GlobalPolicyDiffEntry("AutonomyPolicy.Mode", "LowRiskAutoAct", "ManualApprovalRequired", "Modified"),
                new GlobalPolicyDiffEntry("SymbolRestrictions[0].State", "Open", "CloseOnly", "Modified")
            ],
            policy,
            "AdminPortal.Settings",
            "corr-policy-1",
            null);

        return new GlobalPolicySnapshot(
            policy,
            12,
            now,
            "super-admin",
            "Manual policy update",
            true,
            [version]);
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
            Snapshot = Snapshot with
            {
                DemoModeEnabled = isEnabled,
                IsPersisted = true,
                LiveModeApprovedAtUtc = isEnabled ? null : new DateTime(2026, 3, 24, 10, 30, 0, DateTimeKind.Utc)
            };

            return Task.FromResult(Snapshot);
        }
    }

    private sealed class FakeGlobalSystemStateService : IGlobalSystemStateService
    {
        public GlobalSystemStateSnapshot Snapshot { get; set; } = new(
            GlobalSystemStateKind.Active,
            "SYSTEM_ACTIVE",
            Message: null,
            "SystemDefault",
            CorrelationId: null,
            IsManualOverride: false,
            ExpiresAtUtc: null,
            UpdatedAtUtc: null,
            UpdatedByUserId: null,
            UpdatedFromIp: null,
            Version: 0,
            IsPersisted: false);

        public int GetSnapshotCalls { get; private set; }

        public List<GlobalSystemStateSetRequest> SetRequests { get; } = [];

        public Task<GlobalSystemStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            GetSnapshotCalls++;
            return Task.FromResult(Snapshot);
        }

        public Task<GlobalSystemStateSnapshot> SetStateAsync(
            GlobalSystemStateSetRequest request,
            CancellationToken cancellationToken = default)
        {
            SetRequests.Add(request);
            Snapshot = new GlobalSystemStateSnapshot(
                request.State,
                request.ReasonCode,
                request.Message,
                request.Source,
                request.CorrelationId,
                request.IsManualOverride,
                request.ExpiresAtUtc,
                UpdatedAtUtc: new DateTime(2026, 3, 24, 11, 0, 0, DateTimeKind.Utc),
                request.UpdatedByUserId,
                request.UpdatedFromIp,
                Version: Snapshot.Version + 1,
                IsPersisted: true);

            return Task.FromResult(Snapshot);
        }
    }

    private sealed class FakeAdminCommandRegistry : IAdminCommandRegistry
    {
        public AdminCommandStartResult StartResult { get; set; } = new(
            AdminCommandStartDisposition.Started,
            AdminCommandStatus.Running,
            ResultSummary: null);

        public AdminCommandStartRequest? LastStartRequest { get; private set; }

        public List<AdminCommandCompletionRequest> CompletionRequests { get; } = [];

        public Task<AdminCommandStartResult> TryStartAsync(
            AdminCommandStartRequest request,
            CancellationToken cancellationToken = default)
        {
            LastStartRequest = request;
            return Task.FromResult(StartResult);
        }

        public Task CompleteAsync(
            AdminCommandCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            CompletionRequests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAdminAuditLogService : IAdminAuditLogService
    {
        public List<AdminAuditLogWriteRequest> Requests { get; } = [];

        public Task WriteAsync(AdminAuditLogWriteRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeApprovalWorkflowService : IApprovalWorkflowService
    {
        public ApprovalQueueDetailSnapshot EnqueueResult { get; set; } = CreateApprovalDetailSnapshot("APR-default");

        public ApprovalQueueDetailSnapshot? Detail { get; set; }

        public IReadOnlyCollection<ApprovalQueueListItem> PendingItems { get; set; } = Array.Empty<ApprovalQueueListItem>();

        public List<ApprovalQueueEnqueueRequest> EnqueueRequests { get; } = [];

        public List<ApprovalQueueDecisionRequest> ApproveRequests { get; } = [];

        public List<ApprovalQueueDecisionRequest> RejectRequests { get; } = [];

        public Task<ApprovalQueueDetailSnapshot> EnqueueAsync(ApprovalQueueEnqueueRequest request, CancellationToken cancellationToken = default)
        {
            EnqueueRequests.Add(request);
            return Task.FromResult(EnqueueResult);
        }

        public Task<IReadOnlyCollection<ApprovalQueueListItem>> ListPendingAsync(int take = 100, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PendingItems);
        }

        public Task<ApprovalQueueDetailSnapshot?> GetDetailAsync(string approvalReference, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ApprovalQueueDetailSnapshot?>(Detail ?? EnqueueResult);
        }

        public Task<ApprovalQueueDetailSnapshot> ApproveAsync(ApprovalQueueDecisionRequest request, CancellationToken cancellationToken = default)
        {
            ApproveRequests.Add(request);
            return Task.FromResult(Detail ?? EnqueueResult);
        }

        public Task<ApprovalQueueDetailSnapshot> RejectAsync(ApprovalQueueDecisionRequest request, CancellationToken cancellationToken = default)
        {
            RejectRequests.Add(request);
            return Task.FromResult(Detail ?? EnqueueResult);
        }

        public Task<ApprovalQueueDetailSnapshot> MarkExecutedAsync(string approvalReference, string actorUserId, string? summary, string? correlationId = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Detail ?? EnqueueResult);
        }

        public Task<ApprovalQueueDetailSnapshot> MarkFailedAsync(string approvalReference, string actorUserId, string summary, string? correlationId = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Detail ?? EnqueueResult);
        }

        public Task<int> ExpirePendingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }
    }

    private sealed class FakeAdminGovernanceReadModelService : IAdminGovernanceReadModelService
    {
        public IReadOnlyCollection<IncidentListItem> IncidentItems { get; set; } = Array.Empty<IncidentListItem>();

        public IncidentDetailSnapshot? IncidentDetail { get; set; }

        public IReadOnlyCollection<SystemStateHistoryListItem> StateHistoryItems { get; set; } = Array.Empty<SystemStateHistoryListItem>();

        public SystemStateHistoryDetailSnapshot? StateHistoryDetail { get; set; }

        public Task<IReadOnlyCollection<IncidentListItem>> ListIncidentsAsync(int take = 100, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(IncidentItems);
        }

        public Task<IncidentDetailSnapshot?> GetIncidentDetailAsync(string incidentReference, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(IncidentDetail);
        }

        public Task<IReadOnlyCollection<SystemStateHistoryListItem>> ListSystemStateHistoryAsync(int take = 100, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(StateHistoryItems);
        }

        public Task<SystemStateHistoryDetailSnapshot?> GetSystemStateHistoryDetailAsync(string historyReference, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(StateHistoryDetail);
        }
    }

    private sealed class FakeTraceService : ITraceService
    {
        public IReadOnlyCollection<AdminTraceListItem> SearchResults { get; set; } = Array.Empty<AdminTraceListItem>();

        public AdminTraceDetailSnapshot? DetailSnapshot { get; set; }

        public Task<DecisionTraceSnapshot> WriteDecisionTraceAsync(DecisionTraceWriteRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExecutionTraceSnapshot> WriteExecutionTraceAsync(ExecutionTraceWriteRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<DecisionTraceSnapshot?> GetDecisionTraceByStrategySignalIdAsync(Guid strategySignalId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<DecisionTraceSnapshot?>(null);
        }

        public Task<IReadOnlyCollection<AdminTraceListItem>> SearchAsync(AdminTraceSearchRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SearchResults);
        }

        public Task<AdminTraceDetailSnapshot?> GetDetailAsync(string correlationId, string? decisionId = null, string? executionAttemptId = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(DetailSnapshot);
        }
    }

    private sealed class FakeApiCredentialValidationService : IApiCredentialValidationService
    {
        public IReadOnlyCollection<ApiCredentialAdminSummary> Summaries { get; set; } = Array.Empty<ApiCredentialAdminSummary>();

        public Task UpsertStoredCredentialAsync(ApiCredentialStoreMirrorRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ApiCredentialValidationSnapshot> RecordValidationAsync(ApiCredentialValidationRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyCollection<ApiCredentialAdminSummary>> ListAdminSummariesAsync(int take = 100, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Summaries);
        }
    }

    private sealed class FakeAdminMonitoringReadModelService : IAdminMonitoringReadModelService
    {
        public MonitoringDashboardSnapshot Snapshot { get; set; } = MonitoringDashboardSnapshot.Empty(new DateTime(2026, 3, 24, 12, 0, 0, DateTimeKind.Utc));

        public FakeAdminMonitoringReadModelService()
        {
        }

        public FakeAdminMonitoringReadModelService(MonitoringDashboardSnapshot snapshot)
        {
            Snapshot = snapshot;
        }

        public Task<MonitoringDashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class FakeCrisisEscalationService : ICrisisEscalationService
    {
        public CrisisEscalationPreview PreviewResult { get; set; } = new(
            CrisisEscalationLevel.SoftHalt,
            "GLOBAL_SOFT_HALT",
            AffectedUserCount: 0,
            AffectedSymbolCount: 0,
            OpenPositionCount: 0,
            PendingOrderCount: 0,
            EstimatedExposure: 0m,
            RequiresReauth: false,
            RequiresSecondApproval: false,
            PreviewStamp: "preview-default");

        public CrisisEscalationExecutionResult ExecutionResult { get; set; } = new(
            new CrisisEscalationPreview(
                CrisisEscalationLevel.SoftHalt,
                "GLOBAL_SOFT_HALT",
                AffectedUserCount: 0,
                AffectedSymbolCount: 0,
                OpenPositionCount: 0,
                PendingOrderCount: 0,
                EstimatedExposure: 0m,
                RequiresReauth: false,
                RequiresSecondApproval: false,
                PreviewStamp: "preview-default"),
            PurgedOrderCount: 0,
            FlattenAttemptCount: 0,
            FlattenReuseCount: 0,
            FailedOperationCount: 0,
            Summary: "Level=SoftHalt | Scope=GLOBAL_SOFT_HALT");

        public List<CrisisEscalationPreviewRequest> PreviewRequests { get; } = [];

        public List<CrisisEscalationExecuteRequest> ExecuteRequests { get; } = [];

        public Task<CrisisEscalationPreview> PreviewAsync(
            CrisisEscalationPreviewRequest request,
            CancellationToken cancellationToken = default)
        {
            PreviewRequests.Add(request);
            return Task.FromResult(PreviewResult);
        }

        public Task<CrisisEscalationExecutionResult> ExecuteAsync(
            CrisisEscalationExecuteRequest request,
            CancellationToken cancellationToken = default)
        {
            ExecuteRequests.Add(request);
            return Task.FromResult(ExecutionResult);
        }
    }

    private sealed class FakeGlobalPolicyEngine : IGlobalPolicyEngine
    {
        public GlobalPolicySnapshot Snapshot { get; set; } = GlobalPolicySnapshot.CreateDefault(new DateTime(2026, 3, 24, 12, 0, 0, DateTimeKind.Utc));

        public FakeGlobalPolicyEngine()
        {
        }

        public FakeGlobalPolicyEngine(GlobalPolicySnapshot snapshot)
        {
            Snapshot = snapshot;
        }

        public Task<GlobalPolicySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Snapshot);
        }

        public Task<GlobalPolicyEvaluationResult> EvaluateAsync(GlobalPolicyEvaluationRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GlobalPolicyEvaluationResult(false, null, null, Snapshot.CurrentVersion, null, Snapshot.Policy.AutonomyPolicy.Mode));
        }

        public Task<GlobalPolicySnapshot> UpdateAsync(GlobalPolicyUpdateRequest request, CancellationToken cancellationToken = default)
        {
            Snapshot = Snapshot with { Policy = request.Policy, CurrentVersion = Snapshot.CurrentVersion + 1, LastUpdatedAtUtc = DateTime.UtcNow, LastUpdatedByUserId = request.ActorUserId, LastChangeSummary = request.Reason, IsPersisted = true };
            return Task.FromResult(Snapshot);
        }

        public Task<GlobalPolicySnapshot> RollbackAsync(GlobalPolicyRollbackRequest request, CancellationToken cancellationToken = default)
        {
            Snapshot = Snapshot with { CurrentVersion = Snapshot.CurrentVersion + 1, LastUpdatedAtUtc = DateTime.UtcNow, LastUpdatedByUserId = request.ActorUserId, LastChangeSummary = request.Reason, IsPersisted = true };
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
