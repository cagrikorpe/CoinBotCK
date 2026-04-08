using System.Security.Claims;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Contracts.Common;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Mfa;
using CoinBot.Web.Areas.Admin.Controllers;
using CoinBot.Web.ViewModels.Admin;
using CoinBot.Web.ViewModels.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Web;

public sealed class AdminControllerTests
{
    [Fact]
    public void Login_WhenUnauthenticated_RedirectsToAuthLogin_WithAdminLandingReturnUrl()
    {
        var controller = CreateController(new FakeGlobalExecutionSwitchService(), isAuthenticated: false);

        var result = controller.Login();

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Login", redirectResult.ActionName);
        Assert.Equal("Auth", redirectResult.ControllerName);
        Assert.Equal("/admin", redirectResult.RouteValues!["returnUrl"]);
    }

    [Fact]
    public void Login_NormalizesAuthShellReturnUrl_ToAvoidRedirectLoop()
    {
        var controller = CreateController(new FakeGlobalExecutionSwitchService(), isAuthenticated: false);

        var result = controller.Login("/Admin/Admin/Login?returnUrl=%2Fadmin");

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("/admin", redirectResult.RouteValues!["returnUrl"]);
    }

    [Fact]
    public void Login_WhenAuthenticatedWithAdminAccess_RedirectsToAdminLanding()
    {
        var controller = CreateController(new FakeGlobalExecutionSwitchService());

        var result = controller.Login("/Admin/Admin/Users");

        var redirectResult = Assert.IsType<LocalRedirectResult>(result);
        Assert.Equal("/Admin/Admin/Users", redirectResult.Url);
    }

    [Fact]
    public void Login_WhenAuthenticatedWithoutAdminAccess_RedirectsToAdminAccessDenied()
    {
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            roles: [ApplicationRoles.User],
            permissions: [ApplicationPermissions.TradeOperations]);

        var result = controller.Login("/Admin/Admin/Overview");

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AdminController.AccessDenied), redirectResult.ActionName);
        Assert.Equal("/Admin/Admin/Overview", redirectResult.RouteValues!["returnUrl"]);
    }

    [Fact]
    public void Mfa_WhenUnauthenticated_RedirectsToAuthMfa()
    {
        var controller = CreateController(new FakeGlobalExecutionSwitchService(), isAuthenticated: false);

        var result = controller.Mfa("/Admin/Admin/Overview", rememberMe: true);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Mfa", redirectResult.ActionName);
        Assert.Equal("Auth", redirectResult.ControllerName);
        Assert.Equal("/Admin/Admin/Overview", redirectResult.RouteValues!["returnUrl"]);
        Assert.Equal(true, redirectResult.RouteValues["rememberMe"]);
    }

    [Fact]
    public void Mfa_WhenAuthenticatedWithoutAdminAccess_RedirectsToAccessDenied()
    {
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            roles: [ApplicationRoles.User],
            permissions: [ApplicationPermissions.ExchangeManagement]);

        var result = controller.Mfa("/Admin/Admin/Overview");

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AdminController.AccessDenied), redirectResult.ActionName);
    }

    [Fact]
    public void Mfa_WhenAuthenticatedWithAdminAccess_RedirectsToAdminLanding()
    {
        var controller = CreateController(new FakeGlobalExecutionSwitchService());

        var result = controller.Mfa("/Admin/Admin/Overview");

        var redirectResult = Assert.IsType<LocalRedirectResult>(result);
        Assert.Equal("/Admin/Admin/Overview", redirectResult.Url);
    }

    [Fact]
    public void AccessDenied_WhenUnauthenticated_RedirectsToLogin()
    {
        var controller = CreateController(new FakeGlobalExecutionSwitchService(), isAuthenticated: false);

        var result = controller.AccessDenied("/Admin/Admin/Users");

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AdminController.Login), redirectResult.ActionName);
        Assert.Equal("/Admin/Admin/Users", redirectResult.RouteValues!["returnUrl"]);
    }

    [Fact]
    public void AccessDenied_WhenAuthenticatedWithAdminAccess_RedirectsToAdminLanding()
    {
        var controller = CreateController(new FakeGlobalExecutionSwitchService());

        var result = controller.AccessDenied("/Admin/Admin/Users");

        var redirectResult = Assert.IsType<LocalRedirectResult>(result);
        Assert.Equal("/Admin/Admin/Users", redirectResult.Url);
    }

    [Fact]
    public void AccessDenied_WhenAuthenticatedWithoutAdminAccess_RendersView()
    {
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            roles: [ApplicationRoles.User],
            permissions: [ApplicationPermissions.ExchangeManagement]);

        var result = controller.AccessDenied("/Admin/Admin/Users");

        var viewResult = Assert.IsType<ViewResult>(result);
        Assert.Equal("/Admin/Admin/Users", controller.ViewData["AdminAuthReturnUrl"]);
        Assert.Null(viewResult.Model);
    }

    [Fact]
    public void SessionExpired_WhenUnauthenticated_RendersView()
    {
        var controller = CreateController(new FakeGlobalExecutionSwitchService(), isAuthenticated: false);

        var result = controller.SessionExpired("/Admin/Admin/Overview");

        var viewResult = Assert.IsType<ViewResult>(result);
        Assert.Equal("/Admin/Admin/Overview", controller.ViewData["AdminAuthReturnUrl"]);
        Assert.Null(viewResult.Model);
    }

    [Fact]
    public void SessionExpired_WhenAuthenticatedWithAdminAccess_RedirectsToAdminLanding()
    {
        var controller = CreateController(new FakeGlobalExecutionSwitchService());

        var result = controller.SessionExpired("/Admin/Admin/Overview");

        var redirectResult = Assert.IsType<LocalRedirectResult>(result);
        Assert.Equal("/Admin/Admin/Overview", redirectResult.Url);
    }

    [Fact]
    public void PermissionDenied_WhenUnauthenticated_RedirectsToLogin()
    {
        var controller = CreateController(new FakeGlobalExecutionSwitchService(), isAuthenticated: false);

        var result = controller.PermissionDenied("/Admin/Admin/Users");

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AdminController.Login), redirectResult.ActionName);
        Assert.Equal("/Admin/Admin/Users", redirectResult.RouteValues!["returnUrl"]);
    }

    [Fact]
    public void PermissionDenied_WhenAuthenticatedWithAdminAccess_RedirectsToAdminLanding()
    {
        var controller = CreateController(new FakeGlobalExecutionSwitchService());

        var result = controller.PermissionDenied("/Admin/Admin/Users");

        var redirectResult = Assert.IsType<LocalRedirectResult>(result);
        Assert.Equal("/Admin/Admin/Users", redirectResult.Url);
    }

    [Fact]
    public void PermissionDenied_WhenAuthenticatedWithoutAdminAccess_RendersView()
    {
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            roles: [ApplicationRoles.User],
            permissions: [ApplicationPermissions.ExchangeManagement]);

        var result = controller.PermissionDenied("/Admin/Admin/Users");

        var viewResult = Assert.IsType<ViewResult>(result);
        Assert.Equal("/Admin/Admin/Users", controller.ViewData["AdminAuthReturnUrl"]);
        Assert.Null(viewResult.Model);
    }

    [Fact]
    public void RoleMatrix_RedirectsToUsers()
    {
        var controller = CreateController(new FakeGlobalExecutionSwitchService());

        var result = controller.RoleMatrix();

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AdminController.Users), redirectResult.ActionName);
        Assert.Equal("Admin", redirectResult.RouteValues!["area"]);
    }

    [Fact]
    public void Overview_SetsProductLevelShellDescription()
    {
        var controller = CreateController(new FakeGlobalExecutionSwitchService());

        var result = controller.Overview();

        Assert.IsType<ViewResult>(result);
        var pageDescription = controller.ViewData["PageDescription"]?.ToString() ?? string.Empty;

        Assert.DoesNotContain("foundation", pageDescription, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("placeholder", pageDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminWorkspaceScreens_LoadRealSnapshots_AndAvoidPlaceholderData()
    {
        var now = new DateTime(2026, 3, 24, 14, 0, 0, DateTimeKind.Utc);
        var workspaceService = new FakeAdminWorkspaceReadModelService
        {
            UsersSnapshot = AdminUsersPageSnapshot.Empty(now),
            UserDetailSnapshot = new AdminUserDetailPageSnapshot(
                "usr-123",
                "Demo User",
                "demo.user@coinbot.local",
                "demo@coinbot.local",
                "SuperAdmin",
                "Aktif",
                "healthy",
                "MFA Açık",
                "healthy",
                "Live",
                "critical",
                "Risk normal",
                "healthy",
                new AdminUserEnvironmentSnapshot("Live", "warning", "Kullanıcı override", "User override resolves to Live.", true),
                new AdminUserRiskOverrideSnapshot("Core", 2m, 10m, 3m, false, false, false, null, null, null, "Risk ve override hazır", "healthy", "Profil 'Core'"),
                [],
                [],
                [],
                [],
                [],
                [],
                now,
                now),
            BotOperationsSnapshot = AdminBotOperationsPageSnapshot.Empty(now),
            StrategyAiMonitoringSnapshot = AdminStrategyAiMonitoringPageSnapshot.Empty(now),
            SupportSnapshot = AdminSupportLookupSnapshot.Empty(now),
            SecurityEventsSnapshot = AdminSecurityEventsPageSnapshot.Empty(now),
            NotificationsSnapshot = AdminNotificationsPageSnapshot.Empty(now)
        };
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            workspaceReadModelService: workspaceService);

        Assert.IsType<ViewResult>(await controller.Users("demo", "Aktif", "MFA Açık", CancellationToken.None));
        Assert.Same(workspaceService.UsersSnapshot, controller.ViewData["AdminUsersPageSnapshot"]);

        Assert.IsType<ViewResult>(await controller.UserDetail("usr-123", CancellationToken.None));
        Assert.Same(workspaceService.UserDetailSnapshot, controller.ViewData["AdminUserDetailPageSnapshot"]);
        Assert.Equal("usr-123", controller.ViewData["AdminEntityId"]);
        Assert.Equal("Demo User", controller.ViewData["AdminEntityLabel"]);

        Assert.IsType<ViewResult>(await controller.BotOperations("bot", "Aktif", "Live", CancellationToken.None));
        Assert.Same(workspaceService.BotOperationsSnapshot, controller.ViewData["AdminBotOperationsPageSnapshot"]);

        Assert.IsType<ViewResult>(await controller.StrategyAiMonitoring("momentum", CancellationToken.None));
        Assert.Same(workspaceService.StrategyAiMonitoringSnapshot, controller.ViewData["AdminStrategyAiMonitoringPageSnapshot"]);

        Assert.IsType<ViewResult>(await controller.SupportTools("usr-123", CancellationToken.None));
        Assert.Same(workspaceService.SupportSnapshot, controller.ViewData["AdminSupportLookupPageSnapshot"]);

        Assert.IsType<ViewResult>(await controller.SecurityEvents("failed", "Critical", "Auth", CancellationToken.None));
        Assert.Same(workspaceService.SecurityEventsSnapshot, controller.ViewData["AdminSecurityEventsPageSnapshot"]);

        Assert.IsType<ViewResult>(await controller.Notifications("warning", "Incident", CancellationToken.None));
        Assert.Same(workspaceService.NotificationsSnapshot, controller.ViewData["AdminNotificationsPageSnapshot"]);
    }

    [Fact]
    public async Task StrategyTemplates_LoadsCatalogAndSelectedArchivedTemplate()
    {
        var templateService = new FakeStrategyTemplateCatalogService();
        templateService.Templates.Add(CreateStrategyTemplateSnapshot("built-in-template", "Built In", isBuiltIn: true));
        templateService.Templates.Add(CreateStrategyTemplateSnapshot("archived-template", "Archived Template", isActive: false, archivedAtUtc: new DateTime(2026, 4, 8, 10, 0, 0, DateTimeKind.Utc)));
        templateService.RevisionsByTemplateKey["archived-template"] =
        [
            new StrategyTemplateRevisionSnapshot(Guid.NewGuid(), Guid.NewGuid(), "archived-template", 2, 2, "Valid", "Archived revision", IsActive: false, IsLatest: true, IsArchived: true, SourceTemplateKey: "archived-template", SourceRevisionNumber: 1, ArchivedAtUtc: new DateTime(2026, 4, 8, 10, 0, 0, DateTimeKind.Utc))
        ];
        var controller = CreateController(new FakeGlobalExecutionSwitchService(), strategyTemplateCatalogService: templateService);

        var result = await controller.StrategyTemplates("archived-template", CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<AdminStrategyTemplateCatalogPageViewModel>(view.Model);
        Assert.Equal("archived-template", model.SelectedTemplateKey);
        Assert.NotNull(model.SelectedTemplate);
        Assert.False(model.SelectedTemplate!.IsActive);
        Assert.Single(model.SelectedTemplateRevisions);
    }

    [Fact]
    public async Task CreateStrategyTemplate_CreatesTemplate_AndWritesAudit()
    {
        var templateService = new FakeStrategyTemplateCatalogService();
        var auditLogService = new FakeAdminAuditLogService();
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            auditLogService: auditLogService,
            strategyTemplateCatalogService: templateService,
            userId: "super-admin",
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.CreateStrategyTemplate(
            "custom-template",
            "Custom Template",
            "Catalog create test.",
            "Momentum",
            "{\"schemaVersion\":2,\"entry\":{\"path\":\"indicator.rsi.value\",\"comparison\":\"lessThanOrEqual\",\"value\":30,\"ruleId\":\"entry-rsi\",\"ruleType\":\"rsi\",\"weight\":10,\"enabled\":true}}",
            "Create reason",
            CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        var call = Assert.Single(templateService.CreateCalls);
        var audit = Assert.Single(auditLogService.Requests);

        Assert.Equal(nameof(AdminController.StrategyTemplates), redirect.ActionName);
        Assert.Equal("custom-template", call.TemplateKey);
        Assert.Equal("Admin.StrategyTemplates.Create", audit.ActionType);
        Assert.Equal("Create reason", audit.Reason);
        Assert.Equal("Strategy template 'Custom Template' revision 1 olarak olusturuldu.", controller.TempData["AdminStrategyTemplateSuccess"]);
    }

    [Fact]
    public async Task ReviseStrategyTemplate_CreatesNewRevision_AndRedirects()
    {
        var templateService = new FakeStrategyTemplateCatalogService();
        templateService.Templates.Add(CreateStrategyTemplateSnapshot("revisioned-template", "Revisioned Template"));
        var auditLogService = new FakeAdminAuditLogService();
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            auditLogService: auditLogService,
            strategyTemplateCatalogService: templateService,
            userId: "super-admin",
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.ReviseStrategyTemplate(
            "revisioned-template",
            "Revisioned Template v2",
            "Updated description",
            "Momentum",
            "{\"schemaVersion\":2,\"entry\":{\"path\":\"indicator.rsi.value\",\"comparison\":\"lessThanOrEqual\",\"value\":28,\"ruleId\":\"entry-rsi\",\"ruleType\":\"rsi\",\"weight\":10,\"enabled\":true}}",
            "Revise reason",
            CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        var call = Assert.Single(templateService.ReviseCalls);
        var audit = Assert.Single(auditLogService.Requests);

        Assert.Equal(nameof(AdminController.StrategyTemplates), redirect.ActionName);
        Assert.Equal("revisioned-template", call.TemplateKey);
        Assert.Equal("Admin.StrategyTemplates.Revise", audit.ActionType);
        Assert.Equal("Revise reason", audit.Reason);
        Assert.Equal("Strategy template 'Revisioned Template v2' revision 2 olarak guncellendi.", controller.TempData["AdminStrategyTemplateSuccess"]);
    }

    [Fact]
    public async Task PublishStrategyTemplate_PublishesSelectedRevision_AndRedirects()
    {
        var templateService = new FakeStrategyTemplateCatalogService();
        templateService.Templates.Add(CreateStrategyTemplateSnapshot("publishable-template", "Publishable Template", latestRevisionNumber: 2, publishedRevisionNumber: 1));
        templateService.RevisionsByTemplateKey["publishable-template"] =
        [
            new StrategyTemplateRevisionSnapshot(Guid.NewGuid(), Guid.NewGuid(), "publishable-template", 2, 2, "Valid", "Draft revision", IsActive: true, IsLatest: true, IsArchived: false, SourceTemplateKey: "publishable-template", SourceRevisionNumber: 1, IsPublished: false),
            new StrategyTemplateRevisionSnapshot(Guid.NewGuid(), Guid.NewGuid(), "publishable-template", 1, 2, "Valid", "Published revision", IsActive: false, IsLatest: false, IsArchived: false, IsPublished: true)
        ];
        var auditLogService = new FakeAdminAuditLogService();
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            auditLogService: auditLogService,
            strategyTemplateCatalogService: templateService,
            userId: "super-admin",
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.PublishStrategyTemplate("publishable-template", 2, "Publish reason", CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        var call = Assert.Single(templateService.PublishCalls);
        var audit = Assert.Single(auditLogService.Requests);

        Assert.Equal(nameof(AdminController.StrategyTemplates), redirect.ActionName);
        Assert.Equal(("publishable-template", 2), call);
        Assert.Equal("Admin.StrategyTemplates.Publish", audit.ActionType);
        Assert.Equal("Publish reason", audit.Reason);
        Assert.Equal("Strategy template 'Publishable Template' revision 2 olarak publish edildi.", controller.TempData["AdminStrategyTemplateSuccess"]);
    }

    [Fact]
    public async Task ArchiveStrategyTemplate_SetsArchiveState_AndRedirects()
    {
        var templateService = new FakeStrategyTemplateCatalogService();
        templateService.Templates.Add(CreateStrategyTemplateSnapshot("archivable-template", "Archivable Template"));
        var auditLogService = new FakeAdminAuditLogService();
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            auditLogService: auditLogService,
            strategyTemplateCatalogService: templateService,
            userId: "super-admin",
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.ArchiveStrategyTemplate("archivable-template", "Archive reason", CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        var call = Assert.Single(templateService.ArchiveCalls);
        var audit = Assert.Single(auditLogService.Requests);

        Assert.Equal(nameof(AdminController.StrategyTemplates), redirect.ActionName);
        Assert.Equal("archivable-template", call);
        Assert.Equal("Admin.StrategyTemplates.Archive", audit.ActionType);
        Assert.Equal("Archive reason", audit.Reason);
        Assert.Equal("Strategy template 'Archivable Template' archive olarak isaretlendi.", controller.TempData["AdminStrategyTemplateSuccess"]);
    }

    [Fact]
    public async Task CreateStrategyTemplate_UsesStableFailureCode_AndWritesBlockedAudit_WhenCatalogRejectsDuplicateKey()
    {
        var templateService = new FakeStrategyTemplateCatalogService
        {
            CreateException = new StrategyTemplateCatalogException("TemplateKeyAlreadyExists", "Strategy template 'custom-template' already exists.")
        };
        var auditLogService = new FakeAdminAuditLogService();
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            auditLogService: auditLogService,
            strategyTemplateCatalogService: templateService,
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.CreateStrategyTemplate(
            "custom-template",
            "Custom Template",
            "Catalog create test.",
            "Momentum",
            "{\"schemaVersion\":2}",
            "Create reason",
            CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        var audit = Assert.Single(auditLogService.Requests);
        Assert.Equal("Admin.StrategyTemplates.CreateBlocked", audit.ActionType);
        Assert.Equal("custom-template", audit.TargetId);
        Assert.Contains("FailureCode=TemplateKeyAlreadyExists", audit.NewValueSummary, StringComparison.Ordinal);
        Assert.Equal("TemplateKeyAlreadyExists: Strategy template 'custom-template' already exists.", controller.TempData["AdminStrategyTemplateError"]);
    }


    [Fact]
    public async Task CreateStrategyTemplate_WhenAdminMfaIsRequired_FailsClosedWithoutCallingCatalog()
    {
        var templateService = new FakeStrategyTemplateCatalogService();
        var auditLogService = new FakeAdminAuditLogService();
        var criticalUserOperationAuthorizer = new FakeCriticalUserOperationAuthorizer
        {
            Result = new CriticalUserOperationAuthorizationResult(
                false,
                "MfaRequired",
                "Bu islem icin MFA zorunludur.")
        };
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            auditLogService: auditLogService,
            strategyTemplateCatalogService: templateService,
            criticalUserOperationAuthorizer: criticalUserOperationAuthorizer,
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.CreateStrategyTemplate(
            "custom-template",
            "Custom Template",
            "Catalog create test.",
            "Momentum",
            "{\"schemaVersion\":2}",
            "Create reason",
            CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);

        Assert.Equal(nameof(AdminController.StrategyTemplates), redirect.ActionName);
        Assert.Empty(templateService.CreateCalls);
        Assert.Empty(auditLogService.Requests);
        Assert.Equal("Bu islem icin MFA zorunludur.", controller.TempData["AdminStrategyTemplateError"]);
        Assert.Single(criticalUserOperationAuthorizer.Requests);
    }

    [Fact]
    public async Task CreateStrategyTemplate_SanitizesUnexpectedErrorFeedback()
    {
        var templateService = new FakeStrategyTemplateCatalogService
        {
            CreateException = new Exception("raw failure`r`nsecret-like")
        };
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            strategyTemplateCatalogService: templateService,
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.CreateStrategyTemplate(
            "custom-template",
            "Custom Template",
            "Catalog create test.",
            "Momentum",
            "{\"schemaVersion\":2}",
            "Create reason",
            CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Strategy template islemi tamamlanamadi.", controller.TempData["AdminStrategyTemplateError"]);
    }

    [Fact]
    public void StrategyTemplateWriteActions_RequirePlatformAdministration_AndAntiForgery()
    {
        foreach (var methodName in new[] { nameof(AdminController.CreateStrategyTemplate), nameof(AdminController.ReviseStrategyTemplate), nameof(AdminController.PublishStrategyTemplate), nameof(AdminController.ArchiveStrategyTemplate) })
        {
            var method = typeof(AdminController).GetMethods().Single(item => item.Name == methodName);
            var authorize = Assert.Single(method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>());
            Assert.Equal(ApplicationPolicies.PlatformAdministration, authorize.Policy);
            Assert.Single(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true).Cast<ValidateAntiForgeryTokenAttribute>());
        }
    }

    [Fact]
    public void StrategyTemplates_UsesAdminPortalAccessPolicy()
    {
        var method = typeof(AdminController).GetMethod(nameof(AdminController.StrategyTemplates), [typeof(string), typeof(CancellationToken)])!;
        var authorize = Assert.Single(method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>());

        Assert.Equal(ApplicationPolicies.AdminPortalAccess, authorize.Policy);
    }
    [Fact]
    public async Task UserDetail_WhenSnapshotMissing_RendersProductNotFoundState_With404Status()
    {
        var workspaceService = new FakeAdminWorkspaceReadModelService
        {
            UserDetailSnapshot = null
        };
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            workspaceReadModelService: workspaceService);

        var result = await controller.UserDetail("usr-missing", CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, controller.Response.StatusCode);
        Assert.Null(controller.ViewData["AdminUserDetailPageSnapshot"]);
        Assert.Equal("usr-missing", controller.ViewData["AdminEntityId"]);
        Assert.Equal("Kullanıcı usr-missing", controller.ViewData["AdminEntityLabel"]);
        Assert.Null(viewResult.Model);
    }

        [Fact]
    public async Task Settings_LoadsOperationalSnapshots_AndMarksOpsAdminAsReadOnly()
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
        var retentionSnapshot = new LogCenterRetentionSnapshot(
            true,
            DecisionTraceRetentionDays: 45,
            ExecutionTraceRetentionDays: 45,
            AdminAuditLogRetentionDays: 90,
            IncidentRetentionDays: 180,
            ApprovalRetentionDays: 180,
            BatchSize: 250,
            LastRunAtUtc: new DateTime(2026, 3, 24, 9, 30, 0, DateTimeKind.Utc),
            LastRunSummary: "LogCenter.Retention.Completed | DecisionTrace=1; ExecutionTrace=1");
        var switchService = new FakeGlobalExecutionSwitchService { Snapshot = executionSnapshot };
        var stateService = new FakeGlobalSystemStateService { Snapshot = globalSystemStateSnapshot };
        var retentionService = new FakeLogCenterRetentionService { Snapshot = retentionSnapshot };
        var timeSyncService = new FakeBinanceTimeSyncService();
        var driftGuardService = new FakeDataLatencyCircuitBreaker();
        var controller = CreateController(
            switchService,
            stateService,
            logCenterRetentionService: retentionService,
            timeSyncService: timeSyncService,
            dataLatencyCircuitBreaker: driftGuardService,
            pilotOptions: new BotExecutionPilotOptions { MaxPilotOrderNotional = "250" },
            roles: [ApplicationRoles.OpsAdmin]);

        var result = await controller.Settings(CancellationToken.None);

        var clockDrift = Assert.IsType<ClockDriftInfoViewModel>(controller.ViewData["AdminClockDriftSnapshot"]);
        var driftGuard = Assert.IsType<MarketDriftGuardInfoViewModel>(controller.ViewData["AdminDriftGuardSnapshot"]);

        Assert.IsType<ViewResult>(result);
        Assert.Equal(1, switchService.GetSnapshotCalls);
        Assert.Equal(1, stateService.GetSnapshotCalls);
        Assert.Equal(1, retentionService.GetSnapshotCalls);
        Assert.False(Assert.Single(timeSyncService.ForceRefreshCalls));
        Assert.Equal(1, driftGuardService.GetSnapshotCalls);
        Assert.Same(executionSnapshot, controller.ViewData["AdminExecutionSwitchSnapshot"]);
        Assert.Same(globalSystemStateSnapshot, controller.ViewData["AdminGlobalSystemStateSnapshot"]);
        Assert.IsType<GlobalPolicySnapshot>(controller.ViewData["AdminGlobalPolicySnapshot"]);
        Assert.Same(retentionSnapshot, controller.ViewData["AdminLogCenterRetentionSnapshot"]);
        Assert.Equal(false, controller.ViewData["AdminCanEditGlobalPolicy"]);
        Assert.Equal(false, controller.ViewData["AdminCanRefreshClockDrift"]);
        Assert.Equal("250", controller.ViewData["AdminPilotOrderNotionalSummary"]);
        Assert.Equal("healthy", controller.ViewData["AdminPilotOrderNotionalTone"]);
        Assert.Equal("OpsAdmin", controller.ViewData["AdminRoleKey"]);
        Assert.Equal("Synchronized", clockDrift.StatusLabel);
        Assert.Contains("Clock drift block aktif", driftGuard.ReasonLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshClockDrift_ForceRefreshesSync_AndRedirectsForPlatformAdmin()
    {
        var timeSyncService = new FakeBinanceTimeSyncService
        {
            ForcedSnapshot = new BinanceTimeSyncSnapshot(
                new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 4, 2, 10, 0, 0, 600, DateTimeKind.Utc),
                600,
                22,
                new DateTime(2026, 4, 2, 10, 0, 1, DateTimeKind.Utc),
                "Synchronized",
                null)
        };
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            timeSyncService: timeSyncService,
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.RefreshClockDrift(CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Equal([true], timeSyncService.ForceRefreshCalls);
        Assert.Equal("Binance server time sync yenilendi. Son probe drift 600 ms. Market heartbeat drift guard snapshot'i ayri izlenir.", controller.TempData["AdminClockDriftSuccess"]);
    }

    [Fact]
    public void RefreshClockDrift_RequiresPlatformAdministration_AndAntiForgery()
    {
        var authorizeAttribute = Assert.Single(
            typeof(AdminController)
                .GetMethod(nameof(AdminController.RefreshClockDrift), [typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>());
        var antiForgeryAttribute = Assert.Single(
            typeof(AdminController)
                .GetMethod(nameof(AdminController.RefreshClockDrift), [typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true)
                .Cast<ValidateAntiForgeryTokenAttribute>());

        Assert.Equal(ApplicationPolicies.PlatformAdministration, authorizeAttribute.Policy);
        Assert.NotNull(antiForgeryAttribute);
    }

    [Fact]
    public async Task Settings_LoadsActivationControlCenterModel_FailClosedSummaryVisible()
    {
        var switchService = new FakeGlobalExecutionSwitchService
        {
            GetSnapshotException = new InvalidOperationException("switch unavailable")
        };
        var stateService = new FakeGlobalSystemStateService
        {
            GetSnapshotException = new InvalidOperationException("state unavailable")
        };
        var timeSyncService = new FakeBinanceTimeSyncService
        {
            SnapshotException = new InvalidOperationException("time sync unavailable")
        };
        var driftGuardService = new FakeDataLatencyCircuitBreaker
        {
            GetSnapshotException = new InvalidOperationException("drift guard unavailable")
        };
        var controller = CreateController(
            switchService,
            stateService,
            timeSyncService: timeSyncService,
            dataLatencyCircuitBreaker: driftGuardService,
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.Settings(CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<AdminActivationControlCenterViewModel>(viewResult.Model);

        Assert.False(model.IsActivatable);
        Assert.Equal("ActivationStateUnavailable", model.LastDecision.Code);
        Assert.Contains(model.ReadinessChecklist, item => item.ReasonCode == "ServerTimeSyncUnavailable");
        Assert.Contains(model.ReadinessChecklist, item => item.ReasonCode == "DataLatencyGuardUnavailable");
        Assert.Equal("Aktif edilemez", model.StatusLabel);
    }

    [Fact]
    public async Task ActivateSystem_WhenReadinessPass_ArmsTradeMaster_AndWritesAudit()
    {
        var switchService = new FakeGlobalExecutionSwitchService
        {
            Snapshot = new GlobalExecutionSwitchSnapshot(
                TradeMasterSwitchState.Disarmed,
                DemoModeEnabled: true,
                IsPersisted: true)
        };
        var stateService = new FakeGlobalSystemStateService
        {
            Snapshot = new GlobalSystemStateSnapshot(
                GlobalSystemStateKind.Active,
                "SYSTEM_ACTIVE",
                Message: null,
                Source: "AdminPortal.Settings",
                CorrelationId: null,
                IsManualOverride: false,
                ExpiresAtUtc: null,
                UpdatedAtUtc: new DateTime(2026, 4, 8, 10, 0, 0, DateTimeKind.Utc),
                UpdatedByUserId: "super-admin",
                UpdatedFromIp: "ip:masked",
                Version: 3,
                IsPersisted: true)
        };
        var commandRegistry = new FakeAdminCommandRegistry();
        var auditLogService = new FakeAdminAuditLogService();
        var driftGuardService = new FakeDataLatencyCircuitBreaker
        {
            Snapshot = new DegradedModeSnapshot(
                DegradedModeStateCode.Normal,
                DegradedModeReasonCode.None,
                SignalFlowBlocked: false,
                ExecutionFlowBlocked: false,
                LatestDataTimestampAtUtc: new DateTime(2026, 4, 8, 9, 59, 0, DateTimeKind.Utc),
                LatestHeartbeatReceivedAtUtc: new DateTime(2026, 4, 8, 9, 59, 5, DateTimeKind.Utc),
                LatestDataAgeMilliseconds: 500,
                LatestClockDriftMilliseconds: 8,
                LastStateChangedAtUtc: new DateTime(2026, 4, 8, 9, 59, 10, DateTimeKind.Utc),
                IsPersisted: true)
        };
        var controller = CreateController(
            switchService,
            stateService,
            commandRegistry,
            auditLogService,
            timeSyncService: new FakeBinanceTimeSyncService(),
            dataLatencyCircuitBreaker: driftGuardService,
            pilotOptions: new BotExecutionPilotOptions
            {
                PilotActivationEnabled = true,
                MaxPilotOrderNotional = "250"
            },
            userId: "super-admin",
            traceIdentifier: "trace-activate-1",
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.ActivateSystem(
            reason: "Controlled activation",
            commandId: "cmd-activate-001",
            reauthToken: "reauth-hook",
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        var call = Assert.Single(switchService.TradeMasterCalls);
        var completion = Assert.Single(commandRegistry.CompletionRequests);
        var auditLog = auditLogService.Requests.Single(request => request.ActionType == "Admin.Settings.Activation.Activate");

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Equal(TradeMasterSwitchState.Armed, call.TradeMasterState);
        Assert.Equal("admin:super-admin", call.Actor);
        Assert.Equal("trace-activate-1", call.CorrelationId);
        Assert.Equal(AdminCommandStatus.Completed, completion.Status);
        Assert.Equal("Admin.Settings.Activation.Activate", auditLog.ActionType);
        Assert.Contains("Sistem aktive edildi", controller.TempData["AdminExecutionSwitchSuccess"]?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActivateSystem_WhenReadinessUnknown_Blocks_AndWritesAudit()
    {
        var switchService = new FakeGlobalExecutionSwitchService
        {
            Snapshot = new GlobalExecutionSwitchSnapshot(
                TradeMasterSwitchState.Disarmed,
                DemoModeEnabled: true,
                IsPersisted: true)
        };
        var stateService = new FakeGlobalSystemStateService
        {
            GetSnapshotException = new InvalidOperationException("state unavailable")
        };
        var commandRegistry = new FakeAdminCommandRegistry();
        var auditLogService = new FakeAdminAuditLogService();
        var driftGuardService = new FakeDataLatencyCircuitBreaker
        {
            Snapshot = new DegradedModeSnapshot(
                DegradedModeStateCode.Normal,
                DegradedModeReasonCode.None,
                SignalFlowBlocked: false,
                ExecutionFlowBlocked: false,
                LatestDataTimestampAtUtc: new DateTime(2026, 4, 8, 9, 59, 0, DateTimeKind.Utc),
                LatestHeartbeatReceivedAtUtc: new DateTime(2026, 4, 8, 9, 59, 5, DateTimeKind.Utc),
                LatestDataAgeMilliseconds: 500,
                LatestClockDriftMilliseconds: 8,
                LastStateChangedAtUtc: new DateTime(2026, 4, 8, 9, 59, 10, DateTimeKind.Utc),
                IsPersisted: true)
        };
        var controller = CreateController(
            switchService,
            stateService,
            commandRegistry,
            auditLogService,
            timeSyncService: new FakeBinanceTimeSyncService(),
            dataLatencyCircuitBreaker: driftGuardService,
            pilotOptions: new BotExecutionPilotOptions
            {
                PilotActivationEnabled = true,
                MaxPilotOrderNotional = "250"
            },
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.ActivateSystem(
            reason: "Controlled activation",
            commandId: "cmd-activate-002",
            reauthToken: null,
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        var auditLog = auditLogService.Requests.Single(request => request.ActionType == "Admin.Settings.Activation.ActivateBlocked");
        var completion = Assert.Single(commandRegistry.CompletionRequests);

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Empty(switchService.TradeMasterCalls);
        Assert.Equal(AdminCommandStatus.Failed, completion.Status);
        Assert.Equal("Admin.Settings.Activation.ActivateBlocked", auditLog.ActionType);
        Assert.Contains("ActivationStateUnavailable", controller.TempData["AdminExecutionSwitchError"]?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivateSystem_WhenConfirmationIsMissing_FailsClosedWithoutChangingState()
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

        var result = await controller.ActivateSystem(
            reason: "Controlled activation",
            commandId: "cmd-activate-confirm",
            reauthToken: null,
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Empty(switchService.TradeMasterCalls);
        Assert.Null(commandRegistry.LastStartRequest);
        Assert.Equal("CriticalActionConfirmationRequired: Bu kritik islem icin ONAYLA ibaresi zorunludur.", controller.TempData["AdminExecutionSwitchError"]);
    }

    [Fact]
    public async Task ActivateSystem_WhenAdminMfaIsRequired_FailsClosedWithoutChangingState()
    {
        var switchService = new FakeGlobalExecutionSwitchService();
        var stateService = new FakeGlobalSystemStateService();
        var commandRegistry = new FakeAdminCommandRegistry();
        var auditLogService = new FakeAdminAuditLogService();
        var criticalUserOperationAuthorizer = new FakeCriticalUserOperationAuthorizer
        {
            Result = new CriticalUserOperationAuthorizationResult(
                false,
                "MfaRequired",
                "Bu islem icin MFA zorunludur.")
        };
        var controller = CreateController(
            switchService,
            stateService,
            commandRegistry,
            auditLogService,
            criticalUserOperationAuthorizer: criticalUserOperationAuthorizer,
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.ActivateSystem(
            reason: "Controlled activation",
            commandId: "cmd-activate-mfa",
            reauthToken: null,
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Empty(switchService.TradeMasterCalls);
        Assert.Null(commandRegistry.LastStartRequest);
        Assert.Equal("Bu islem icin MFA zorunludur.", controller.TempData["AdminExecutionSwitchError"]);
        Assert.Single(criticalUserOperationAuthorizer.Requests);
    }

    [Fact]
    public async Task DeactivateSystem_DisarmsTradeMaster_AndWritesAudit()
    {
        var switchService = new FakeGlobalExecutionSwitchService
        {
            Snapshot = new GlobalExecutionSwitchSnapshot(
                TradeMasterSwitchState.Armed,
                DemoModeEnabled: true,
                IsPersisted: true)
        };
        var stateService = new FakeGlobalSystemStateService();
        var commandRegistry = new FakeAdminCommandRegistry();
        var auditLogService = new FakeAdminAuditLogService();
        var controller = CreateController(
            switchService,
            stateService,
            commandRegistry,
            auditLogService,
            userId: "super-admin",
            traceIdentifier: "trace-deactivate-1",
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.DeactivateSystem(
            reason: "Controlled shutdown",
            commandId: "cmd-deactivate-001",
            reauthToken: "reauth-hook",
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        var call = Assert.Single(switchService.TradeMasterCalls);
        var completion = Assert.Single(commandRegistry.CompletionRequests);
        var auditLog = auditLogService.Requests.Single(request => request.ActionType == "Admin.Settings.Activation.Deactivate");

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Equal(TradeMasterSwitchState.Disarmed, call.TradeMasterState);
        Assert.Equal("admin:super-admin", call.Actor);
        Assert.Equal("trace-deactivate-1", call.CorrelationId);
        Assert.Equal(AdminCommandStatus.Completed, completion.Status);
        Assert.Equal("Admin.Settings.Activation.Deactivate", auditLog.ActionType);
        Assert.Contains("fail-closed kapatildi", controller.TempData["AdminExecutionSwitchSuccess"]?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActivateSystem_RequiresPlatformAdministration_AndAntiForgery()
    {
        var authorizeAttribute = Assert.Single(
            typeof(AdminController)
                .GetMethod(nameof(AdminController.ActivateSystem), [typeof(string), typeof(string), typeof(string), typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>());
        var antiForgeryAttribute = Assert.Single(
            typeof(AdminController)
                .GetMethod(nameof(AdminController.ActivateSystem), [typeof(string), typeof(string), typeof(string), typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true)
                .Cast<ValidateAntiForgeryTokenAttribute>());

        Assert.Equal(ApplicationPolicies.PlatformAdministration, authorizeAttribute.Policy);
        Assert.NotNull(antiForgeryAttribute);
    }

    [Fact]
    public void DeactivateSystem_RequiresPlatformAdministration_AndAntiForgery()
    {
        var authorizeAttribute = Assert.Single(
            typeof(AdminController)
                .GetMethod(nameof(AdminController.DeactivateSystem), [typeof(string), typeof(string), typeof(string), typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>());
        var antiForgeryAttribute = Assert.Single(
            typeof(AdminController)
                .GetMethod(nameof(AdminController.DeactivateSystem), [typeof(string), typeof(string), typeof(string), typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true)
                .Cast<ValidateAntiForgeryTokenAttribute>());

        Assert.Equal(ApplicationPolicies.PlatformAdministration, authorizeAttribute.Policy);
        Assert.NotNull(antiForgeryAttribute);
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
    public async Task SetTradeMasterState_WhenConfirmationIsMissing_FailsClosedWithoutCallingServices()
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
            reason: "Controlled enablement",
            commandId: "cmd-trade-confirm",
            reauthToken: null,
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Empty(switchService.TradeMasterCalls);
        Assert.Null(commandRegistry.LastStartRequest);
        Assert.Equal("CriticalActionConfirmationRequired: Bu kritik islem icin ONAYLA ibaresi zorunludur.", controller.TempData["AdminExecutionSwitchError"]);
    }

    [Fact]
    public async Task SetTradeMasterState_WhenAdminMfaIsRequired_FailsClosedWithoutCallingServices()
    {
        var switchService = new FakeGlobalExecutionSwitchService();
        var stateService = new FakeGlobalSystemStateService();
        var commandRegistry = new FakeAdminCommandRegistry();
        var auditLogService = new FakeAdminAuditLogService();
        var criticalUserOperationAuthorizer = new FakeCriticalUserOperationAuthorizer
        {
            Result = new CriticalUserOperationAuthorizationResult(
                false,
                "MfaRequired",
                "Bu islem icin MFA zorunludur.")
        };
        var controller = CreateController(
            switchService,
            stateService,
            commandRegistry,
            auditLogService,
            criticalUserOperationAuthorizer: criticalUserOperationAuthorizer,
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.SetTradeMasterState(
            isArmed: true,
            reason: "Controlled enablement",
            commandId: "cmd-trade-mfa",
            reauthToken: null,
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Empty(switchService.TradeMasterCalls);
        Assert.Null(commandRegistry.LastStartRequest);
        Assert.Equal("Bu islem icin MFA zorunludur.", controller.TempData["AdminExecutionSwitchError"]);
        Assert.Single(criticalUserOperationAuthorizer.Requests);
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
    public async Task SetDemoMode_WhenConfirmationIsMissing_FailsClosedWithoutCallingServices()
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

        var result = await controller.SetDemoMode(
            isEnabled: false,
            reason: "Planned live window",
            commandId: "cmd-demo-confirm",
            reauthToken: null,
            liveApprovalReference: "chg-9001",
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Empty(switchService.DemoModeCalls);
        Assert.Null(commandRegistry.LastStartRequest);
        Assert.Equal("CriticalActionConfirmationRequired: Bu kritik islem icin ONAYLA ibaresi zorunludur.", controller.TempData["AdminExecutionSwitchError"]);
    }

    [Fact]
    public async Task SetGlobalSystemState_WhenConfirmationIsMissing_FailsClosedBeforeUpdate()
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

        var result = await controller.SetGlobalSystemState(
            state: GlobalSystemStateKind.Maintenance,
            reason: "Planned maintenance",
            reasonCode: "PLANNED_MAINTENANCE",
            message: "Exchange sync freeze",
            expiresAtUtc: new DateTime(2026, 3, 24, 23, 0, 0, DateTimeKind.Utc),
            commandId: "cmd-gs-confirm",
            reauthToken: null,
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Empty(stateService.SetRequests);
        Assert.Null(commandRegistry.LastStartRequest);
        Assert.Equal("CriticalActionConfirmationRequired: Bu kritik islem icin ONAYLA ibaresi zorunludur.", controller.TempData["AdminGlobalSystemStateError"]);
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
    public async Task Search_WhenQueryMissing_ReturnsRealSearchView()
    {
        var controller = CreateController(new FakeGlobalExecutionSwitchService());

        var result = await controller.Search(null, CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        Assert.Null(viewResult.Model);
        Assert.Equal("Global Search", controller.ViewData["Title"]);
    }

    [Fact]
    public async Task Search_RedirectsToTraceDetail_WhenQueryMatchesCorrelationId()
    {
        var now = new DateTime(2026, 3, 24, 13, 0, 0, DateTimeKind.Utc);
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            traceService: new FakeTraceService
            {
                SearchResults =
                [
                    new AdminTraceListItem(
                        "corr-trace-1",
                        "user-01",
                        "BTCUSDT",
                        "1m",
                        "StrategyVersion:abc",
                        "Persisted",
                        null,
                        "Binance.PrivateRest",
                        1,
                        1,
                        now)
                ]
            });

        var result = await controller.Search("corr-trace-1", CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AdminController.TraceDetail), redirectResult.ActionName);
        Assert.Equal("Admin", redirectResult.ControllerName);
        Assert.Equal("corr-trace-1", redirectResult.RouteValues!["correlationId"]);
        Assert.False(redirectResult.RouteValues!.ContainsKey("decisionId"));
        Assert.False(redirectResult.RouteValues!.ContainsKey("executionAttemptId"));
    }

    [Fact]
    public async Task Search_RedirectsToTraceDetail_WhenQueryMatchesDecisionId()
    {
        var now = new DateTime(2026, 3, 24, 13, 5, 0, DateTimeKind.Utc);
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            traceService: new FakeTraceService
            {
                SearchResults =
                [
                    new AdminTraceListItem(
                        "corr-trace-2",
                        "user-02",
                        "ETHUSDT",
                        "5m",
                        "StrategyVersion:def",
                        "Persisted",
                        null,
                        "Binance.PrivateRest",
                        1,
                        0,
                        now)
                ],
                DetailSnapshot = new AdminTraceDetailSnapshot(
                    "corr-trace-2",
                    [
                        new DecisionTraceSnapshot(
                            Guid.NewGuid(),
                            Guid.NewGuid(),
                            "corr-trace-2",
                            "dec-trace-2",
                            "user-02",
                            "ETHUSDT",
                            "5m",
                            "StrategyVersion:def",
                            "Entry",
                            72,
                            "Persisted",
                            null,
                            12,
                            "{\"decision\":\"persisted\"}",
                            now)
                    ],
                    Array.Empty<ExecutionTraceSnapshot>())
            });

        var result = await controller.Search("dec-trace-2", CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AdminController.TraceDetail), redirectResult.ActionName);
        Assert.Equal("corr-trace-2", redirectResult.RouteValues!["correlationId"]);
        Assert.Equal("dec-trace-2", redirectResult.RouteValues!["decisionId"]);
    }

    [Fact]
    public async Task Search_RedirectsToTraceDetail_WhenQueryMatchesExecutionAttemptId()
    {
        var now = new DateTime(2026, 3, 24, 13, 10, 0, DateTimeKind.Utc);
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            traceService: new FakeTraceService
            {
                SearchResults =
                [
                    new AdminTraceListItem(
                        "corr-trace-3",
                        "user-03",
                        "BNBUSDT",
                        "15m",
                        "StrategyVersion:ghi",
                        "Submitted",
                        null,
                        "Binance.PrivateRest",
                        0,
                        1,
                        now)
                ],
                DetailSnapshot = new AdminTraceDetailSnapshot(
                    "corr-trace-3",
                    Array.Empty<DecisionTraceSnapshot>(),
                    [
                        new ExecutionTraceSnapshot(
                            Guid.NewGuid(),
                            Guid.NewGuid(),
                            "corr-trace-3",
                            "exe-trace-3",
                            "cmd-trace-3",
                            "user-03",
                            "Binance.PrivateRest",
                            "/fapi/v1/order",
                            "{\"request\":\"masked\"}",
                            "{\"response\":\"masked\"}",
                            200,
                            null,
                            34,
                            now)
                    ])
            });

        var result = await controller.Search("exe-trace-3", CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AdminController.TraceDetail), redirectResult.ActionName);
        Assert.Equal("corr-trace-3", redirectResult.RouteValues!["correlationId"]);
        Assert.Equal("exe-trace-3", redirectResult.RouteValues!["executionAttemptId"]);
    }

    [Fact]
    public async Task Search_RedirectsToTraceDetail_WhenQueryMatchesExecutionOrderId()
    {
        var now = new DateTime(2026, 3, 24, 13, 12, 0, DateTimeKind.Utc);
        var executionOrderId = Guid.NewGuid();
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            traceService: new FakeTraceService
            {
                SearchResults =
                [
                    new AdminTraceListItem(
                        "corr-trace-4",
                        "user-04",
                        "SOLUSDT",
                        "1m",
                        "StrategyVersion:jkl",
                        "Submitted",
                        null,
                        "Binance.PrivateRest",
                        0,
                        1,
                        now)
                ],
                DetailSnapshot = new AdminTraceDetailSnapshot(
                    "corr-trace-4",
                    Array.Empty<DecisionTraceSnapshot>(),
                    [
                        new ExecutionTraceSnapshot(
                            Guid.NewGuid(),
                            executionOrderId,
                            "corr-trace-4",
                            "exe-trace-4",
                            "cmd-trace-4",
                            "user-04",
                            "Binance.PrivateRest",
                            "/fapi/v1/order",
                            "{\"request\":\"masked\"}",
                            "{\"response\":\"masked\"}",
                            200,
                            "OK",
                            19,
                            now)
                    ])
            });

        var result = await controller.Search(executionOrderId.ToString(), CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AdminController.TraceDetail), redirectResult.ActionName);
        Assert.Equal("corr-trace-4", redirectResult.RouteValues!["correlationId"]);
        Assert.Equal("exe-trace-4", redirectResult.RouteValues!["executionAttemptId"]);
    }

    [Fact]
    public async Task Search_RedirectsToUserDetail_WhenQueryMatchesUserId_AndOperatorHasIdentityPermission()
    {
        var now = new DateTime(2026, 3, 24, 13, 13, 0, DateTimeKind.Utc);
        var workspaceService = new FakeAdminWorkspaceReadModelService
        {
            UserDetailSnapshot = new AdminUserDetailPageSnapshot(
                "usr-search-1",
                "Search User",
                "search.user",
                "search.user@coinbot.local",
                "Support",
                "Aktif",
                "healthy",
                "MFA Acik",
                "healthy",
                "Demo",
                "info",
                "Risk normal",
                "healthy",
                new AdminUserEnvironmentSnapshot("Demo", "info", "Global varsayilan", "Default demo mode.", false),
                new AdminUserRiskOverrideSnapshot("Core", 2m, 10m, 3m, false, false, false, null, null, null, "Risk ve override hazir", "healthy", "Profil 'Core'"),
                [],
                [],
                [],
                [],
                [],
                [],
                now,
                now)
        };
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            workspaceReadModelService: workspaceService);

        var result = await controller.Search("usr-search-1", CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AdminController.UserDetail), redirectResult.ActionName);
        Assert.Equal("usr-search-1", redirectResult.RouteValues!["id"]);
    }

    [Fact]
    public async Task Search_DoesNotRedirectToUserDetail_WhenOperatorLacksIdentityPermission()
    {
        var now = new DateTime(2026, 3, 24, 13, 14, 0, DateTimeKind.Utc);
        var workspaceService = new FakeAdminWorkspaceReadModelService
        {
            UserDetailSnapshot = new AdminUserDetailPageSnapshot(
                "usr-search-2",
                "Search User",
                "search.user",
                "search.user@coinbot.local",
                "Support",
                "Aktif",
                "healthy",
                "MFA Acik",
                "healthy",
                "Demo",
                "info",
                "Risk normal",
                "healthy",
                new AdminUserEnvironmentSnapshot("Demo", "info", "Global varsayilan", "Default demo mode.", false),
                new AdminUserRiskOverrideSnapshot("Core", 2m, 10m, 3m, false, false, false, null, null, null, "Risk ve override hazir", "healthy", "Profil 'Core'"),
                [],
                [],
                [],
                [],
                [],
                [],
                now,
                now)
        };
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            workspaceReadModelService: workspaceService,
            roles: [ApplicationRoles.SecurityAuditor]);

        var result = await controller.Search("usr-search-2", CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AdminController.Audit), redirectResult.ActionName);
        Assert.Equal("usr-search-2", redirectResult.RouteValues!["query"]);
    }

    [Fact]
    public async Task Search_RedirectsToIncidentDetail_WhenQueryMatchesIncidentReference()
    {
        var now = new DateTime(2026, 3, 24, 13, 15, 0, DateTimeKind.Utc);
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            governanceReadModelService: new FakeAdminGovernanceReadModelService
            {
                IncidentDetail = CreateIncidentDetailSnapshot(now)
            },
            traceService: new FakeTraceService());

        var result = await controller.Search("INC-9001", CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AdminController.IncidentDetail), redirectResult.ActionName);
        Assert.Equal("Admin", redirectResult.ControllerName);
        Assert.Equal("INC-9001", redirectResult.RouteValues!["incidentReference"]);
    }

    [Fact]
    public async Task Search_FallsBackToAudit_WhenNoTraceIdentifierMatches()
    {
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            traceService: new FakeTraceService());

        var result = await controller.Search("unmatched-query", CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AdminController.Audit), redirectResult.ActionName);
        Assert.Equal("unmatched-query", redirectResult.RouteValues!["query"]);
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
    public async Task UpdateSymbolRestrictions_UsesStructuredWritePath_AndPreservesUnchangedMetadata()
    {
        var now = new DateTime(2026, 3, 24, 12, 30, 0, DateTimeKind.Utc);
        var policy = new RiskPolicySnapshot(
            "GlobalRiskPolicy",
            new ExecutionGuardPolicy(250_000m, 500_000m, 20, CloseOnlyBlocksNewPositions: true),
            new AutonomyPolicy(AutonomyPolicyMode.ManualApprovalRequired, RequireManualApprovalForLive: true),
            [
                new SymbolRestriction("BTCUSDT", SymbolRestrictionState.CloseOnly, "manual review", now, "ops-admin"),
                new SymbolRestriction("ETHUSDT", SymbolRestrictionState.ReviewOnly, "watch closely", now.AddMinutes(-20), "ops-admin")
            ]);
        var policySnapshot = new GlobalPolicySnapshot(
            policy,
            CurrentVersion: 8,
            LastUpdatedAtUtc: now,
            LastUpdatedByUserId: "ops-admin",
            LastChangeSummary: "Desk controls",
            IsPersisted: true,
            Versions: Array.Empty<GlobalPolicyVersionSnapshot>());
        var policyEngine = new FakeGlobalPolicyEngine(policySnapshot);
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            globalPolicyEngine: policyEngine,
            userId: "super-admin",
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.UpdateSymbolRestrictions(
            [
                new AdminSymbolRestrictionInputModel
                {
                    Symbol = "BTCUSDT",
                    State = nameof(SymbolRestrictionState.CloseOnly),
                    Reason = "manual review"
                },
                new AdminSymbolRestrictionInputModel
                {
                    Symbol = "ETHUSDT",
                    State = nameof(SymbolRestrictionState.Blocked),
                    Reason = "exchange halt"
                }
            ],
            "Desk override",
            "cmd-restriction-001",
            reauthToken: null,
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        var updateRequest = Assert.Single(policyEngine.UpdateRequests);
        var persistedRestrictions = updateRequest.Policy.SymbolRestrictions.OrderBy(item => item.Symbol, StringComparer.Ordinal).ToArray();
        var preservedRestriction = Assert.Single(persistedRestrictions, item => item.Symbol == "BTCUSDT");
        var changedRestriction = Assert.Single(persistedRestrictions, item => item.Symbol == "ETHUSDT");

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Equal("AdminPortal.Settings.SymbolRestrictions", updateRequest.Source);
        Assert.Equal(policy.ExecutionGuardPolicy, updateRequest.Policy.ExecutionGuardPolicy);
        Assert.Equal(policy.AutonomyPolicy, updateRequest.Policy.AutonomyPolicy);
        Assert.Equal(now, preservedRestriction.UpdatedAtUtc);
        Assert.Equal("ops-admin", preservedRestriction.UpdatedByUserId);
        Assert.Equal(SymbolRestrictionState.Blocked, changedRestriction.State);
        Assert.Equal("exchange halt", changedRestriction.Reason);
        Assert.Equal("super-admin", changedRestriction.UpdatedByUserId);
        Assert.NotNull(changedRestriction.UpdatedAtUtc);
        Assert.Contains("updated", controller.TempData["AdminGlobalPolicySuccess"]?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateSymbolRestrictions_ReadOnlyRoleIsRejected()
    {
        var policyEngine = new FakeGlobalPolicyEngine(CreatePolicySnapshot(new DateTime(2026, 3, 24, 12, 30, 0, DateTimeKind.Utc)));
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            globalPolicyEngine: policyEngine,
            roles: [ApplicationRoles.OpsAdmin]);

        var result = await controller.UpdateSymbolRestrictions(
            [
                new AdminSymbolRestrictionInputModel
                {
                    Symbol = "BTCUSDT",
                    State = nameof(SymbolRestrictionState.Blocked),
                    Reason = "blocked"
                }
            ],
            "Desk override",
            "cmd-restriction-002",
            reauthToken: null,
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Empty(policyEngine.UpdateRequests);
        Assert.Equal("Bu rolde symbol restriction degistirilemez.", controller.TempData["AdminGlobalPolicyError"]);
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
    public async Task ExecuteCrisisEscalation_WhenPreviewStampIsMissing_FailsClosedWithoutExecuting()
    {
        var commandRegistry = new FakeAdminCommandRegistry();
        var auditLogService = new FakeAdminAuditLogService();
        var crisisService = new FakeCrisisEscalationService();
        var controller = CreateController(
            new FakeGlobalExecutionSwitchService(),
            commandRegistry: commandRegistry,
            auditLogService: auditLogService,
            crisisEscalationService: crisisService,
            roles: [ApplicationRoles.SuperAdmin]);

        var result = await controller.ExecuteCrisisEscalation(
            CrisisEscalationLevel.SoftHalt,
            "GLOBAL_SOFT_HALT",
            "CRISIS_SOFT_HALT",
            "Operator note",
            "Market integrity protection",
            previewStamp: null,
            commandId: "cmd-crisis-missing-preview",
            reauthToken: null,
            secondApprovalReference: null,
            cancellationToken: CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);

        Assert.Equal(nameof(AdminController.Settings), redirectResult.ActionName);
        Assert.Empty(crisisService.ExecuteRequests);
        Assert.Null(commandRegistry.LastStartRequest);
        Assert.Equal("Impact preview zorunludur.", controller.TempData["AdminCrisisEscalationError"]);
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
                        ClockDriftMs: null,
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

    private static StrategyTemplateSnapshot CreateStrategyTemplateSnapshot(
        string templateKey,
        string templateName,
        bool isBuiltIn = false,
        bool isActive = true,
        DateTime? archivedAtUtc = null,
        int latestRevisionNumber = 1,
        int? publishedRevisionNumber = null)
    {
        return new StrategyTemplateSnapshot(
            templateKey,
            templateName,
            $"{templateName} description",
            "Momentum",
            2,
            "{\"schemaVersion\":2}",
            new StrategyDefinitionValidationSnapshot(true, "Valid", "Validated", Array.Empty<string>(), 1),
            IsBuiltIn: isBuiltIn,
            IsActive: isActive,
            TemplateSource: isBuiltIn ? "BuiltIn" : "Custom",
            ActiveRevisionNumber: isActive ? latestRevisionNumber : 0,
            LatestRevisionNumber: latestRevisionNumber,
            PublishedRevisionNumber: publishedRevisionNumber ?? latestRevisionNumber,
            TemplateId: Guid.NewGuid(),
            ActiveRevisionId: isActive ? Guid.NewGuid() : null,
            LatestRevisionId: Guid.NewGuid(),
            PublishedRevisionId: Guid.NewGuid(),
            ArchivedAtUtc: archivedAtUtc,
            CreatedAtUtc: new DateTime(2026, 4, 8, 8, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc: new DateTime(2026, 4, 8, 9, 0, 0, DateTimeKind.Utc));
    }
    private static AdminController CreateController(
        FakeGlobalExecutionSwitchService switchService,
        FakeGlobalSystemStateService? stateService = null,
        FakeAdminCommandRegistry? commandRegistry = null,
        FakeAdminAuditLogService? auditLogService = null,
        FakeTraceService? traceService = null,
        FakeApiCredentialValidationService? apiCredentialValidationService = null,
        FakeAdminWorkspaceReadModelService? workspaceReadModelService = null,
        FakeStrategyTemplateCatalogService? strategyTemplateCatalogService = null,
        FakeCriticalUserOperationAuthorizer? criticalUserOperationAuthorizer = null,
        FakeApprovalWorkflowService? approvalWorkflowService = null,
        FakeAdminGovernanceReadModelService? governanceReadModelService = null,
        FakeAdminMonitoringReadModelService? monitoringReadModelService = null,
        FakeLogCenterRetentionService? logCenterRetentionService = null,
        FakeBinanceTimeSyncService? timeSyncService = null,
        FakeDataLatencyCircuitBreaker? dataLatencyCircuitBreaker = null,
        FakeGlobalPolicyEngine? globalPolicyEngine = null,
        FakeCrisisEscalationService? crisisEscalationService = null,
        BotExecutionPilotOptions? pilotOptions = null,
        string userId = "admin-01",
        string traceIdentifier = "trace-001",
        string[]? roles = null,
        string[]? permissions = null,
        bool isAuthenticated = true)
    {
        roles ??= [ApplicationRoles.SuperAdmin];
        permissions ??= roles
            .SelectMany(ApplicationRoleClaims.GetClaims)
            .Where(claim => claim.Type == ApplicationClaimTypes.Permission)
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var claims = new List<Claim>();

        if (isAuthenticated)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
            claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
            claims.AddRange(permissions.Select(permission => new Claim(ApplicationClaimTypes.Permission, permission)));
        }

        var httpContext = new DefaultHttpContext
        {
            TraceIdentifier = traceIdentifier,
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, isAuthenticated ? "TestAuth" : null))
        };

        return new AdminController(
            globalExecutionSwitchService: switchService,
            globalSystemStateService: stateService ?? new FakeGlobalSystemStateService(),
            adminCommandRegistry: commandRegistry ?? new FakeAdminCommandRegistry(),
            adminAuditLogService: auditLogService ?? new FakeAdminAuditLogService(),
            traceService: traceService ?? new FakeTraceService(),
            binanceTimeSyncService: timeSyncService ?? new FakeBinanceTimeSyncService(),
            dataLatencyCircuitBreaker: dataLatencyCircuitBreaker ?? new FakeDataLatencyCircuitBreaker(),
            apiCredentialValidationService: apiCredentialValidationService ?? new FakeApiCredentialValidationService(),
            adminWorkspaceReadModelService: workspaceReadModelService ?? new FakeAdminWorkspaceReadModelService(),
            strategyTemplateCatalogService: strategyTemplateCatalogService ?? new FakeStrategyTemplateCatalogService(),
            criticalUserOperationAuthorizer: criticalUserOperationAuthorizer ?? new FakeCriticalUserOperationAuthorizer(),
            approvalWorkflowService: approvalWorkflowService,
            adminGovernanceReadModelService: governanceReadModelService,
            adminMonitoringReadModelService: monitoringReadModelService ?? new FakeAdminMonitoringReadModelService(),
            logCenterRetentionService: logCenterRetentionService ?? new FakeLogCenterRetentionService(),
            globalPolicyEngine: globalPolicyEngine ?? new FakeGlobalPolicyEngine(),
            crisisEscalationService: crisisEscalationService,
            botExecutionPilotOptions: Options.Create(pilotOptions ?? new BotExecutionPilotOptions()),
            dataLatencyGuardOptions: Options.Create(new DataLatencyGuardOptions
            {
                ClockDriftThresholdSeconds = 2,
                StaleDataThresholdSeconds = 3,
                StopDataThresholdSeconds = 6
            }),
            privateDataOptions: Options.Create(new BinancePrivateDataOptions
            {
                ServerTimeSyncRefreshSeconds = 30
            }))
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

        public Exception? GetSnapshotException { get; set; }

        public Exception? SetTradeMasterException { get; set; }

        public Exception? SetDemoModeException { get; set; }

        public List<TradeMasterCall> TradeMasterCalls { get; } = [];

        public List<DemoModeCall> DemoModeCalls { get; } = [];

        public int GetSnapshotCalls { get; private set; }

        public Task<GlobalExecutionSwitchSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            GetSnapshotCalls++;
            if (GetSnapshotException is not null)
            {
                throw GetSnapshotException;
            }

            return Task.FromResult(Snapshot);
        }

        public Task<GlobalExecutionSwitchSnapshot> SetTradeMasterStateAsync(
            TradeMasterSwitchState tradeMasterState,
            string actor,
            string? context = null,
            string? correlationId = null,
            CancellationToken cancellationToken = default)
        {
            if (SetTradeMasterException is not null)
            {
                throw SetTradeMasterException;
            }

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
            if (SetDemoModeException is not null)
            {
                throw SetDemoModeException;
            }

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

        public Exception? GetSnapshotException { get; set; }

        public int GetSnapshotCalls { get; private set; }

        public List<GlobalSystemStateSetRequest> SetRequests { get; } = [];

        public Task<GlobalSystemStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            GetSnapshotCalls++;
            if (GetSnapshotException is not null)
            {
                throw GetSnapshotException;
            }

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

    private sealed class FakeAdminWorkspaceReadModelService : IAdminWorkspaceReadModelService
    {
        public AdminUsersPageSnapshot UsersSnapshot { get; set; } = AdminUsersPageSnapshot.Empty(DateTime.UtcNow);

        public AdminUserDetailPageSnapshot? UserDetailSnapshot { get; set; }

        public AdminBotOperationsPageSnapshot BotOperationsSnapshot { get; set; } = AdminBotOperationsPageSnapshot.Empty(DateTime.UtcNow);

        public AdminStrategyAiMonitoringPageSnapshot StrategyAiMonitoringSnapshot { get; set; } = AdminStrategyAiMonitoringPageSnapshot.Empty(DateTime.UtcNow);

        public AdminSupportLookupSnapshot SupportSnapshot { get; set; } = AdminSupportLookupSnapshot.Empty(DateTime.UtcNow);

        public AdminSecurityEventsPageSnapshot SecurityEventsSnapshot { get; set; } = AdminSecurityEventsPageSnapshot.Empty(DateTime.UtcNow);

        public AdminNotificationsPageSnapshot NotificationsSnapshot { get; set; } = AdminNotificationsPageSnapshot.Empty(DateTime.UtcNow);

        public Task<AdminUsersPageSnapshot> GetUsersAsync(string? query = null, string? status = null, string? mfa = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(UsersSnapshot);
        }

        public Task<AdminUserDetailPageSnapshot?> GetUserDetailAsync(string userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(UserDetailSnapshot);
        }

        public Task<AdminBotOperationsPageSnapshot> GetBotOperationsAsync(string? query = null, string? status = null, string? mode = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(BotOperationsSnapshot);
        }

        public Task<AdminStrategyAiMonitoringPageSnapshot> GetStrategyAiMonitoringAsync(string? query = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(StrategyAiMonitoringSnapshot);
        }

        public Task<AdminSupportLookupSnapshot> GetSupportLookupAsync(string? query = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SupportSnapshot);
        }

        public Task<AdminSecurityEventsPageSnapshot> GetSecurityEventsAsync(string? query = null, string? severity = null, string? module = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SecurityEventsSnapshot);
        }

        public Task<AdminNotificationsPageSnapshot> GetNotificationsAsync(string? severity = null, string? category = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(NotificationsSnapshot);
        }
    }

    private sealed class FakeStrategyTemplateCatalogService : IStrategyTemplateCatalogService
    {
        public List<StrategyTemplateSnapshot> Templates { get; } = [];
        public Dictionary<string, IReadOnlyCollection<StrategyTemplateRevisionSnapshot>> RevisionsByTemplateKey { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string OwnerUserId, string TemplateKey, string TemplateName, string Description, string Category, string DefinitionJson)> CreateCalls { get; } = [];
        public List<(string TemplateKey, string TemplateName, string Description, string Category, string DefinitionJson)> ReviseCalls { get; } = [];
        public List<(string TemplateKey, int RevisionNumber)> PublishCalls { get; } = [];
        public List<string> ArchiveCalls { get; } = [];
        public Exception? CreateException { get; set; }
        public Exception? ReviseException { get; set; }
        public Exception? PublishException { get; set; }
        public Exception? ArchiveException { get; set; }

        public Task<IReadOnlyCollection<StrategyTemplateSnapshot>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyCollection<StrategyTemplateSnapshot>>(
                Templates.Where(template => template.IsActive && template.PublishedRevisionNumber > 0).ToArray());
        }

        public Task<IReadOnlyCollection<StrategyTemplateSnapshot>> ListAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyCollection<StrategyTemplateSnapshot>>(Templates.ToArray());
        }

        public Task<StrategyTemplateSnapshot> GetAsync(string templateKey, CancellationToken cancellationToken = default)
        {
            var template = Templates.SingleOrDefault(item =>
                    string.Equals(item.TemplateKey, templateKey, StringComparison.OrdinalIgnoreCase) &&
                    item.IsActive &&
                    item.PublishedRevisionNumber > 0)
                ?? throw new StrategyTemplateCatalogException("TemplateNotFound", $"Strategy template '{templateKey}' was not found.");
            return Task.FromResult(template);
        }

        public Task<StrategyTemplateSnapshot> GetIncludingArchivedAsync(string templateKey, CancellationToken cancellationToken = default)
        {
            var template = Templates.SingleOrDefault(item => string.Equals(item.TemplateKey, templateKey, StringComparison.OrdinalIgnoreCase))
                ?? throw new StrategyTemplateCatalogException("TemplateNotFound", $"Strategy template '{templateKey}' was not found.");
            return Task.FromResult(template);
        }

        public Task<StrategyTemplateSnapshot> CreateCustomAsync(string ownerUserId, string templateKey, string templateName, string description, string category, string definitionJson, CancellationToken cancellationToken = default)
        {
            if (CreateException is not null)
            {
                throw CreateException;
            }

            var created = CreateStrategyTemplateSnapshot(templateKey, templateName, publishedRevisionNumber: 1);
            CreateCalls.Add((ownerUserId, templateKey, templateName, description, category, definitionJson));
            Templates.RemoveAll(item => string.Equals(item.TemplateKey, templateKey, StringComparison.OrdinalIgnoreCase));
            Templates.Add(created);
            RevisionsByTemplateKey[templateKey] =
            [
                new StrategyTemplateRevisionSnapshot(Guid.NewGuid(), created.TemplateId, created.TemplateKey, 1, created.SchemaVersion, created.Validation.StatusCode, created.Validation.Summary, IsActive: true, IsLatest: true, IsArchived: false, IsPublished: true)
            ];
            return Task.FromResult(created);
        }

        public Task<StrategyTemplateSnapshot> CloneAsync(string ownerUserId, string sourceTemplateKey, string templateKey, string templateName, string description, string category, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<StrategyTemplateSnapshot> ReviseAsync(string templateKey, string templateName, string description, string category, string definitionJson, CancellationToken cancellationToken = default)
        {
            if (ReviseException is not null)
            {
                throw ReviseException;
            }

            ReviseCalls.Add((templateKey, templateName, description, category, definitionJson));
            var current = Templates.Single(item => string.Equals(item.TemplateKey, templateKey, StringComparison.OrdinalIgnoreCase));
            var revised = current with
            {
                TemplateName = templateName,
                Description = description,
                Category = category,
                ActiveRevisionNumber = current.LatestRevisionNumber + 1,
                LatestRevisionNumber = current.LatestRevisionNumber + 1,
                PublishedRevisionNumber = current.PublishedRevisionNumber,
                UpdatedAtUtc = new DateTime(2026, 4, 8, 10, 0, 0, DateTimeKind.Utc)
            };
            Templates.Remove(current);
            Templates.Add(revised);
            RevisionsByTemplateKey[templateKey] =
            [
                new StrategyTemplateRevisionSnapshot(Guid.NewGuid(), revised.TemplateId, revised.TemplateKey, revised.LatestRevisionNumber, revised.SchemaVersion, revised.Validation.StatusCode, revised.Validation.Summary, IsActive: true, IsLatest: true, IsArchived: false, SourceTemplateKey: templateKey, SourceRevisionNumber: current.ActiveRevisionNumber, IsPublished: false),
                new StrategyTemplateRevisionSnapshot(Guid.NewGuid(), revised.TemplateId, revised.TemplateKey, current.PublishedRevisionNumber, revised.SchemaVersion, revised.Validation.StatusCode, revised.Validation.Summary, IsActive: false, IsLatest: false, IsArchived: false, IsPublished: true)
            ];
            return Task.FromResult(revised);
        }

        public Task<StrategyTemplateSnapshot> PublishAsync(string templateKey, int revisionNumber, CancellationToken cancellationToken = default)
        {
            if (PublishException is not null)
            {
                throw PublishException;
            }

            PublishCalls.Add((templateKey, revisionNumber));
            var current = Templates.Single(item => string.Equals(item.TemplateKey, templateKey, StringComparison.OrdinalIgnoreCase));
            var published = current with
            {
                PublishedRevisionNumber = revisionNumber,
                UpdatedAtUtc = new DateTime(2026, 4, 8, 10, 30, 0, DateTimeKind.Utc)
            };
            Templates.Remove(current);
            Templates.Add(published);

            if (RevisionsByTemplateKey.TryGetValue(templateKey, out var revisions))
            {
                RevisionsByTemplateKey[templateKey] = revisions
                    .Select(revision => revision with { IsPublished = revision.RevisionNumber == revisionNumber })
                    .ToArray();
            }

            return Task.FromResult(published);
        }

        public Task<IReadOnlyCollection<StrategyTemplateRevisionSnapshot>> ListRevisionsAsync(string templateKey, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(RevisionsByTemplateKey.TryGetValue(templateKey, out var revisions)
                ? revisions
                : (IReadOnlyCollection<StrategyTemplateRevisionSnapshot>)Array.Empty<StrategyTemplateRevisionSnapshot>());
        }

        public Task<StrategyTemplateSnapshot> ArchiveAsync(string templateKey, CancellationToken cancellationToken = default)
        {
            if (ArchiveException is not null)
            {
                throw ArchiveException;
            }

            ArchiveCalls.Add(templateKey);
            var current = Templates.Single(item => string.Equals(item.TemplateKey, templateKey, StringComparison.OrdinalIgnoreCase));
            var archivedAtUtc = new DateTime(2026, 4, 8, 11, 0, 0, DateTimeKind.Utc);
            var archived = current with
            {
                IsActive = false,
                ActiveRevisionNumber = 0,
                ArchivedAtUtc = archivedAtUtc,
                UpdatedAtUtc = archivedAtUtc
            };
            Templates.Remove(current);
            Templates.Add(archived);
            return Task.FromResult(archived);
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

    private sealed class FakeCriticalUserOperationAuthorizer : ICriticalUserOperationAuthorizer
    {
        public CriticalUserOperationAuthorizationResult Result { get; set; } =
            new(true, null, null);

        public List<CriticalUserOperationAuthorizationRequest> Requests { get; } = [];

        public Task<CriticalUserOperationAuthorizationResult> AuthorizeAsync(
            CriticalUserOperationAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeLogCenterRetentionService : ILogCenterRetentionService
    {
        public LogCenterRetentionSnapshot Snapshot { get; set; } = new(
            Enabled: true,
            DecisionTraceRetentionDays: 365,
            ExecutionTraceRetentionDays: 365,
            AdminAuditLogRetentionDays: 365,
            IncidentRetentionDays: 730,
            ApprovalRetentionDays: 730,
            BatchSize: 250,
            LastRunAtUtc: null,
            LastRunSummary: null);

        public int GetSnapshotCalls { get; private set; }

        public Task<LogCenterRetentionSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            GetSnapshotCalls++;
            return Task.FromResult(Snapshot);
        }

        public Task<LogCenterRetentionRunSnapshot> ApplyAsync(CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }

    private sealed class FakeBinanceTimeSyncService : IBinanceTimeSyncService
    {
        public List<bool> ForceRefreshCalls { get; } = [];

        public Exception? SnapshotException { get; set; }

        public BinanceTimeSyncSnapshot Snapshot { get; init; } = new(
            new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
            0,
            12,
            new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
            "Synchronized",
            null);

        public BinanceTimeSyncSnapshot? ForcedSnapshot { get; init; }

        public Task<BinanceTimeSyncSnapshot> GetSnapshotAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            ForceRefreshCalls.Add(forceRefresh);
            if (SnapshotException is not null)
            {
                throw SnapshotException;
            }

            return Task.FromResult(forceRefresh && ForcedSnapshot is not null ? ForcedSnapshot : Snapshot);
        }

        public Task<long> GetCurrentTimestampMillisecondsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(1_710_000_000_000L);
        }
    }

    private sealed class FakeDataLatencyCircuitBreaker : IDataLatencyCircuitBreaker
    {
        public DegradedModeSnapshot Snapshot { get; set; } = new(
            DegradedModeStateCode.Stopped,
            DegradedModeReasonCode.ClockDriftExceeded,
            SignalFlowBlocked: true,
            ExecutionFlowBlocked: true,
            LatestDataTimestampAtUtc: new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
            LatestHeartbeatReceivedAtUtc: new DateTime(2026, 4, 2, 10, 0, 2, DateTimeKind.Utc),
            LatestDataAgeMilliseconds: 2200,
            LatestClockDriftMilliseconds: 2234,
            LastStateChangedAtUtc: new DateTime(2026, 4, 2, 10, 0, 3, DateTimeKind.Utc),
            IsPersisted: true);

        public Exception? GetSnapshotException { get; set; }

        public int GetSnapshotCalls { get; private set; }

        public Task<DegradedModeSnapshot> GetSnapshotAsync(string? correlationId = null, string? symbol = null, string? timeframe = null, CancellationToken cancellationToken = default)
        {
            GetSnapshotCalls++;
            if (GetSnapshotException is not null)
            {
                throw GetSnapshotException;
            }

            return Task.FromResult(Snapshot);
        }

        public Task<DegradedModeSnapshot> RecordHeartbeatAsync(DataLatencyHeartbeat heartbeat, string? correlationId = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
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
        public List<GlobalPolicyUpdateRequest> UpdateRequests { get; } = [];
        public List<GlobalPolicyRollbackRequest> RollbackRequests { get; } = [];

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
            UpdateRequests.Add(request);
            Snapshot = Snapshot with { Policy = request.Policy, CurrentVersion = Snapshot.CurrentVersion + 1, LastUpdatedAtUtc = DateTime.UtcNow, LastUpdatedByUserId = request.ActorUserId, LastChangeSummary = request.Reason, IsPersisted = true };
            return Task.FromResult(Snapshot);
        }

        public Task<GlobalPolicySnapshot> RollbackAsync(GlobalPolicyRollbackRequest request, CancellationToken cancellationToken = default)
        {
            RollbackRequests.Add(request);
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





















