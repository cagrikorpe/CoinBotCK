using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Contracts.Common;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Mfa;
using CoinBot.Web.StrategyBuilderSupport;
using CoinBot.Web.ViewModels.Admin;
using CoinBot.Web.ViewModels.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Web.Areas.Admin.Controllers;

[Area("Admin")]
[Authorize(Policy = ApplicationPolicies.AdminPortalAccess)]
public sealed class AdminController : Controller
{
    private const string AdminCanEditGlobalPolicyViewDataKey = "AdminCanEditGlobalPolicy";
    private const string ExecutionSwitchSnapshotViewDataKey = "AdminExecutionSwitchSnapshot";
    private const string GlobalSystemStateSnapshotViewDataKey = "AdminGlobalSystemStateSnapshot";
    private const string ExecutionSwitchSuccessTempDataKey = "AdminExecutionSwitchSuccess";
    private const string ExecutionSwitchErrorTempDataKey = "AdminExecutionSwitchError";
    private const string GlobalSystemStateSuccessTempDataKey = "AdminGlobalSystemStateSuccess";
    private const string GlobalSystemStateErrorTempDataKey = "AdminGlobalSystemStateError";
    private const string MonitoringDashboardSnapshotViewDataKey = "AdminMonitoringDashboardSnapshot";
    private const string PilotOrderNotionalSummaryViewDataKey = "AdminPilotOrderNotionalSummary";
    private const string PilotOrderNotionalToneViewDataKey = "AdminPilotOrderNotionalTone";
    private const string LongRegimePolicyStatusViewDataKey = "AdminLongRegimePolicyStatus";
    private const string LongRegimePolicyToneViewDataKey = "AdminLongRegimePolicyTone";
    private const string LongRegimePolicyDetailViewDataKey = "AdminLongRegimePolicyDetail";
    private const string GlobalPolicySnapshotViewDataKey = "AdminGlobalPolicySnapshot";
    private const string GlobalPolicySuccessTempDataKey = "AdminGlobalPolicySuccess";
    private const string GlobalPolicyErrorTempDataKey = "AdminGlobalPolicyError";
    private const string AdminLogCenterRetentionSnapshotViewDataKey = "AdminLogCenterRetentionSnapshot";
    private const string RolloutClosureCenterViewDataKey = "AdminRolloutClosureCenter";
    private const string ClockDriftSnapshotViewDataKey = "AdminClockDriftSnapshot";
    private const string DriftGuardSnapshotViewDataKey = "AdminDriftGuardSnapshot";
    private const string CanRefreshClockDriftViewDataKey = "AdminCanRefreshClockDrift";
    private const string ClockDriftSuccessTempDataKey = "AdminClockDriftSuccess";
    private const string ClockDriftErrorTempDataKey = "AdminClockDriftError";
    private const string SetupSuccessTempDataKey = "AdminSetupSuccess";
    private const string SetupErrorTempDataKey = "AdminSetupError";
    private const string ApprovalSuccessTempDataKey = "AdminApprovalSuccess";
    private const string ApprovalErrorTempDataKey = "AdminApprovalError";
    private const string AdminUserSuccessTempDataKey = "AdminUserSuccess";
    private const string AdminUserErrorTempDataKey = "AdminUserError";
    private const string CrisisPreviewViewDataKey = "AdminCrisisEscalationPreview";
    private const string CrisisSuccessTempDataKey = "AdminCrisisEscalationSuccess";
    private const string CrisisErrorTempDataKey = "AdminCrisisEscalationError";
    private const string CrisisPreviewTempDataKey = "AdminCrisisEscalationPreviewState";
    private const string StrategyTemplateSuccessTempDataKey = "AdminStrategyTemplateSuccess";
    private const string StrategyTemplateErrorTempDataKey = "AdminStrategyTemplateError";
    private const string StrategyTemplateBuilderDraftTempDataKey = "AdminStrategyTemplateBuilderDraft";
    private const string UltraDebugLogSuccessTempDataKey = "AdminUltraDebugLogSuccess";
    private const string UltraDebugLogErrorTempDataKey = "AdminUltraDebugLogError";
    private const string UltraDebugLogManualDisableReason = "manual_disable";
    private const string UltraDebugLogDurationExpiredReason = "duration_expired";
    private const string UltraDebugLogRuntimeErrorReason = "runtime_error";
    private const string UltraDebugLogDiskPressureReason = "disk_pressure";
    private const string UltraDebugLogRuntimeWriteFailureReason = "ultra_runtime_write_failure";
    private const string UltraDebugLogSizeLimitExceededReason = "ultra_size_limit_exceeded";
    private const int UltraDebugLogSearchDefaultTake = 25;
    private const int UltraDebugLogSearchMaxTake = 50;
    private const int UltraDebugLogSearchMaxFiles = 12;
    private const int UltraDebugLogSearchWindowKilobytes = 256;
    private static readonly string[] UltraDebugLogSearchCategories =
    [
        "scanner",
        "strategy",
        "handoff",
        "execution",
        "exchange",
        "runtime"
    ];


    private const string CriticalActionConfirmationPhrase = "ONAYLA";
    private static readonly JsonSerializerOptions PolicyJsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly IApiCredentialValidationService apiCredentialValidationService;
    private readonly IAdminAuditLogService adminAuditLogService;
    private readonly IAdminCommandRegistry adminCommandRegistry;
    private readonly IAdminGovernanceReadModelService? adminGovernanceReadModelService;
    private readonly IAdminWorkspaceReadModelService adminWorkspaceReadModelService;
    private readonly IAdminMonitoringReadModelService? adminMonitoringReadModelService;
    private readonly IApprovalWorkflowService? approvalWorkflowService;
    private readonly ICrisisEscalationService? crisisEscalationService;
    private readonly ICriticalUserOperationAuthorizer criticalUserOperationAuthorizer;
    private readonly IStrategyTemplateCatalogService strategyTemplateCatalogService;
    private readonly ILogCenterReadModelService? logCenterReadModelService;
    private readonly ILogCenterRetentionService? logCenterRetentionService;
    private readonly IGlobalPolicyEngine? globalPolicyEngine;
    private readonly IUltraDebugLogService? ultraDebugLogService;
    private readonly IGlobalExecutionSwitchService globalExecutionSwitchService;
    private readonly IGlobalSystemStateService globalSystemStateService;
    private readonly IBinanceTimeSyncService binanceTimeSyncService;
    private readonly IDataLatencyCircuitBreaker dataLatencyCircuitBreaker;
    private readonly DataLatencyGuardOptions dataLatencyGuardOptions;
    private readonly BinancePrivateDataOptions privateDataOptions;
    private readonly ITraceService traceService;
    private readonly IBinanceCredentialProbeClient? binanceCredentialProbeClient;
    private readonly IUserExchangeCommandCenterService? userExchangeCommandCenterService;
    private readonly UserManager<ApplicationUser>? userManager;
    private readonly ILogger<AdminController>? logger;

    private readonly BotExecutionPilotOptions pilotOptionsValue;

    public AdminController(
        IGlobalExecutionSwitchService globalExecutionSwitchService,
        IGlobalSystemStateService globalSystemStateService,
        IAdminCommandRegistry adminCommandRegistry,
        IAdminAuditLogService adminAuditLogService,
        ITraceService traceService,
        IBinanceTimeSyncService binanceTimeSyncService,
        IDataLatencyCircuitBreaker dataLatencyCircuitBreaker,
        IApiCredentialValidationService apiCredentialValidationService,
        IAdminWorkspaceReadModelService adminWorkspaceReadModelService,

        IStrategyTemplateCatalogService strategyTemplateCatalogService,
        ICriticalUserOperationAuthorizer criticalUserOperationAuthorizer,
        IApprovalWorkflowService? approvalWorkflowService = null,
        IAdminGovernanceReadModelService? adminGovernanceReadModelService = null,
        IAdminMonitoringReadModelService? adminMonitoringReadModelService = null,
        ILogCenterReadModelService? logCenterReadModelService = null,
        ILogCenterRetentionService? logCenterRetentionService = null,
        IGlobalPolicyEngine? globalPolicyEngine = null,
        ICrisisEscalationService? crisisEscalationService = null,
        IOptions<BotExecutionPilotOptions>? botExecutionPilotOptions = null,
        IOptions<DataLatencyGuardOptions>? dataLatencyGuardOptions = null,
        IOptions<BinancePrivateDataOptions>? privateDataOptions = null,
        IBinanceCredentialProbeClient? binanceCredentialProbeClient = null,
        IUserExchangeCommandCenterService? userExchangeCommandCenterService = null,
        UserManager<ApplicationUser>? userManager = null,
        IUltraDebugLogService? ultraDebugLogService = null,
        ILogger<AdminController>? logger = null)
    {
        this.globalExecutionSwitchService = globalExecutionSwitchService;
        this.globalSystemStateService = globalSystemStateService;
        this.adminCommandRegistry = adminCommandRegistry;
        this.adminAuditLogService = adminAuditLogService;
        this.traceService = traceService;
        this.binanceTimeSyncService = binanceTimeSyncService;
        this.dataLatencyCircuitBreaker = dataLatencyCircuitBreaker;
        this.apiCredentialValidationService = apiCredentialValidationService;
        this.adminWorkspaceReadModelService = adminWorkspaceReadModelService;

        this.strategyTemplateCatalogService = strategyTemplateCatalogService;
        this.criticalUserOperationAuthorizer = criticalUserOperationAuthorizer;
        this.approvalWorkflowService = approvalWorkflowService;
        this.adminGovernanceReadModelService = adminGovernanceReadModelService;
        this.adminMonitoringReadModelService = adminMonitoringReadModelService;
        this.logCenterReadModelService = logCenterReadModelService;
        this.logCenterRetentionService = logCenterRetentionService;
        this.globalPolicyEngine = globalPolicyEngine;
        this.crisisEscalationService = crisisEscalationService;
        this.dataLatencyGuardOptions = dataLatencyGuardOptions?.Value ?? new DataLatencyGuardOptions();
        this.privateDataOptions = privateDataOptions?.Value ?? new BinancePrivateDataOptions();
        pilotOptionsValue = botExecutionPilotOptions?.Value ?? new BotExecutionPilotOptions();
        this.binanceCredentialProbeClient = binanceCredentialProbeClient;
        this.userExchangeCommandCenterService = userExchangeCommandCenterService;
        this.userManager = userManager;
        this.ultraDebugLogService = ultraDebugLogService;
        this.logger = logger;
    }

    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
    {
        var targetReturnUrl = NormalizeAdminReturnUrl(returnUrl);

        if (User.Identity?.IsAuthenticated == true)
        {
            return HasAdminPortalAccess()
                ? LocalRedirect(targetReturnUrl)
                : RedirectToAction(nameof(AccessDenied), new { returnUrl = targetReturnUrl });
        }

        return RedirectToAction("Login", "Auth", new { area = string.Empty, returnUrl = targetReturnUrl });
    }

    [AllowAnonymous]
    public IActionResult Mfa(string? returnUrl = null, bool rememberMe = false)
    {
        var targetReturnUrl = NormalizeAdminReturnUrl(returnUrl);

        if (User.Identity?.IsAuthenticated == true)
        {
            return HasAdminPortalAccess()
                ? LocalRedirect(targetReturnUrl)
                : RedirectToAction(nameof(AccessDenied), new { returnUrl = targetReturnUrl });
        }

        return RedirectToAction("Mfa", "Auth", new { area = string.Empty, returnUrl = targetReturnUrl, rememberMe });
    }

    [AllowAnonymous]
    public IActionResult AccessDenied(string? returnUrl = null)
    {
        var targetReturnUrl = NormalizeAdminReturnUrl(returnUrl);

        if (!(User.Identity?.IsAuthenticated ?? false))
        {
            return RedirectToAction(nameof(Login), new { returnUrl = targetReturnUrl });
        }

        if (HasAdminPortalAccess())
        {
            return LocalRedirect(targetReturnUrl);
        }

        adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    ResolveAdminUserId(),
                    "Admin.Security.AccessDenied",
                    "AdminPortal",
                    targetReturnUrl,
                    oldValueSummary: null,
                    newValueSummary: null,
                    reason: "Authenticated user lacks admin portal access.",
                    correlationId: HttpContext.TraceIdentifier))
            .GetAwaiter()
            .GetResult();

        ApplyShellMeta(
            title: "Access Denied",
            description: "Admin alanında yetkisiz erişim durumları için yönlendirici ve profesyonel ekran.",
            activeNav: "Overview",
            breadcrumbItems: new[] { "Super Admin", "Security", "Access Denied" });
        ViewData["AdminAuthReturnUrl"] = targetReturnUrl;

        return View();
    }

    [AllowAnonymous]
    public IActionResult PermissionDenied(string? returnUrl = null)
    {
        var targetReturnUrl = NormalizeAdminReturnUrl(returnUrl);

        if (!(User.Identity?.IsAuthenticated ?? false))
        {
            return RedirectToAction(nameof(Login), new { returnUrl = targetReturnUrl });
        }

        if (HasAdminPortalAccess())
        {
            return LocalRedirect(targetReturnUrl);
        }

        adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    ResolveAdminUserId(),
                    "Admin.Security.PermissionDenied",
                    "AdminPortal",
                    targetReturnUrl,
                    oldValueSummary: null,
                    newValueSummary: null,
                    reason: "Authenticated user lacks the required admin permission.",
                    correlationId: HttpContext.TraceIdentifier))
            .GetAwaiter()
            .GetResult();

        ApplyShellMeta(
            title: "Insufficient Permission",
            description: "Rol var ama kapsam yetersiz olduğunda gösterilecek temiz admin ekranı.",
            activeNav: "Users",
            breadcrumbItems: new[] { "Super Admin", "Security", "Insufficient Permission" });
        ViewData["AdminAuthReturnUrl"] = targetReturnUrl;

        return View();
    }

    [AllowAnonymous]
    public IActionResult SessionExpired(string? returnUrl = null)
    {
        var targetReturnUrl = NormalizeAdminReturnUrl(returnUrl);

        if (User.Identity?.IsAuthenticated == true)
        {
            return HasAdminPortalAccess()
                ? LocalRedirect(targetReturnUrl)
                : RedirectToAction(nameof(AccessDenied), new { returnUrl = targetReturnUrl });
        }

        ApplyShellMeta(
            title: "Session Expired",
            description: "Idle timeout, re-auth ve session boundary uyarıları için admin güvenlik ekranı.",
            activeNav: "Overview",
            breadcrumbItems: new[] { "Super Admin", "Security", "Session Expired" });
        ViewData["AdminAuthReturnUrl"] = targetReturnUrl;

        return View();
    }

    [Authorize(Policy = ApplicationPolicies.IdentityAdministration)]
    public IActionResult RoleMatrix()
    {
        return RedirectToAction(nameof(Users), new { area = "Admin" });
    }

    public async Task<IActionResult> Search(string? query, CancellationToken cancellationToken)
    {
        var normalizedQuery = query?.Trim();

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            if (CanViewUserDirectory())
            {
                var userDetail = await adminWorkspaceReadModelService.GetUserDetailAsync(normalizedQuery, cancellationToken);

                if (userDetail is not null)
                {
                    return RedirectToAction(
                        nameof(UserDetail),
                        "Admin",
                        new
                        {
                            area = "Admin",
                            id = userDetail.UserId
                        });
                }
            }

            if (adminGovernanceReadModelService is not null)
            {
                var incidentDetail = await adminGovernanceReadModelService.GetIncidentDetailAsync(
                    normalizedQuery,
                    cancellationToken);

                if (incidentDetail is not null)
                {
                    return RedirectToAction(
                        nameof(IncidentDetail),
                        "Admin",
                        new
                        {
                            area = "Admin",
                            incidentReference = incidentDetail.IncidentReference
                        });
                }
            }

            var exactTraceMatch = await traceService.FindExactMatchAsync(normalizedQuery, cancellationToken);
            if (exactTraceMatch is not null)
            {
                return RedirectToAction(
                    nameof(TraceDetail),
                    "Admin",
                    new
                    {
                        area = "Admin",
                        correlationId = exactTraceMatch.CorrelationId,
                        decisionId = exactTraceMatch.DecisionId,
                        executionAttemptId = exactTraceMatch.ExecutionAttemptId
                    });
            }

            return RedirectToAction(
                nameof(Audit),
                "Admin",
                new
                {
                    area = "Admin",
                    query = normalizedQuery
                });
        }

        ApplyShellMeta(
            title: "Global Search",
            description: "CorrelationId, OrderId, DecisionId, ExecutionAttemptId, IncidentId ve UserId bazli exact-match admin global search route.",
            activeNav: "Search",
            breadcrumbItems: new[] { "Super Admin", "Search", "Global Search" });

        ViewData["AdminSearchQuery"] = normalizedQuery;
        return View();
    }

    public async Task<IActionResult> Overview(CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Super Admin Ana Akis",
            description: "Kurulumu kontrol et, sistemi aktiflestir, calismayi izle ve teknik detaylara yalnizca gerektiginde gir.",
            activeNav: "Overview",
            breadcrumbItems: new[] { "Super Admin", "Ana Akis" });

        var evaluatedAtUtc = DateTime.UtcNow;

        if (!CanEditGlobalPolicy())
        {
            return View(AdminOperationsCenterComposer.CreateAccessDenied(evaluatedAtUtc));
        }

        var operationalContext = await LoadSettingsOperationalContextAsync(cancellationToken);
        var monitoringDashboard = await LoadMonitoringDashboardSnapshotAsync(cancellationToken);
        var usersSnapshot = await adminWorkspaceReadModelService.GetUsersAsync(null, null, null, cancellationToken);
        var botOperationsSnapshot = await adminWorkspaceReadModelService.GetBotOperationsAsync(null, null, null, cancellationToken);
        var credentialSummaries = await apiCredentialValidationService.ListAdminSummariesAsync(cancellationToken: cancellationToken);
        var globalPolicySnapshot = await LoadGlobalPolicySnapshotAsync(cancellationToken);
        var retentionSnapshot = await LoadLogCenterRetentionSnapshotSafeAsync(cancellationToken);
        var canRefreshClockDrift = CanRefreshClockDrift();

        ViewData[MonitoringDashboardSnapshotViewDataKey] = monitoringDashboard;
        ViewData[ExecutionSwitchSnapshotViewDataKey] = operationalContext.ExecutionSnapshot;
        ViewData[GlobalSystemStateSnapshotViewDataKey] = operationalContext.GlobalSystemStateSnapshot;
        ViewData[GlobalPolicySnapshotViewDataKey] = globalPolicySnapshot;
        ViewData[AdminLogCenterRetentionSnapshotViewDataKey] = retentionSnapshot;
        ViewData[ClockDriftSnapshotViewDataKey] = operationalContext.ClockDriftViewModel;
        ViewData[DriftGuardSnapshotViewDataKey] = operationalContext.DriftGuardViewModel;
        ViewData[CanRefreshClockDriftViewDataKey] = canRefreshClockDrift;
        ViewData[CrisisPreviewViewDataKey] = LoadCrisisPreviewViewModelFromTempData();
        ViewData[PilotOrderNotionalSummaryViewDataKey] = operationalContext.PilotOrderNotionalSummary;
        ViewData[PilotOrderNotionalToneViewDataKey] = operationalContext.PilotOrderNotionalTone;
        ViewData[LongRegimePolicyStatusViewDataKey] = operationalContext.LongRegimePolicyStatus;
        ViewData[LongRegimePolicyToneViewDataKey] = operationalContext.LongRegimePolicyTone;
        ViewData[LongRegimePolicyDetailViewDataKey] = operationalContext.LongRegimePolicyDetail;
        ViewData["AdminActivationControlCenter"] = operationalContext.ActivationControlCenter;

        var model = AdminOperationsCenterComposer.Compose(
            operationalContext.ActivationControlCenter,
            monitoringDashboard,
            operationalContext.ClockDriftSnapshot,
            operationalContext.DriftGuardSnapshot,
            usersSnapshot,
            botOperationsSnapshot,
            credentialSummaries,
            globalPolicySnapshot,
            pilotOptionsValue,
            retentionSnapshot,
            operationalContext.ExecutionSnapshot,
            operationalContext.GlobalSystemStateSnapshot,
            canRefreshClockDrift,
            evaluatedAtUtc);

        return View(model);
    }

    [Authorize(Policy = ApplicationPolicies.IdentityAdministration)]
    public async Task<IActionResult> Users(
        string? query,
        string? status,
        string? mfa,
        CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Kullanıcılar",
            description: "Kullanıcı listesi, filtre barı, güvenlik sinyalleri ve kontrollü admin aksiyonları için operasyonel kullanıcı yönetimi yüzeyi.",
            activeNav: "Users",
            breadcrumbItems: new[] { "Super Admin", "Kimlik", "Kullanıcılar" });

        ViewData["AdminUsersPageSnapshot"] = await adminWorkspaceReadModelService.GetUsersAsync(query, status, mfa, cancellationToken);
        return View();
    }

    [Authorize(Policy = ApplicationPolicies.IdentityAdministration)]
    public async Task<IActionResult> UserDetail(string? id, CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Kullanıcı Detayı",
            description: "Profil özeti, güvenlik durumu, bot ve exchange summary ile son aktiviteleri operasyon odaklı detay ekranında toplar.",
            activeNav: "UserDetail",
            breadcrumbItems: new[] { "Super Admin", "Kimlik", "Kullanıcı Detayı" });

        var snapshot = string.IsNullOrWhiteSpace(id)
            ? null
            : await adminWorkspaceReadModelService.GetUserDetailAsync(id, cancellationToken);

        if (snapshot is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
        }

        ViewData["AdminUserDetailPageSnapshot"] = snapshot;
        ViewData["AdminEntityId"] = snapshot?.UserId ?? (string.IsNullOrWhiteSpace(id) ? "usr-unknown" : id);
        ViewData["AdminEntityLabel"] = snapshot?.DisplayName ?? (string.IsNullOrWhiteSpace(id) ? "Kullanıcı bulunamadı" : $"Kullanıcı {id}");
        return View();
    }


    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = ApplicationPolicies.IdentityAdministration)]
    public async Task<IActionResult> ApproveUser(string? id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            TempData[AdminUserErrorTempDataKey] = "Kullanıcı seçilmedi.";
            return RedirectToAction(nameof(Users));
        }

        if (userManager is null)
        {
            TempData[AdminUserErrorTempDataKey] = "Kullanıcı onayı şu an hazır değil.";
            return RedirectToAction(nameof(UserDetail), new { id });
        }

        var user = await userManager.FindByIdAsync(id.Trim());
        if (user is null)
        {
            TempData[AdminUserErrorTempDataKey] = "Kullanıcı bulunamadı.";
            return RedirectToAction(nameof(Users));
        }

        if (user.EmailConfirmed)
        {
            TempData[AdminUserSuccessTempDataKey] = "Kullanıcı zaten onaylı.";
            return RedirectToAction(nameof(UserDetail), new { id = user.Id });
        }

        user.EmailConfirmed = true;
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            user.EmailConfirmed = false;
            TempData[AdminUserErrorTempDataKey] = "Kullanıcı onaylanamadı.";
            return RedirectToAction(nameof(UserDetail), new { id = user.Id });
        }

        await adminAuditLogService.WriteAsync(
            BuildAdminAuditLogWriteRequest(
                ResolveAdminUserId(),
                "Admin.Users.Approve",
                "User",
                user.Id,
                oldValueSummary: "Onay bekliyor",
                newValueSummary: "Onaylı",
                reason: "Kullanıcı onaylandı.",
                correlationId: HttpContext.TraceIdentifier),
            cancellationToken);

        TempData[AdminUserSuccessTempDataKey] = "Kullanıcı onaylandı.";
        return RedirectToAction(nameof(UserDetail), new { id = user.Id });
    }

    [Authorize(Policy = ApplicationPolicies.AuditRead)]
    public async Task<IActionResult> ExchangeAccounts(CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Exchange Hesapları",
            description: "Platform çapındaki exchange bağlantılarını sağlık, permission, freshness ve risk bayraklarıyla izleyen admin monitoring yüzeyi.",
            activeNav: "ExchangeAccounts",
            breadcrumbItems: new[] { "Super Admin", "Operasyon", "Exchange Hesapları" });

        var summaries = await apiCredentialValidationService.ListAdminSummariesAsync(cancellationToken: cancellationToken);
        return View(summaries);
    }

    [Authorize(Policy = ApplicationPolicies.TradeOperations)]
    public async Task<IActionResult> BotOperations(
        string? query,
        string? status,
        string? mode,
        CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Bot Operasyonları",
            description: "Platform genelindeki botları durum, strategy, risk ve AI etkisi perspektifiyle izleyen operasyon ekranı.",
            activeNav: "BotOperations",
            breadcrumbItems: new[] { "Super Admin", "Operasyon", "Bot Operasyonları" });

        ViewData["AdminBotOperationsPageSnapshot"] = await adminWorkspaceReadModelService.GetBotOperationsAsync(query, status, mode, cancellationToken);
        return View();
    }

    [Authorize(Policy = ApplicationPolicies.TradeOperations)]
    public async Task<IActionResult> StrategyAiMonitoring(
        string? query,
        CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Strategy / AI İzleme",
            description: "Strategy template kullanımı, AI health, confidence/veto sinyalleri ve explainability örneklerini izleyen admin monitoring yüzeyi.",
            activeNav: "StrategyAiMonitoring",
            breadcrumbItems: new[] { "Super Admin", "İzleme", "Strategy / AI" });

        ViewData["AdminStrategyAiMonitoringPageSnapshot"] = await adminWorkspaceReadModelService.GetStrategyAiMonitoringAsync(query, cancellationToken);
        return View();
    }

    [Authorize(Policy = ApplicationPolicies.AdminPortalAccess)]
    public async Task<IActionResult> StrategyTemplates(string? templateKey, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(templateKey))
        {
            return await RenderStrategyTemplateDetailAsync(templateKey, cancellationToken);
        }

        ApplyShellMeta(
            title: "Strategy Templates",
            description: "Built-in ve custom strategy template katalogunu inventory mantigiyla gosteren Super Admin liste yuzeyi.",
            activeNav: "StrategyTemplates",
            breadcrumbItems: new[] { "Super Admin", "Strategy", "Strategy Templates" });

        var model = await BuildStrategyTemplateCatalogPageViewModelAsync(null, cancellationToken, autoSelectFirstTemplate: false);
        return View(model);
    }

    [Authorize(Policy = ApplicationPolicies.AdminPortalAccess)]
    public async Task<IActionResult> StrategyBuilder(string? templateKey, CancellationToken cancellationToken)
    {
        var pendingDraft = LoadStrategyTemplateBuilderDraftFromTempData();
        var requestedTemplateKey = NormalizeOptionalInput(templateKey, 128);
        var effectiveTemplateKey = NormalizeOptionalInput(pendingDraft?.SourceTemplateKey, 128) ?? requestedTemplateKey;

        ApplyShellMeta(
            title: "Yeni template oluştur",
            description: "Bu ekran yalnizca yeni template olusturma icindir; revise ve archive burada yer almaz.",
            activeNav: "StrategyTemplates",
            breadcrumbItems: new[] { "Super Admin", "Strategy", "Strategy Templates", "Yeni Template" });

        var model = await BuildStrategyTemplateCatalogPageViewModelAsync(effectiveTemplateKey, cancellationToken, autoSelectFirstTemplate: false);
        model = model with
        {
            BuilderDraft = ResolveStrategyTemplateBuilderDraft(model.SelectedTemplate, pendingDraft)
        };
        ApplyStrategyBuilderRuntimeViewData(model.BuilderDraft?.DefinitionJson ?? model.SelectedTemplate?.DefinitionJson);
        return View("StrategyTemplateCreate", model);
    }

    [Authorize(Policy = ApplicationPolicies.AdminPortalAccess)]
    public async Task<IActionResult> StrategyTemplateDetail(string? templateKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(templateKey))
        {
            return RedirectToAction(nameof(StrategyTemplates), new { area = "Admin" });
        }

        return RedirectToAction(nameof(StrategyTemplates), new { area = "Admin", templateKey });
    }

    private async Task<IActionResult> RenderStrategyTemplateDetailAsync(string? templateKey, CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Template detay / düzenleme",
            description: "Bu ekran tek template odaklidir; revise ve archive yalnizca burada yapilir, create burada yer almaz.",
            activeNav: "StrategyTemplates",
            breadcrumbItems: new[] { "Super Admin", "Strategy", "Strategy Templates", "Detay" });

        var model = await BuildStrategyTemplateCatalogPageViewModelAsync(templateKey, cancellationToken, autoSelectFirstTemplate: false);
        ApplyStrategyBuilderRuntimeViewData(model.SelectedTemplate?.DefinitionJson);
        return View("StrategyTemplateDetail", model);
    }

    private void ApplyStrategyBuilderRuntimeViewData(string? definitionJson)
    {
        ViewData["AdminStrategyBuilderRuntimeConfig"] =
            StrategyBuilderRuntimeParityHelper.BuildRuntimeConfig(pilotOptionsValue);
        ViewData["AdminStrategyBuilderExplainability"] =
            StrategyBuilderRuntimeParityHelper.BuildSnapshot(definitionJson, pilotOptionsValue);
    }

    private AdminStrategyTemplateBuilderDraftViewModel? LoadStrategyTemplateBuilderDraftFromTempData()
    {
        if (TempData[StrategyTemplateBuilderDraftTempDataKey] is not string serializedDraft ||
            string.IsNullOrWhiteSpace(serializedDraft))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AdminStrategyTemplateBuilderDraftViewModel>(
                serializedDraft,
                PolicyJsonSerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void PersistStrategyTemplateBuilderDraft(
        string? sourceTemplateKey,
        string? templateKey,
        string? templateName,
        string? description,
        string? category,
        string? definitionJson)
    {
        TempData[StrategyTemplateBuilderDraftTempDataKey] = JsonSerializer.Serialize(
            new AdminStrategyTemplateBuilderDraftViewModel(
                NormalizeOptionalInput(sourceTemplateKey, 128),
                NormalizeOptionalInput(templateKey, 128),
                NormalizeOptionalInput(templateName, 128),
                NormalizeOptionalInput(description, 512),
                NormalizeOptionalInput(category, 64),
                string.IsNullOrWhiteSpace(definitionJson) ? null : definitionJson.Trim()),
            PolicyJsonSerializerOptions);
    }

    private static AdminStrategyTemplateBuilderDraftViewModel? ResolveStrategyTemplateBuilderDraft(
        StrategyTemplateSnapshot? selectedTemplate,
        AdminStrategyTemplateBuilderDraftViewModel? draft)
    {
        if (draft is not null)
        {
            return draft;
        }

        if (selectedTemplate is null)
        {
            return null;
        }

        return new AdminStrategyTemplateBuilderDraftViewModel(
            selectedTemplate.TemplateKey,
            null,
            selectedTemplate.TemplateName,
            selectedTemplate.Description,
            selectedTemplate.Category,
            selectedTemplate.DefinitionJson);
    }

    private string? ReadPostedStrategyTemplateSourceKey()
    {
        if (Request?.HasFormContentType != true ||
            !Request.Form.TryGetValue("sourceTemplateKey", out var values))
        {
            return null;
        }

        return NormalizeOptionalInput(values.ToString(), 128);
    }

    private static object BuildStrategyBuilderRouteValues(string? sourceTemplateKey)
    {
        return string.IsNullOrWhiteSpace(sourceTemplateKey)
            ? new { area = "Admin" }
            : new { area = "Admin", templateKey = sourceTemplateKey };
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateStrategyTemplate(
        string? templateKey,
        string? templateName,
        string? description,
        string? category,
        string? definitionJson,
        string? reason,
        CancellationToken cancellationToken)
    {
        var normalizedSourceTemplateKey = ReadPostedStrategyTemplateSourceKey();
        var draftTemplateKey = NormalizeOptionalInput(templateKey, 128);
        var draftTemplateName = NormalizeOptionalInput(templateName, 128);
        var draftDescription = NormalizeOptionalInput(description, 512);
        var draftCategory = NormalizeOptionalInput(category, 64);
        var draftDefinitionJson = definitionJson?.Trim();
        var builderRedirectRouteValues = BuildStrategyBuilderRouteValues(normalizedSourceTemplateKey);

        var mfaResult = await EnforcePlatformAdminMfaAsync(
            "StrategyTemplates.Create",
            StrategyTemplateErrorTempDataKey,
            nameof(StrategyBuilder),
            builderRedirectRouteValues,
            cancellationToken);

        if (mfaResult is not null)
        {
            PersistStrategyTemplateBuilderDraft(
                normalizedSourceTemplateKey,
                draftTemplateKey,
                draftTemplateName,
                draftDescription,
                draftCategory,
                draftDefinitionJson);
            return mfaResult;
        }

        var normalizedReason = NormalizeRequiredReason(reason);
        if (normalizedReason is null)
        {
            PersistStrategyTemplateBuilderDraft(
                normalizedSourceTemplateKey,
                draftTemplateKey,
                draftTemplateName,
                draftDescription,
                draftCategory,
                draftDefinitionJson);
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateInputMessage("Create", "Audit reason zorunludur. Yeni template olusturulamadi.");
            return RedirectToAction(nameof(StrategyBuilder), builderRedirectRouteValues);
        }

        var normalizedTemplateKey = NormalizeRequiredInput(templateKey, 128);
        var normalizedTemplateName = NormalizeRequiredInput(templateName, 128);
        var normalizedDescription = NormalizeRequiredInput(description, 512);
        var normalizedCategory = NormalizeRequiredInput(category, 64);
        var normalizedDefinitionJson = definitionJson?.Trim();
        if (normalizedTemplateKey is null ||
            normalizedTemplateName is null ||
            normalizedDescription is null ||
            normalizedCategory is null ||
            string.IsNullOrWhiteSpace(normalizedDefinitionJson))
        {
            PersistStrategyTemplateBuilderDraft(
                normalizedSourceTemplateKey,
                draftTemplateKey,
                draftTemplateName,
                draftDescription,
                draftCategory,
                draftDefinitionJson);
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateInputMessage("Create", "Template key, isim, aciklama, kategori ve builder definitionJson zorunludur.");
            return RedirectToAction(nameof(StrategyBuilder), builderRedirectRouteValues);
        }

        var actorUserId = ResolveAdminUserId();

        try
        {
            var created = await strategyTemplateCatalogService.CreateCustomAsync(
                actorUserId,
                normalizedTemplateKey,
                normalizedTemplateName,
                normalizedDescription,
                normalizedCategory,
                normalizedDefinitionJson,
                cancellationToken);

            await adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    actorUserId,
                    "Admin.StrategyTemplates.Create",
                    "StrategyTemplate",
                    created.TemplateKey,
                    oldValueSummary: null,
                    newValueSummary: BuildStrategyTemplateSummary(created),
                    normalizedReason,
                    HttpContext.TraceIdentifier),
                cancellationToken);

            TempData.Remove(StrategyTemplateBuilderDraftTempDataKey);
            TempData[StrategyTemplateSuccessTempDataKey] = BuildStrategyTemplateSuccessMessage("Create", created, created.ActiveRevisionNumber);
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin", templateKey = created.TemplateKey });
        }
        catch (StrategyTemplateCatalogException exception)
        {
            PersistStrategyTemplateBuilderDraft(
                normalizedSourceTemplateKey,
                normalizedTemplateKey,
                normalizedTemplateName,
                normalizedDescription,
                normalizedCategory,
                normalizedDefinitionJson);
            await WriteStrategyTemplateFailureAuditAsync(
                actorUserId,
                "Admin.StrategyTemplates.CreateBlocked",
                normalizedTemplateKey,
                null,
                $"TemplateKey={normalizedTemplateKey}; Name={normalizedTemplateName}; Category={normalizedCategory}",
                normalizedReason,
                exception.FailureCode,
                cancellationToken);
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateFailureMessage("Create", normalizedTemplateKey, exception);
            return RedirectToAction(nameof(StrategyBuilder), builderRedirectRouteValues);
        }
        catch (StrategyDefinitionValidationException exception)
        {
            PersistStrategyTemplateBuilderDraft(
                normalizedSourceTemplateKey,
                normalizedTemplateKey,
                normalizedTemplateName,
                normalizedDescription,
                normalizedCategory,
                normalizedDefinitionJson);
            await WriteStrategyTemplateFailureAuditAsync(
                actorUserId,
                "Admin.StrategyTemplates.CreateBlocked",
                normalizedTemplateKey,
                null,
                $"TemplateKey={normalizedTemplateKey}; Name={normalizedTemplateName}; Category={normalizedCategory}",
                normalizedReason,
                exception.StatusCode,
                cancellationToken);
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateValidationMessage("Create", normalizedTemplateKey, exception.Message);
            return RedirectToAction(nameof(StrategyBuilder), builderRedirectRouteValues);
        }
        catch (InvalidOperationException exception)
        {
            PersistStrategyTemplateBuilderDraft(
                normalizedSourceTemplateKey,
                normalizedTemplateKey,
                normalizedTemplateName,
                normalizedDescription,
                normalizedCategory,
                normalizedDefinitionJson);
            await WriteStrategyTemplateFailureAuditAsync(
                actorUserId,
                "Admin.StrategyTemplates.CreateBlocked",
                normalizedTemplateKey,
                null,
                $"TemplateKey={normalizedTemplateKey}; Name={normalizedTemplateName}; Category={normalizedCategory}",
                normalizedReason,
                "OperationInvalid",
                cancellationToken);
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateOperationMessage("Create", normalizedTemplateKey, exception.Message);
            return RedirectToAction(nameof(StrategyBuilder), builderRedirectRouteValues);
        }
        catch
        {
            PersistStrategyTemplateBuilderDraft(
                normalizedSourceTemplateKey,
                normalizedTemplateKey,
                normalizedTemplateName,
                normalizedDescription,
                normalizedCategory,
                normalizedDefinitionJson);
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateGenericFailureMessage("Create", normalizedTemplateKey);
            return RedirectToAction(nameof(StrategyBuilder), builderRedirectRouteValues);
        }
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviseStrategyTemplate(
        string? templateKey,
        string? templateName,
        string? description,
        string? category,
        string? definitionJson,
        CancellationToken cancellationToken)
    {
        var normalizedTemplateKey = NormalizeRequiredInput(templateKey, 128);
        if (normalizedTemplateKey is null)
        {
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateInputMessage("Save", "Template key zorunludur. Guncelleme islemi baslatilamadi.");
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin" });
        }

        var mfaResult = await EnforcePlatformAdminMfaAsync(
            "StrategyTemplates.Revise",
            StrategyTemplateErrorTempDataKey,
            nameof(StrategyTemplateDetail),
            new { area = "Admin", templateKey = normalizedTemplateKey },
            cancellationToken);

        if (mfaResult is not null)
        {
            return mfaResult;
        }

        const string normalizedReason = "DirectUpdate";

        var normalizedTemplateName = NormalizeRequiredInput(templateName, 128);
        var normalizedDescription = NormalizeRequiredInput(description, 512);
        var normalizedCategory = NormalizeRequiredInput(category, 64);
        var normalizedDefinitionJson = definitionJson?.Trim();
        if (normalizedTemplateName is null ||
            normalizedDescription is null ||
            normalizedCategory is null ||
            string.IsNullOrWhiteSpace(normalizedDefinitionJson))
        {
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateInputMessage("Save", "Template adi, aciklama, kategori ve builder definitionJson zorunludur.");
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin", templateKey = normalizedTemplateKey });
        }

        var actorUserId = ResolveAdminUserId();

        try
        {
            var previous = await strategyTemplateCatalogService.GetIncludingArchivedAsync(normalizedTemplateKey, cancellationToken);
            var revised = await strategyTemplateCatalogService.UpdateCurrentAsync(
                normalizedTemplateKey,
                normalizedTemplateName,
                normalizedDescription,
                normalizedCategory,
                normalizedDefinitionJson,
                cancellationToken);

            await adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    actorUserId,
                    "Admin.StrategyTemplates.Save",
                    "StrategyTemplate",
                    revised.TemplateKey,
                    oldValueSummary: BuildStrategyTemplateSummary(previous),
                    newValueSummary: BuildStrategyTemplateSummary(revised),
                    normalizedReason,
                    HttpContext.TraceIdentifier),
                cancellationToken);

            TempData[StrategyTemplateSuccessTempDataKey] = BuildStrategyTemplateSuccessMessage("Save", revised, revised.ActiveRevisionNumber);
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin", templateKey = revised.TemplateKey });
        }
        catch (StrategyTemplateCatalogException exception)
        {
            await WriteStrategyTemplateFailureAuditAsync(
                actorUserId,
                "Admin.StrategyTemplates.SaveBlocked",
                normalizedTemplateKey,
                null,
                $"TemplateKey={normalizedTemplateKey}; Name={normalizedTemplateName}; Category={normalizedCategory}",
                normalizedReason,
                exception.FailureCode,
                cancellationToken);
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateFailureMessage("Save", normalizedTemplateKey, exception);
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin", templateKey = normalizedTemplateKey });
        }
        catch (StrategyDefinitionValidationException exception)
        {
            await WriteStrategyTemplateFailureAuditAsync(
                actorUserId,
                "Admin.StrategyTemplates.SaveBlocked",
                normalizedTemplateKey,
                null,
                $"TemplateKey={normalizedTemplateKey}; Name={normalizedTemplateName}; Category={normalizedCategory}",
                normalizedReason,
                exception.StatusCode,
                cancellationToken);
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateValidationMessage("Save", normalizedTemplateKey, exception.Message);
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin", templateKey = normalizedTemplateKey });
        }
        catch (InvalidOperationException exception)
        {
            await WriteStrategyTemplateFailureAuditAsync(
                actorUserId,
                "Admin.StrategyTemplates.SaveBlocked",
                normalizedTemplateKey,
                null,
                $"TemplateKey={normalizedTemplateKey}; Name={normalizedTemplateName}; Category={normalizedCategory}",
                normalizedReason,
                "OperationInvalid",
                cancellationToken);
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateOperationMessage("Save", normalizedTemplateKey, exception.Message);
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin", templateKey = normalizedTemplateKey });
        }
        catch
        {
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateGenericFailureMessage("Save", normalizedTemplateKey);
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin", templateKey = normalizedTemplateKey });
        }
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PublishStrategyTemplate(
        string? templateKey,
        int revisionNumber,
        string? reason,
        CancellationToken cancellationToken)
    {
        var normalizedTemplateKey = NormalizeRequiredInput(templateKey, 128);
        if (normalizedTemplateKey is null)
        {
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateInputMessage("Publish", "Template key zorunludur. Publish islemi baslatilamadi.");
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin" });
        }

        var mfaResult = await EnforcePlatformAdminMfaAsync(
            "StrategyTemplates.Publish",
            StrategyTemplateErrorTempDataKey,
            nameof(StrategyTemplateDetail),
            new { area = "Admin", templateKey = normalizedTemplateKey },
            cancellationToken);

        if (mfaResult is not null)
        {
            return mfaResult;
        }

        var normalizedReason = NormalizeRequiredReason(reason);
        if (normalizedReason is null)
        {
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateInputMessage("Publish", "Audit reason zorunludur. Publish islemi tamamlanamadi.");
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin", templateKey = normalizedTemplateKey });
        }

        if (revisionNumber <= 0)
        {
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateInputMessage("Publish", "Revision numarasi gecersiz. Publish icin gecerli bir revision secin.");
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin", templateKey = normalizedTemplateKey });
        }

        var actorUserId = ResolveAdminUserId();

        try
        {
            var previous = await strategyTemplateCatalogService.GetIncludingArchivedAsync(normalizedTemplateKey, cancellationToken);
            var published = await strategyTemplateCatalogService.PublishAsync(normalizedTemplateKey, revisionNumber, cancellationToken);

            await adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    actorUserId,
                    "Admin.StrategyTemplates.Publish",
                    "StrategyTemplate",
                    published.TemplateKey,
                    oldValueSummary: BuildStrategyTemplateSummary(previous),
                    newValueSummary: BuildStrategyTemplateSummary(published),
                    normalizedReason,
                    HttpContext.TraceIdentifier),
                cancellationToken);

            TempData[StrategyTemplateSuccessTempDataKey] = BuildStrategyTemplateSuccessMessage("Publish", published, published.PublishedRevisionNumber);
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin", templateKey = published.TemplateKey });
        }
        catch (StrategyTemplateCatalogException exception)
        {
            await WriteStrategyTemplateFailureAuditAsync(
                actorUserId,
                "Admin.StrategyTemplates.PublishBlocked",
                normalizedTemplateKey,
                null,
                $"TemplateKey={normalizedTemplateKey}; Revision={revisionNumber}",
                normalizedReason,
                exception.FailureCode,
                cancellationToken);
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateFailureMessage("Publish", normalizedTemplateKey, exception);
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin", templateKey = normalizedTemplateKey });
        }
        catch (ArgumentOutOfRangeException exception)
        {
            await WriteStrategyTemplateFailureAuditAsync(
                actorUserId,
                "Admin.StrategyTemplates.PublishBlocked",
                normalizedTemplateKey,
                null,
                $"TemplateKey={normalizedTemplateKey}; Revision={revisionNumber}",
                normalizedReason,
                "RevisionOutOfRange",
                cancellationToken);
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateOperationMessage("Publish", normalizedTemplateKey, exception.Message);
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin", templateKey = normalizedTemplateKey });
        }
        catch (InvalidOperationException exception)
        {
            await WriteStrategyTemplateFailureAuditAsync(
                actorUserId,
                "Admin.StrategyTemplates.PublishBlocked",
                normalizedTemplateKey,
                null,
                $"TemplateKey={normalizedTemplateKey}; Revision={revisionNumber}",
                normalizedReason,
                "OperationInvalid",
                cancellationToken);
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateOperationMessage("Publish", normalizedTemplateKey, exception.Message);
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin", templateKey = normalizedTemplateKey });
        }
        catch
        {
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateGenericFailureMessage("Publish", normalizedTemplateKey);
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin", templateKey = normalizedTemplateKey });
        }
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ArchiveStrategyTemplate(
        string? templateKey,
        string? reason,
        CancellationToken cancellationToken)
    {
        var normalizedTemplateKey = NormalizeRequiredInput(templateKey, 128);
        if (normalizedTemplateKey is null)
        {
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateInputMessage("Archive", "Template key zorunludur. Archive islemi baslatilamadi.");
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin" });
        }

        var mfaResult = await EnforcePlatformAdminMfaAsync(
            "StrategyTemplates.Archive",
            StrategyTemplateErrorTempDataKey,
            nameof(StrategyTemplateDetail),
            new { area = "Admin", templateKey = normalizedTemplateKey },
            cancellationToken);

        if (mfaResult is not null)
        {
            return mfaResult;
        }

        var normalizedReason = NormalizeRequiredReason(reason);
        if (normalizedReason is null)
        {
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateInputMessage("Archive", "Audit reason zorunludur. Archive islemi tamamlanamadi.");
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin", templateKey = normalizedTemplateKey });
        }

        var actorUserId = ResolveAdminUserId();

        try
        {
            var previous = await strategyTemplateCatalogService.GetIncludingArchivedAsync(normalizedTemplateKey, cancellationToken);
            var archived = await strategyTemplateCatalogService.ArchiveAsync(normalizedTemplateKey, cancellationToken);

            await adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    actorUserId,
                    "Admin.StrategyTemplates.Archive",
                    "StrategyTemplate",
                    archived.TemplateKey,
                    oldValueSummary: BuildStrategyTemplateSummary(previous),
                    newValueSummary: BuildStrategyTemplateSummary(archived),
                    normalizedReason,
                    HttpContext.TraceIdentifier),
                cancellationToken);

            TempData[StrategyTemplateSuccessTempDataKey] = BuildStrategyTemplateSuccessMessage("Archive", archived, archived.ActiveRevisionNumber);
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin", templateKey = archived.TemplateKey });
        }
        catch (StrategyTemplateCatalogException exception)
        {
            await WriteStrategyTemplateFailureAuditAsync(
                actorUserId,
                "Admin.StrategyTemplates.ArchiveBlocked",
                normalizedTemplateKey,
                null,
                $"TemplateKey={normalizedTemplateKey}; Action=Archive",
                normalizedReason,
                exception.FailureCode,
                cancellationToken);
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateFailureMessage("Archive", normalizedTemplateKey, exception);
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin", templateKey = normalizedTemplateKey });
        }
        catch (InvalidOperationException exception)
        {
            await WriteStrategyTemplateFailureAuditAsync(
                actorUserId,
                "Admin.StrategyTemplates.ArchiveBlocked",
                normalizedTemplateKey,
                null,
                $"TemplateKey={normalizedTemplateKey}; Action=Archive",
                normalizedReason,
                "OperationInvalid",
                cancellationToken);
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateOperationMessage("Archive", normalizedTemplateKey, exception.Message);
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin", templateKey = normalizedTemplateKey });
        }
        catch
        {
            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateGenericFailureMessage("Archive", normalizedTemplateKey);
            return RedirectToAction(nameof(StrategyTemplateDetail), new { area = "Admin", templateKey = normalizedTemplateKey });
        }
    }
    public async Task<IActionResult> SystemHealth(CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Sistem Sağlığı",
            description: "API, web app, queue ve worker health sinyallerini failed jobs ve stale warning görünümüyle tek ekranda toplar.",
            activeNav: "SystemHealth",
            breadcrumbItems: new[] { "Super Admin", "Runtime", "Sistem Sağlığı" });

        ViewData[MonitoringDashboardSnapshotViewDataKey] = await LoadMonitoringDashboardSnapshotAsync(cancellationToken);

        return View();
    }

    public async Task<IActionResult> Jobs(CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Job / Worker Durumu",
            description: "Worker health, last heartbeat ve failed jobs akışlarını ortak system health görünümünde toplar.",
            activeNav: "Jobs",
            breadcrumbItems: new[] { "Super Admin", "Runtime", "Job / Worker" });

        ViewData[MonitoringDashboardSnapshotViewDataKey] = await LoadMonitoringDashboardSnapshotAsync(cancellationToken);

        return View("SystemHealth");
    }

    [Authorize(Policy = ApplicationPolicies.AuditRead)]
    [HttpGet("/admin/audit-logs")]
    public Task<IActionResult> AuditLogs(
        string? query,
        string? correlationId,
        string? decisionId,
        string? executionAttemptId,
        string? userId,
        string? symbol,
        string? outcome,
        string? reasonCode,
        string? focus,
        int take = 120,
        int page = 1,
        int pageSize = 25,
        string? logBucket = null,
        string? logCategory = null,
        string? logSource = null,
        string? logSearch = null,
        string? logTimeWindow = null,
        int logTake = 25,
        bool ultraLog = false,
        CancellationToken cancellationToken = default)
    {
        return Audit(
            query,
            correlationId,
            decisionId,
            executionAttemptId,
            userId,
            symbol,
            outcome,
            reasonCode,
            focus,
            take,
            page,
            pageSize,
            logBucket,
            logCategory,
            logSource,
            logSearch,
            logTimeWindow,
            logTake,
            ultraLog,
            cancellationToken);
    }

    [Authorize(Policy = ApplicationPolicies.AuditRead)]
    public async Task<IActionResult> Audit(
        string? query,
        string? correlationId,
        string? decisionId,
        string? executionAttemptId,
        string? userId,
        string? symbol,
        string? outcome,
        string? reasonCode,
        string? focus,
        int take = 120,
        int page = 1,
        int pageSize = 25,
        string? logBucket = null,
        string? logCategory = null,
        string? logSource = null,
        string? logSearch = null,
        string? logTimeWindow = null,
        int logTake = 25,
        bool ultraLog = false,
        CancellationToken cancellationToken = default)
    {
        var routeStopwatch = Stopwatch.StartNew();
        ApplyShellMeta(
            title: "Incident / Audit / Decision Center",
            description: "Decision, execution, admin audit, approval ve incident zincirini reason code ve outcome filtreleriyle tek merkezde geriye donuk okutan operasyon ekranı.",
            activeNav: "Audit",
            breadcrumbItems: new[] { "Super Admin", "Gozlem", "Incident / Audit / Decision" });

        var normalizedTake = NormalizeAuditTake(take);
        var normalizedPage = NormalizeAuditPage(page);
        var normalizedPageSize = NormalizeAuditPageSize(
            pageSize,
            normalizedTake,
            Request.Query.ContainsKey("pageSize"));
        var normalizedQuery = NormalizeOptionalInput(query, 128);
        var normalizedCorrelationId = NormalizeOptionalInput(correlationId, 128);
        var normalizedDecisionId = NormalizeOptionalInput(decisionId, 64);
        var normalizedExecutionAttemptId = NormalizeOptionalInput(executionAttemptId, 64);
        var normalizedUserId = NormalizeOptionalInput(userId, 450);
        var normalizedSymbol = NormalizeOptionalInput(symbol, 32);
        var normalizedFocus = NormalizeOptionalInput(focus, 128);
        var normalizedOutcome = NormalizeOptionalInput(outcome, 32);
        var normalizedReasonCode = NormalizeOptionalInput(reasonCode, 128);
        var requestQuery = ShouldUseFocusBackfillQuery(
            normalizedFocus,
            normalizedQuery,
            normalizedCorrelationId,
            normalizedDecisionId,
            normalizedExecutionAttemptId,
            normalizedUserId,
            normalizedSymbol)
            ? normalizedFocus
            : normalizedQuery;
        var shouldLoadUltraDebugLog = ShouldLoadUltraDebugLog(
            ultraLog,
            logBucket,
            logCategory,
            logSource,
            logSearch,
            logTimeWindow);
        var useBoundedInitialAuditLoad = ShouldUseBoundedInitialAuditLoad(
            requestQuery,
            normalizedCorrelationId,
            normalizedDecisionId,
            normalizedExecutionAttemptId,
            normalizedUserId,
            normalizedSymbol,
            normalizedOutcome,
            normalizedReasonCode,
            normalizedFocus,
            normalizedPage,
            shouldLoadUltraDebugLog);

        if (useBoundedInitialAuditLoad)
        {
            normalizedTake = Math.Min(normalizedTake, 25);
            normalizedPageSize = Math.Min(normalizedPageSize, 25);
        }

        var request = new LogCenterQueryRequest(
            requestQuery,
            normalizedCorrelationId,
            normalizedDecisionId,
            normalizedExecutionAttemptId,
            normalizedUserId,
            normalizedSymbol,
            Status: null,
            FromUtc: null,
            ToUtc: null,
            Take: normalizedTake,
            Page: normalizedPage,
            PageSize: normalizedPageSize);
        var serviceRequest = request with
        {
            Take = ResolveAuditServiceTake(
                normalizedTake,
                normalizedPage,
                normalizedPageSize,
                normalizedOutcome,
                normalizedReasonCode,
                Request.Query.ContainsKey("take"),
                useBoundedInitialAuditLoad)
        };
        var evaluatedAtUtc = DateTime.UtcNow;

        LogAuditRoutePhase(
            "AuditRouteStarted",
            routeStopwatch.ElapsedMilliseconds,
            useBoundedInitialAuditLoad,
            shouldLoadUltraDebugLog,
            false,
            null);

        LogCenterPageSnapshot snapshot;
        if (useBoundedInitialAuditLoad)
        {
            snapshot = CreateLightweightAuditShellSnapshot(request);
            LogAuditRoutePhase(
                "AuditLightweightShellSnapshot",
                routeStopwatch.ElapsedMilliseconds,
                useBoundedInitialAuditLoad,
                shouldLoadUltraDebugLog,
                false,
                snapshot.Entries.Count);
        }
        else if (logCenterReadModelService is null)
        {
            snapshot = new LogCenterPageSnapshot(
                request,
                new LogCenterSummarySnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, null),
                await LoadLogCenterRetentionSnapshotSafeAsync(cancellationToken) ?? new LogCenterRetentionSnapshot(false, 0, 0, 0, 0, 0, 0, null, null),
                Array.Empty<LogCenterEntrySnapshot>(),
                true,
                "Log center read-model unavailable.");
            LogAuditRoutePhase(
                "AuditReadModelUnavailable",
                routeStopwatch.ElapsedMilliseconds,
                useBoundedInitialAuditLoad,
                shouldLoadUltraDebugLog,
                false,
                snapshot.Entries.Count);
        }
        else
        {
            snapshot = (await logCenterReadModelService.GetPageAsync(serviceRequest, cancellationToken)) with { Filters = request };
            LogAuditRoutePhase(
                "AuditLogCenterSnapshotLoaded",
                routeStopwatch.ElapsedMilliseconds,
                useBoundedInitialAuditLoad,
                shouldLoadUltraDebugLog,
                false,
                snapshot.Entries.Count);
        }

        var draftModel = AdminIncidentAuditDecisionCenterComposer.Compose(
            snapshot,
            normalizedOutcome,
            normalizedReasonCode,
            normalizedFocus,
            traceDetail: null,
            approvalDetail: null,
            incidentDetail: null,
            evaluatedAtUtc,
            allowImplicitSelection: !useBoundedInitialAuditLoad);
        LogAuditRoutePhase(
            "AuditDraftModelComposed",
            routeStopwatch.ElapsedMilliseconds,
            useBoundedInitialAuditLoad,
            shouldLoadUltraDebugLog,
            false,
            draftModel.Rows.Count);

        AdminTraceDetailSnapshot? traceDetail = null;
        ApprovalQueueDetailSnapshot? approvalDetail = null;
        IncidentDetailSnapshot? incidentDetail = null;
        var deepDetailsLoaded = false;

        if (ShouldLoadAuditDeepDetails(draftModel.Filters.FocusReference, draftModel.Detail))
        {
            traceDetail = await LoadAuditTraceDetailAsync(draftModel.Detail, cancellationToken);
            approvalDetail = await LoadAuditApprovalDetailAsync(draftModel.Detail, cancellationToken);
            incidentDetail = await LoadAuditIncidentDetailAsync(draftModel.Detail, cancellationToken);
            deepDetailsLoaded = true;
            LogAuditRoutePhase(
                "AuditDeepDetailsLoaded",
                routeStopwatch.ElapsedMilliseconds,
                useBoundedInitialAuditLoad,
                shouldLoadUltraDebugLog,
                deepDetailsLoaded,
                draftModel.Rows.Count);
        }

        var model = AdminIncidentAuditDecisionCenterComposer.Compose(
            snapshot,
            normalizedOutcome,
            normalizedReasonCode,
            normalizedFocus,
            traceDetail,
            approvalDetail,
            incidentDetail,
            evaluatedAtUtc,
            allowImplicitSelection: !useBoundedInitialAuditLoad) with
        {
            UltraDebugLog = shouldLoadUltraDebugLog
                ? await LoadUltraDebugLogViewModelAsync(
                    logBucket,
                    logCategory,
                    logSource,
                    logSearch,
                    logTimeWindow,
                    logTake,
                    cancellationToken)
                : null
        };
        LogAuditRoutePhase(
            "AuditModelReady",
            routeStopwatch.ElapsedMilliseconds,
            useBoundedInitialAuditLoad,
            shouldLoadUltraDebugLog,
            deepDetailsLoaded,
            model.Rows.Count);

        return View("Audit", model);
    }

    [HttpPost("/admin/audit-logs/ultra-log/enable")]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnableUltraDebugLog(
        string? durationKey,
        int? normalLogsLimitMb,
        int? ultraLogsLimitMb,
        CancellationToken cancellationToken)
    {
        var mfaResult = await EnforcePlatformAdminMfaAsync(
            "AuditLogs.UltraDebugLog.Enable",
            UltraDebugLogErrorTempDataKey,
            nameof(AuditLogs),
            new { area = "Admin" },
            cancellationToken);

        if (mfaResult is not null)
        {
            return mfaResult;
        }

        var normalizedDurationKey = NormalizeOptionalInput(durationKey, 16);
        var actorUserId = ResolveAdminUserId();

        if (string.IsNullOrWhiteSpace(normalizedDurationKey))
        {
            TempData[UltraDebugLogErrorTempDataKey] = "Aktivasyon başarısız: süre seçilmedi";
            await adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    actorUserId,
                    "ultra_log_enable_failed_validation",
                    "UltraDebugLog",
                    "singleton",
                    oldValueSummary: null,
                    newValueSummary: null,
                    "Ultra log activation failed because no duration was selected.",
                    HttpContext.TraceIdentifier),
                cancellationToken);
            return RedirectToAction(nameof(AuditLogs), new { area = "Admin", ultraLog = true });
        }

        if (!TryResolveUltraDebugLogSizeLimit(normalLogsLimitMb, out var normalizedNormalLogsLimitMb))
        {
            TempData[UltraDebugLogErrorTempDataKey] = "Aktivasyon başarısız: normal logs limiti seçilmedi";
            await adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    actorUserId,
                    "ultra_log_enable_failed_validation",
                    "UltraDebugLog",
                    "singleton",
                    oldValueSummary: null,
                    newValueSummary: null,
                    "Ultra log activation failed because no valid normal logs limit was selected.",
                    HttpContext.TraceIdentifier),
                cancellationToken);
            return RedirectToAction(nameof(AuditLogs), new { area = "Admin", ultraLog = true });
        }

        if (!TryResolveUltraDebugLogSizeLimit(ultraLogsLimitMb, out var normalizedUltraLogsLimitMb))
        {
            TempData[UltraDebugLogErrorTempDataKey] = "Aktivasyon başarısız: ultra logs limiti seçilmedi";
            await adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    actorUserId,
                    "ultra_log_enable_failed_validation",
                    "UltraDebugLog",
                    "singleton",
                    oldValueSummary: null,
                    newValueSummary: null,
                    "Ultra log activation failed because no valid ultra debug logs limit was selected.",
                    HttpContext.TraceIdentifier),
                cancellationToken);
            return RedirectToAction(nameof(AuditLogs), new { area = "Admin", ultraLog = true });
        }

        if (ultraDebugLogService is null)
        {
            TempData[UltraDebugLogErrorTempDataKey] = "Ultra log runtime hatası nedeniyle kapatıldı";
            return RedirectToAction(nameof(AuditLogs), new { area = "Admin", ultraLog = true });
        }

        try
        {
            await ultraDebugLogService.EnableAsync(
                new UltraDebugLogEnableRequest(
                    normalizedDurationKey,
                    actorUserId,
                    ResolveAdminEmail(),
                    ResolveMaskedRemoteIpAddress(),
                    ResolveMaskedUserAgent(),
                    HttpContext.TraceIdentifier,
                    normalizedNormalLogsLimitMb,
                    normalizedUltraLogsLimitMb),
                cancellationToken);

            TempData[UltraDebugLogSuccessTempDataKey] = "Ultra log aktif";
        }
        catch (UltraDebugLogOperationException)
        {
            TempData[UltraDebugLogErrorTempDataKey] = "Ultra log runtime hatası nedeniyle kapatıldı";
        }

        return RedirectToAction(nameof(AuditLogs), new { area = "Admin", ultraLog = true });
    }

    [HttpPost("/admin/audit-logs/ultra-log/disable")]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisableUltraDebugLog(CancellationToken cancellationToken)
    {
        var mfaResult = await EnforcePlatformAdminMfaAsync(
            "AuditLogs.UltraDebugLog.Disable",
            UltraDebugLogErrorTempDataKey,
            nameof(AuditLogs),
            new { area = "Admin" },
            cancellationToken);

        if (mfaResult is not null)
        {
            return mfaResult;
        }

        if (ultraDebugLogService is null)
        {
            TempData[UltraDebugLogErrorTempDataKey] = "Ultra log runtime hatası nedeniyle kapatıldı";
            return RedirectToAction(nameof(AuditLogs), new { area = "Admin", ultraLog = true });
        }

        await ultraDebugLogService.DisableAsync(
            new UltraDebugLogDisableRequest(
                ResolveAdminUserId(),
                ResolveAdminEmail(),
                ResolveMaskedRemoteIpAddress(),
                ResolveMaskedUserAgent(),
                HttpContext.TraceIdentifier,
                UltraDebugLogManualDisableReason),
            cancellationToken);

        TempData[UltraDebugLogSuccessTempDataKey] = "Admin tarafından kapatıldı";
        return RedirectToAction(nameof(AuditLogs), new { area = "Admin", ultraLog = true });
    }

    [HttpGet("/admin/audit-logs/ultra-log/export")]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    public async Task<IActionResult> ExportUltraDebugLog(
        string? logBucket = null,
        string? logCategory = null,
        string? logSource = null,
        string? logSearch = null,
        string? logTimeWindow = null,
        int maxRows = 200,
        bool zipPackage = false,
        CancellationToken cancellationToken = default)
    {
        if (ultraDebugLogService is null)
        {
            return StatusCode(503, "Masked log export unavailable.");
        }

        var nowUtc = DateTime.UtcNow;
        var normalizedTimeWindow = ResolveUltraDebugSearchTimeWindowKey(logTimeWindow);
        var fromUtc = ResolveUltraDebugSearchFromUtc(nowUtc, normalizedTimeWindow);

        try
        {
            var export = await ultraDebugLogService.ExportAsync(
                new UltraDebugLogExportRequest(
                    BucketName: logBucket ?? "all",
                    Category: logCategory,
                    Source: NormalizeOptionalInput(logSource, 128),
                    SearchTerm: NormalizeOptionalInput(logSearch, 128),
                    FromUtc: fromUtc,
                    ToUtc: nowUtc,
                    MaxRows: maxRows,
                    ZipPackage: zipPackage),
                cancellationToken);

            Response.Headers.CacheControl = "no-store, no-cache";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["X-Content-Type-Options"] = "nosniff";

            return File(export.Content, export.ContentType, export.FileDownloadName);
        }
        catch (UltraDebugLogOperationException)
        {
            return BadRequest("Masked log export request was rejected.");
        }
    }

    private static int NormalizeAuditTake(int take)
    {
        return take is >= 25 and <= 200
            ? take
            : 120;
    }

    private static int NormalizeAuditPage(int page)
    {
        return page >= 1 ? page : 1;
    }

    private static int NormalizeAuditPageSize(int pageSize, int take, bool hasExplicitPageSize)
    {
        if (hasExplicitPageSize)
        {
            return pageSize switch
            {
                <= 0 => 25,
                <= 10 => 10,
                <= 25 => 25,
                <= 50 => 50,
                _ => 100
            };
        }

        return take is 10 or 25 or 50 or 100 ? take : 25;
    }

    private static int ResolveAuditServiceTake(
        int normalizedTake,
        int page,
        int pageSize,
        string? outcome,
        string? reasonCode,
        bool hasExplicitTake,
        bool useBoundedInitialAuditLoad = false)
    {
        if (useBoundedInitialAuditLoad)
        {
            return Math.Clamp(Math.Min(normalizedTake, pageSize), 1, 25);
        }

        var requiredEntries = ((page - 1) * pageSize) + pageSize + 1;
        var multiplier = !string.IsNullOrWhiteSpace(outcome) || !string.IsNullOrWhiteSpace(reasonCode)
            ? 4
            : 1;
        var desiredTake = Math.Min(requiredEntries * multiplier, 1000);

        return hasExplicitTake
            ? Math.Clamp(Math.Max(normalizedTake, desiredTake), pageSize + 1, 1000)
            : Math.Clamp(desiredTake, pageSize + 1, 1000);
    }

    private static bool ShouldLoadUltraDebugLog(
        bool ultraLog,
        string? logBucket,
        string? logCategory,
        string? logSource,
        string? logSearch,
        string? logTimeWindow)
    {
        return ultraLog ||
               !string.IsNullOrWhiteSpace(logBucket) ||
               !string.IsNullOrWhiteSpace(logCategory) ||
               !string.IsNullOrWhiteSpace(logSource) ||
               !string.IsNullOrWhiteSpace(logSearch) ||
               !string.IsNullOrWhiteSpace(logTimeWindow);
    }

    private static bool ShouldUseBoundedInitialAuditLoad(
        string? query,
        string? correlationId,
        string? decisionId,
        string? executionAttemptId,
        string? userId,
        string? symbol,
        string? outcome,
        string? reasonCode,
        string? focusReference,
        int page,
        bool shouldLoadUltraDebugLog)
    {
        return page == 1 &&
               !shouldLoadUltraDebugLog &&
               string.IsNullOrWhiteSpace(query) &&
               string.IsNullOrWhiteSpace(correlationId) &&
               string.IsNullOrWhiteSpace(decisionId) &&
               string.IsNullOrWhiteSpace(executionAttemptId) &&
               string.IsNullOrWhiteSpace(userId) &&
               string.IsNullOrWhiteSpace(symbol) &&
               string.IsNullOrWhiteSpace(outcome) &&
               string.IsNullOrWhiteSpace(reasonCode) &&
               string.IsNullOrWhiteSpace(focusReference);
    }

    private static LogCenterPageSnapshot CreateLightweightAuditShellSnapshot(LogCenterQueryRequest request)
    {
        return new LogCenterPageSnapshot(
            request,
            new LogCenterSummarySnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, null),
            new LogCenterRetentionSnapshot(false, 0, 0, 0, 0, 0, 0, null, null),
            Array.Empty<LogCenterEntrySnapshot>(),
            false,
            null);
    }

    private void LogAuditRoutePhase(
        string phase,
        long elapsedMs,
        bool initialShell,
        bool ultraLogRequested,
        bool deepDetailsLoaded,
        int? rowCount)
    {
        logger?.LogInformation(
            "AuditRoutePhase phase={Phase} elapsedMs={ElapsedMs} initialShell={InitialShell} ultraLogRequested={UltraLogRequested} deepDetailsLoaded={DeepDetailsLoaded} rowCount={RowCount}",
            phase,
            elapsedMs,
            initialShell,
            ultraLogRequested,
            deepDetailsLoaded,
            rowCount);
    }

    private static bool ShouldUseFocusBackfillQuery(
        string? focusReference,
        string? query,
        string? correlationId,
        string? decisionId,
        string? executionAttemptId,
        string? userId,
        string? symbol)
    {
        return !string.IsNullOrWhiteSpace(focusReference) &&
               string.IsNullOrWhiteSpace(query) &&
               string.IsNullOrWhiteSpace(correlationId) &&
               string.IsNullOrWhiteSpace(decisionId) &&
               string.IsNullOrWhiteSpace(executionAttemptId) &&
               string.IsNullOrWhiteSpace(userId) &&
               string.IsNullOrWhiteSpace(symbol);
    }

    private static bool ShouldLoadAuditDeepDetails(
        string? focusReference,
        AdminIncidentAuditDecisionDetailViewModel detail)
    {
        if (!detail.HasSelection || string.IsNullOrWhiteSpace(focusReference))
        {
            return false;
        }

        return string.Equals(detail.Reference, focusReference, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(NormalizeUnavailableReference(detail.CorrelationId), focusReference, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(NormalizeUnavailableReference(detail.DecisionId), focusReference, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(NormalizeUnavailableReference(detail.ExecutionAttemptId), focusReference, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(NormalizeUnavailableReference(detail.IncidentReference), focusReference, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(NormalizeUnavailableReference(detail.ApprovalReference), focusReference, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<AdminTraceDetailSnapshot?> LoadAuditTraceDetailAsync(
        AdminIncidentAuditDecisionDetailViewModel detail,
        CancellationToken cancellationToken)
    {
        if (!detail.HasSelection ||
            string.Equals(detail.CorrelationId, "Unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return await traceService.GetDetailAsync(
            detail.CorrelationId,
            NormalizeUnavailableReference(detail.DecisionId),
            NormalizeUnavailableReference(detail.ExecutionAttemptId),
            cancellationToken);
    }

    private async Task<ApprovalQueueDetailSnapshot?> LoadAuditApprovalDetailAsync(
        AdminIncidentAuditDecisionDetailViewModel detail,
        CancellationToken cancellationToken)
    {
        if (approvalWorkflowService is null ||
            !detail.HasSelection ||
            string.Equals(detail.ApprovalReference, "Unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return await approvalWorkflowService.GetDetailAsync(detail.ApprovalReference, cancellationToken);
    }

    private async Task<IncidentDetailSnapshot?> LoadAuditIncidentDetailAsync(
        AdminIncidentAuditDecisionDetailViewModel detail,
        CancellationToken cancellationToken)
    {
        if (adminGovernanceReadModelService is null ||
            !detail.HasSelection ||
            string.Equals(detail.IncidentReference, "Unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return await adminGovernanceReadModelService.GetIncidentDetailAsync(detail.IncidentReference, cancellationToken);
    }

    private async Task<AdminUltraDebugLogViewModel> LoadUltraDebugLogViewModelAsync(
        string? logBucket,
        string? logCategory,
        string? logSource,
        string? logSearch,
        string? logTimeWindow,
        int logTake,
        CancellationToken cancellationToken)
    {
        var snapshot = ultraDebugLogService is null
            ? new UltraDebugLogSnapshot(false, null, null, null, null, null, null, null, false, null, null)
            : await ultraDebugLogService.GetSnapshotAsync(cancellationToken);
        var nowUtc = DateTime.UtcNow;
        var normalizedLogBucket = NormalizeUltraDebugSearchBucket(logBucket);
        var normalizedLogCategory = NormalizeUltraDebugSearchCategory(logCategory);
        var normalizedLogSource = NormalizeOptionalInput(logSource, 128);
        var normalizedLogSearch = NormalizeOptionalInput(logSearch, 128);
        var normalizedLogTimeWindow = ResolveUltraDebugSearchTimeWindowKey(logTimeWindow);
        var normalizedLogTake = NormalizeUltraDebugSearchTake(logTake);
        var fromUtc = ResolveUltraDebugSearchFromUtc(nowUtc, normalizedLogTimeWindow);
        var searchSnapshot = ultraDebugLogService is null
            ? new UltraDebugLogTailSnapshot(
                normalizedLogBucket,
                normalizedLogTake,
                0,
                0,
                false,
                Array.Empty<UltraDebugLogTailLineSnapshot>())
            : await ultraDebugLogService.SearchAsync(
                new UltraDebugLogSearchRequest(
                    normalizedLogBucket,
                    normalizedLogCategory,
                    normalizedLogSource,
                    normalizedLogSearch,
                    fromUtc,
                    normalizedLogTake),
                cancellationToken);
        var remaining = snapshot.IsEnabled && snapshot.ExpiresAtUtc.HasValue && snapshot.ExpiresAtUtc.Value > nowUtc
            ? snapshot.ExpiresAtUtc.Value - nowUtc
            : TimeSpan.Zero;
        var statusLabel = snapshot.IsEnabled ? "Açık" : "Kapalı";
        var statusTone = snapshot.IsEnabled
            ? "healthy"
            : string.Equals(snapshot.AutoDisabledReason, UltraDebugLogDurationExpiredReason, StringComparison.OrdinalIgnoreCase)
                ? "warning"
                : string.Equals(snapshot.AutoDisabledReason, UltraDebugLogDiskPressureReason, StringComparison.OrdinalIgnoreCase)
                    ? "critical"
                : string.Equals(snapshot.AutoDisabledReason, UltraDebugLogRuntimeWriteFailureReason, StringComparison.OrdinalIgnoreCase)
                    ? "critical"
                : string.Equals(snapshot.AutoDisabledReason, UltraDebugLogSizeLimitExceededReason, StringComparison.OrdinalIgnoreCase)
                    ? "critical"
                : string.Equals(snapshot.AutoDisabledReason, UltraDebugLogRuntimeErrorReason, StringComparison.OrdinalIgnoreCase)
                    ? "critical"
                    : "neutral";
        var statusMessage = snapshot.IsEnabled
            ? "Ultra log aktif"
            : string.Equals(snapshot.AutoDisabledReason, UltraDebugLogDurationExpiredReason, StringComparison.OrdinalIgnoreCase)
                ? "Süre dolduğu için kapandı"
                : string.Equals(snapshot.AutoDisabledReason, UltraDebugLogDiskPressureReason, StringComparison.OrdinalIgnoreCase)
                    ? "Disk baskısı nedeniyle kapandı"
                : string.Equals(snapshot.AutoDisabledReason, UltraDebugLogRuntimeWriteFailureReason, StringComparison.OrdinalIgnoreCase)
                    ? "Ultra log write failure nedeniyle kapandı"
                : string.Equals(snapshot.AutoDisabledReason, UltraDebugLogSizeLimitExceededReason, StringComparison.OrdinalIgnoreCase)
                    ? "Ultra log limit aşıldığı için kapandı"
                : string.Equals(snapshot.AutoDisabledReason, UltraDebugLogManualDisableReason, StringComparison.OrdinalIgnoreCase)
                    ? "Admin tarafından kapatıldı"
                    : string.Equals(snapshot.AutoDisabledReason, UltraDebugLogRuntimeErrorReason, StringComparison.OrdinalIgnoreCase)
                        ? "Ultra log runtime hatası nedeniyle kapatıldı"
                        : "Kapalı";

        return new AdminUltraDebugLogViewModel(
            snapshot.IsEnabled,
            statusLabel,
            statusTone,
            statusMessage,
            snapshot.EnabledByAdminEmail ?? snapshot.EnabledByAdminId ?? "Bilinmiyor",
            snapshot.StartedAtUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "n/a",
            snapshot.ExpiresAtUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "n/a",
            snapshot.IsEnabled ? FormatRemainingDuration(remaining) : "0 dk",
            ResolveUltraDebugDurationLabel(snapshot.DurationKey),
            ResolveUltraDebugLogSizeLimitLabel(snapshot.NormalLogsLimitMb),
            ResolveUltraDebugLogSizeLimitLabel(snapshot.UltraLogsLimitMb),
            ResolveUltraDebugUsageLabel(snapshot.NormalLogsUsageBytes, snapshot.NormalLogsLimitMb),
            ResolveUltraDebugUsageLabel(snapshot.UltraLogsUsageBytes, snapshot.UltraLogsLimitMb),
            ResolveUltraDebugDiskFreeSpaceLabel(snapshot.DiskFreeSpaceBytes),
            ResolveUltraDebugSafetyModeLabel(snapshot.IsNormalFallbackMode),
            ResolveUltraDebugReasonLabel(snapshot.AutoDisabledReason),
            (ultraDebugLogService?.GetDurationOptions() ?? GetUltraDebugDurationOptions())
                .Select(item => new AdminUltraDebugLogDurationOptionViewModel(
                    item.Key,
                    item.Label,
                    string.Equals(item.Key, snapshot.DurationKey, StringComparison.OrdinalIgnoreCase)))
                .ToArray(),
            (ultraDebugLogService?.GetLogSizeLimitOptions() ?? GetUltraDebugLogSizeLimitOptions())
                .Select(item => new AdminUltraDebugLogSizeLimitOptionViewModel(
                    item.ValueMb,
                    item.Label,
                    item.ValueMb == snapshot.NormalLogsLimitMb))
                .ToArray(),
            (ultraDebugLogService?.GetLogSizeLimitOptions() ?? GetUltraDebugLogSizeLimitOptions())
                .Select(item => new AdminUltraDebugLogSizeLimitOptionViewModel(
                    item.ValueMb,
                    item.Label,
                    item.ValueMb == snapshot.UltraLogsLimitMb))
                .ToArray(),
            MapUltraDebugStructuredEvent(snapshot.LatestStructuredEvent),
            snapshot.LatestCategoryEvents?
                .Select(MapUltraDebugStructuredEvent)
                .Where(item => item is not null)
                .Cast<AdminUltraDebugStructuredEventViewModel>()
                .ToArray(),
            MapUltraDebugTail(snapshot.NormalLogsTail, "Normal logs tail"),
            MapUltraDebugTail(snapshot.UltraLogsTail, "Ultra logs tail"),
            BuildUltraDebugLogSearchViewModel(
                normalizedLogBucket,
                normalizedLogCategory,
                normalizedLogSource,
                normalizedLogSearch,
                normalizedLogTimeWindow,
                normalizedLogTake,
                searchSnapshot));
    }

    private static string? NormalizeUnavailableReference(string? value)
    {
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, "Unavailable", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;
    }

    private static string ResolveUltraDebugDurationLabel(string? durationKey)
    {
        var option = GetUltraDebugDurationOptions()
            .SingleOrDefault(item => string.Equals(item.Key, durationKey, StringComparison.OrdinalIgnoreCase));

        return option is not null
            ? option.Label
            : "Seçilmedi";
    }

    private static bool TryResolveUltraDebugLogSizeLimit(int? valueMb, out int resolvedValueMb)
    {
        var option = GetUltraDebugLogSizeLimitOptions()
            .SingleOrDefault(item => item.ValueMb == valueMb);
        resolvedValueMb = option?.ValueMb ?? 0;
        return resolvedValueMb > 0;
    }

    private static string ResolveUltraDebugLogSizeLimitLabel(int? valueMb)
    {
        var option = GetUltraDebugLogSizeLimitOptions()
            .SingleOrDefault(item => item.ValueMb == valueMb);

        return option is not null
            ? option.Label
            : "Seçilmedi";
    }

    private static string ResolveUltraDebugUsageLabel(long usageBytes, int? limitMb)
    {
        var usageLabel = $"{Math.Round(usageBytes / (1024d * 1024d), 2):0.##} MB";
        return limitMb.HasValue && limitMb.Value > 0
            ? $"{usageLabel} / {limitMb.Value} MB"
            : $"{usageLabel} / Seçilmedi";
    }

    private static string ResolveUltraDebugDiskFreeSpaceLabel(long? bytes)
    {
        if (!bytes.HasValue)
        {
            return "n/a";
        }

        return $"{Math.Round(bytes.Value / (1024d * 1024d), 2):0.##} MB";
    }

    private static string ResolveUltraDebugSafetyModeLabel(bool isFallbackActive)
    {
        return isFallbackActive ? "Curated fallback aktif" : "Normal";
    }

    private static AdminUltraDebugStructuredEventViewModel? MapUltraDebugStructuredEvent(UltraDebugLogEventSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        return new AdminUltraDebugStructuredEventViewModel(
            CategoryLabel: ResolveUltraDebugCategoryLabel(snapshot.Category),
            EventName: snapshot.EventName,
            Summary: snapshot.Summary,
            OccurredAtUtcLabel: snapshot.OccurredAtUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "n/a",
            SymbolLabel: snapshot.Symbol ?? "n/a",
            TimeframeLabel: snapshot.Timeframe ?? "n/a",
            SourceLayerLabel: snapshot.SourceLayer ?? "n/a",
            DecisionReasonCodeLabel: snapshot.DecisionReasonCode ?? "n/a",
            BlockerCodeLabel: snapshot.BlockerCode ?? "n/a",
            LatencyBreakdownLabel: snapshot.LatencyBreakdownLabel ?? "n/a");
    }

    private static AdminUltraDebugTailViewModel? MapUltraDebugTail(UltraDebugLogTailSnapshot? snapshot, string bucketLabel)
    {
        if (snapshot is null)
        {
            return null;
        }

        var summaryLabel = $"{snapshot.ReturnedLineCount}/{snapshot.RequestedLineCount} satır · file={snapshot.FilesScanned}";
        return new AdminUltraDebugTailViewModel(
            BucketLabel: bucketLabel,
            SummaryLabel: summaryLabel,
            IsTruncated: snapshot.IsTruncated,
            Lines: snapshot.Lines
                .Select(MapUltraDebugTailLine)
                .ToArray());
    }

    private static AdminUltraDebugTailLineViewModel MapUltraDebugTailLine(UltraDebugLogTailLineSnapshot snapshot)
    {
        return new AdminUltraDebugTailLineViewModel(
            OccurredAtUtcLabel: snapshot.OccurredAtUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "n/a",
            CategoryLabel: ResolveUltraDebugCategoryLabel(snapshot.Category),
            BucketLabel: ResolveUltraDebugBucketLabel(snapshot.BucketLabel),
            SourceLabel: snapshot.Source ?? "n/a",
            EventName: snapshot.EventName,
            Summary: snapshot.Summary,
            SymbolLabel: snapshot.Symbol ?? "n/a",
            DetailPreview: snapshot.DetailPreview ?? "n/a",
            CorrelationIdLabel: snapshot.CorrelationId ?? "n/a",
            SourceFileLabel: snapshot.SourceFileName ?? "n/a");
    }

    private static AdminUltraDebugLogSearchViewModel BuildUltraDebugLogSearchViewModel(
        string selectedBucketValue,
        string? selectedCategoryValue,
        string? sourceFilter,
        string? searchTerm,
        string selectedTimeWindowValue,
        int selectedTake,
        UltraDebugLogTailSnapshot snapshot)
    {
        var normalizedCategoryValue = selectedCategoryValue ?? string.Empty;
        return new AdminUltraDebugLogSearchViewModel(
            SelectedBucketValue: selectedBucketValue,
            SelectedCategoryValue: normalizedCategoryValue,
            SelectedTimeWindowValue: selectedTimeWindowValue,
            SelectedTake: selectedTake,
            SourceFilter: sourceFilter ?? string.Empty,
            SearchTerm: searchTerm ?? string.Empty,
            HasActiveFilters:
                !string.Equals(selectedBucketValue, "all", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(normalizedCategoryValue) ||
                !string.IsNullOrWhiteSpace(sourceFilter) ||
                !string.IsNullOrWhiteSpace(searchTerm) ||
                !string.Equals(selectedTimeWindowValue, "1h", StringComparison.OrdinalIgnoreCase) ||
                selectedTake != UltraDebugLogSearchDefaultTake,
            PerformanceGuardLabel: $"Hard cap: {UltraDebugLogSearchMaxTake} satır · {UltraDebugLogSearchMaxFiles} dosya · {UltraDebugLogSearchWindowKilobytes} KB/dosya · masked arama",
            BucketOptions: GetUltraDebugSearchBucketOptions(selectedBucketValue),
            CategoryOptions: GetUltraDebugSearchCategoryOptions(normalizedCategoryValue),
            TimeWindowOptions: GetUltraDebugSearchTimeWindowOptions(selectedTimeWindowValue),
            TakeOptions: GetUltraDebugSearchTakeOptions(selectedTake),
            SearchResult: MapUltraDebugTail(
                snapshot,
                $"Filtered preview · {ResolveUltraDebugBucketLabel(selectedBucketValue)} · {ResolveUltraDebugSearchTimeWindowLabel(selectedTimeWindowValue)}")!);
    }

    private static string ResolveUltraDebugCategoryLabel(string category)
    {
        return category switch
        {
            "scanner" => "Scanner",
            "strategy" => "Strategy",
            "handoff" => "Handoff",
            "execution" => "Execution",
            "exchange" => "Exchange",
            "runtime" => "Runtime",
            _ => category
        };
    }

    private static string ResolveUltraDebugBucketLabel(string? bucketName)
    {
        return bucketName?.Trim().ToLowerInvariant() switch
        {
            "normal" => "Normal bucket",
            "ultra_debug" => "Ultra bucket",
            "all" => "Tüm bucket'lar",
            _ => "Tüm bucket'lar"
        };
    }

    private static string NormalizeUltraDebugSearchBucket(string? bucketName)
    {
        return bucketName?.Trim().ToLowerInvariant() switch
        {
            "normal" => "normal",
            "ultra_debug" => "ultra_debug",
            _ => "all"
        };
    }

    private static string? NormalizeUltraDebugSearchCategory(string? category)
    {
        var normalizedCategory = NormalizeOptionalInput(category, 32)?.ToLowerInvariant();
        return !string.IsNullOrWhiteSpace(normalizedCategory) &&
               UltraDebugLogSearchCategories.Contains(normalizedCategory, StringComparer.OrdinalIgnoreCase)
            ? normalizedCategory
            : null;
    }

    private static int NormalizeUltraDebugSearchTake(int take)
    {
        return take is >= 1 and <= UltraDebugLogSearchMaxTake
            ? take
            : UltraDebugLogSearchDefaultTake;
    }

    private static string ResolveUltraDebugSearchTimeWindowKey(string? timeWindow)
    {
        return timeWindow?.Trim().ToLowerInvariant() switch
        {
            "15m" => "15m",
            "1h" => "1h",
            "3h" => "3h",
            "6h" => "6h",
            "24h" => "24h",
            "7d" => "7d",
            _ => "1h"
        };
    }

    private static DateTime ResolveUltraDebugSearchFromUtc(DateTime nowUtc, string timeWindowKey)
    {
        return timeWindowKey switch
        {
            "15m" => nowUtc.AddMinutes(-15),
            "3h" => nowUtc.AddHours(-3),
            "6h" => nowUtc.AddHours(-6),
            "24h" => nowUtc.AddHours(-24),
            "7d" => nowUtc.AddDays(-7),
            _ => nowUtc.AddHours(-1)
        };
    }

    private static string ResolveUltraDebugSearchTimeWindowLabel(string timeWindowKey)
    {
        return timeWindowKey switch
        {
            "15m" => "15 dk",
            "3h" => "3 saat",
            "6h" => "6 saat",
            "24h" => "24 saat",
            "7d" => "7 gün",
            _ => "1 saat"
        };
    }

    private static IReadOnlyCollection<AdminUltraDebugLogFilterOptionViewModel> GetUltraDebugSearchBucketOptions(string selectedValue)
    {
        return
        [
            new AdminUltraDebugLogFilterOptionViewModel("all", "Tüm bucket'lar", string.Equals(selectedValue, "all", StringComparison.OrdinalIgnoreCase)),
            new AdminUltraDebugLogFilterOptionViewModel("normal", "Normal bucket", string.Equals(selectedValue, "normal", StringComparison.OrdinalIgnoreCase)),
            new AdminUltraDebugLogFilterOptionViewModel("ultra_debug", "Ultra bucket", string.Equals(selectedValue, "ultra_debug", StringComparison.OrdinalIgnoreCase))
        ];
    }

    private static IReadOnlyCollection<AdminUltraDebugLogFilterOptionViewModel> GetUltraDebugSearchCategoryOptions(string selectedValue)
    {
        return
        [
            new AdminUltraDebugLogFilterOptionViewModel(string.Empty, "Tüm category'ler", string.IsNullOrWhiteSpace(selectedValue)),
            new AdminUltraDebugLogFilterOptionViewModel("scanner", "Scanner", string.Equals(selectedValue, "scanner", StringComparison.OrdinalIgnoreCase)),
            new AdminUltraDebugLogFilterOptionViewModel("strategy", "Strategy", string.Equals(selectedValue, "strategy", StringComparison.OrdinalIgnoreCase)),
            new AdminUltraDebugLogFilterOptionViewModel("handoff", "Handoff", string.Equals(selectedValue, "handoff", StringComparison.OrdinalIgnoreCase)),
            new AdminUltraDebugLogFilterOptionViewModel("execution", "Execution", string.Equals(selectedValue, "execution", StringComparison.OrdinalIgnoreCase)),
            new AdminUltraDebugLogFilterOptionViewModel("exchange", "Exchange", string.Equals(selectedValue, "exchange", StringComparison.OrdinalIgnoreCase)),
            new AdminUltraDebugLogFilterOptionViewModel("runtime", "Runtime", string.Equals(selectedValue, "runtime", StringComparison.OrdinalIgnoreCase))
        ];
    }

    private static IReadOnlyCollection<AdminUltraDebugLogFilterOptionViewModel> GetUltraDebugSearchTimeWindowOptions(string selectedValue)
    {
        return
        [
            new AdminUltraDebugLogFilterOptionViewModel("15m", "15 dk", string.Equals(selectedValue, "15m", StringComparison.OrdinalIgnoreCase)),
            new AdminUltraDebugLogFilterOptionViewModel("1h", "1 saat", string.Equals(selectedValue, "1h", StringComparison.OrdinalIgnoreCase)),
            new AdminUltraDebugLogFilterOptionViewModel("3h", "3 saat", string.Equals(selectedValue, "3h", StringComparison.OrdinalIgnoreCase)),
            new AdminUltraDebugLogFilterOptionViewModel("6h", "6 saat", string.Equals(selectedValue, "6h", StringComparison.OrdinalIgnoreCase)),
            new AdminUltraDebugLogFilterOptionViewModel("24h", "24 saat", string.Equals(selectedValue, "24h", StringComparison.OrdinalIgnoreCase)),
            new AdminUltraDebugLogFilterOptionViewModel("7d", "7 gün", string.Equals(selectedValue, "7d", StringComparison.OrdinalIgnoreCase))
        ];
    }

    private static IReadOnlyCollection<AdminUltraDebugLogTakeOptionViewModel> GetUltraDebugSearchTakeOptions(int selectedValue)
    {
        return
        [
            new AdminUltraDebugLogTakeOptionViewModel(10, "10", selectedValue == 10),
            new AdminUltraDebugLogTakeOptionViewModel(25, "25", selectedValue == 25),
            new AdminUltraDebugLogTakeOptionViewModel(50, "50", selectedValue == 50)
        ];
    }

    private static string ResolveUltraDebugReasonLabel(string? reasonCode)
    {
        return reasonCode switch
        {
            UltraDebugLogManualDisableReason => "Admin tarafından kapatıldı",
            UltraDebugLogDurationExpiredReason => "Süre doldu",
            UltraDebugLogDiskPressureReason => "Disk pressure",
            UltraDebugLogRuntimeWriteFailureReason => "Runtime write failure",
            UltraDebugLogRuntimeErrorReason => "Runtime hatası",
            UltraDebugLogSizeLimitExceededReason => "Ultra logs limiti aşıldı",
            null => "none",
            "" => "none",
            _ => reasonCode
        };
    }

    private static IReadOnlyCollection<UltraDebugLogDurationOption> GetUltraDebugDurationOptions()
    {
        return
        [
            new UltraDebugLogDurationOption("1h", "1 saat", TimeSpan.FromHours(1)),
            new UltraDebugLogDurationOption("3h", "3 saat", TimeSpan.FromHours(3)),
            new UltraDebugLogDurationOption("5h", "5 saat", TimeSpan.FromHours(5)),
            new UltraDebugLogDurationOption("8h", "8 saat", TimeSpan.FromHours(8)),
            new UltraDebugLogDurationOption("12h", "12 saat", TimeSpan.FromHours(12)),
            new UltraDebugLogDurationOption("1d", "1 gün", TimeSpan.FromDays(1)),
            new UltraDebugLogDurationOption("3d", "3 gün", TimeSpan.FromDays(3)),
            new UltraDebugLogDurationOption("5d", "5 gün", TimeSpan.FromDays(5)),
            new UltraDebugLogDurationOption("7d", "7 gün", TimeSpan.FromDays(7))
        ];
    }

    private static IReadOnlyCollection<UltraDebugLogSizeLimitOption> GetUltraDebugLogSizeLimitOptions()
    {
        return
        [
            new UltraDebugLogSizeLimitOption(128, "128 MB"),
            new UltraDebugLogSizeLimitOption(256, "256 MB"),
            new UltraDebugLogSizeLimitOption(512, "512 MB"),
            new UltraDebugLogSizeLimitOption(1024, "1024 MB"),
            new UltraDebugLogSizeLimitOption(2048, "2048 MB"),
            new UltraDebugLogSizeLimitOption(4096, "4096 MB")
        ];
    }

    private static string FormatRemainingDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "0 dk";
        }

        if (duration.TotalDays >= 1d)
        {
            return $"{Math.Floor(duration.TotalDays):0} gün";
        }

        if (duration.TotalHours >= 1d)
        {
            return $"{Math.Floor(duration.TotalHours):0} saat";
        }

        return $"{Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes)):0} dk";
    }

    [Authorize(Policy = ApplicationPolicies.AuditRead)]
    [HttpGet("/admin/trace")]
    public Task<IActionResult> Trace(
        [FromQuery] string? correlationId,
        [FromQuery] string? decisionId,
        [FromQuery] string? executionAttemptId,
        CancellationToken cancellationToken)
    {
        var normalizedCorrelationId = NormalizeOptionalInput(correlationId, 128);
        var normalizedDecisionId = NormalizeOptionalInput(decisionId, 64);
        var normalizedExecutionAttemptId = NormalizeOptionalInput(executionAttemptId, 64);

        if (string.IsNullOrWhiteSpace(normalizedCorrelationId))
        {
            return Task.FromResult<IActionResult>(RedirectToAction(
                nameof(Audit),
                new
                {
                    area = "Admin",
                    page = 1,
                    pageSize = 25,
                    take = 25,
                    ultraLog = false
                })!);
        }

        return TraceDetail(normalizedCorrelationId, normalizedDecisionId, normalizedExecutionAttemptId, cancellationToken);
    }

    [Authorize(Policy = ApplicationPolicies.AuditRead)]
    [HttpGet("/admin/trace/detail")]
    public async Task<IActionResult> TraceDetail(
        [FromQuery] string? correlationId,
        [FromQuery] string? decisionId,
        [FromQuery] string? executionAttemptId,
        CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Trace Detail",
            description: "CorrelationId bazli signal, decision ve execution zincirini detay seviyesinde izleyen masked admin detail ekranı.",
            activeNav: "Audit",
            breadcrumbItems: new[] { "Super Admin", "Gözlem", "Trace Detail" });

        var normalizedCorrelationId = NormalizeOptionalInput(correlationId, 128);
        if (string.IsNullOrWhiteSpace(normalizedCorrelationId))
        {
            return BadRequest("Trace detail requires correlationId.");
        }

        var detail = await traceService.GetDetailAsync(
            normalizedCorrelationId,
            NormalizeOptionalInput(decisionId, 64),
            NormalizeOptionalInput(executionAttemptId, 64),
            cancellationToken);

        return detail is null
            ? NotFound()
            : View("TraceDetail", detail);
    }

    [Authorize(Policy = ApplicationPolicies.AuditRead)]
    public async Task<IActionResult> Approvals(
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        ApplyShellMeta(
            title: "Approval Merkezi",
            description: "Pending approval kuyruklarini, dual approval adimlarini ve audit-linked governance aksiyonlarini merkezde izleyen admin ekranı.",
            activeNav: "Approvals",
            breadcrumbItems: new[] { "Super Admin", "Governance", "Approval Merkezi" });

        ViewData["AdminCanManageApprovals"] = CanManageApprovals();
        var approvals = approvalWorkflowService is null
            ? Array.Empty<ApprovalQueueListItem>()
            : await approvalWorkflowService.ListPendingAsync(take, cancellationToken);

        return View(approvals);
    }

    [Authorize(Policy = ApplicationPolicies.AuditRead)]
    public async Task<IActionResult> ApprovalDetail(
        string approvalReference,
        CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Approval Detail",
            description: "Approval queue item, action history ve ilgili incident/trace baglantilarini tek detay sayfasinda sunar.",
            activeNav: "Approvals",
            breadcrumbItems: new[] { "Super Admin", "Governance", "Approval Detail" });

        ViewData["AdminCanManageApprovals"] = CanManageApprovals();
        if (approvalWorkflowService is null)
        {
            return NotFound();
        }

        var detail = await approvalWorkflowService.GetDetailAsync(approvalReference, cancellationToken);
        return detail is null ? NotFound() : View(detail);
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveApproval(
        string approvalReference,
        string? reason,
        CancellationToken cancellationToken)
    {
        var mfaResult = await EnforcePlatformAdminMfaAsync(
            "Admin.Approvals.Approve",
            ApprovalErrorTempDataKey,
            nameof(ApprovalDetail),
            new { approvalReference },
            cancellationToken);

        if (mfaResult is not null)
        {
            return mfaResult;
        }

        if (approvalWorkflowService is null)
        {
            TempData[ApprovalErrorTempDataKey] = "Approval workflow service unavailable.";
            return RedirectToAction(nameof(Approvals));
        }

        var normalizedReason = NormalizeOptionalInput(reason, 512);

        try
        {
            var detail = await approvalWorkflowService.ApproveAsync(
                new ApprovalQueueDecisionRequest(
                    approvalReference,
                    ResolveAdminUserId(),
                    normalizedReason,
                    HttpContext.TraceIdentifier),
                cancellationToken);

            TempData[ApprovalSuccessTempDataKey] = detail.Status == ApprovalQueueStatus.Executed
                ? $"Approval {detail.ApprovalReference} executed."
                : $"Approval {detail.ApprovalReference} recorded.";
        }
        catch (Exception exception)
        {
            TempData[ApprovalErrorTempDataKey] = exception.Message;
        }

        return RedirectToAction(nameof(ApprovalDetail), new { approvalReference });
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectApproval(
        string approvalReference,
        string? reason,
        CancellationToken cancellationToken)
    {
        var mfaResult = await EnforcePlatformAdminMfaAsync(
            "Admin.Approvals.Reject",
            ApprovalErrorTempDataKey,
            nameof(ApprovalDetail),
            new { approvalReference },
            cancellationToken);

        if (mfaResult is not null)
        {
            return mfaResult;
        }

        if (approvalWorkflowService is null)
        {
            TempData[ApprovalErrorTempDataKey] = "Approval workflow service unavailable.";
            return RedirectToAction(nameof(Approvals));
        }

        var normalizedReason = NormalizeRequiredReason(reason);

        if (normalizedReason is null)
        {
            TempData[ApprovalErrorTempDataKey] = "Reject reason zorunludur.";
            return RedirectToAction(nameof(ApprovalDetail), new { approvalReference });
        }

        try
        {
            await approvalWorkflowService.RejectAsync(
                new ApprovalQueueDecisionRequest(
                    approvalReference,
                    ResolveAdminUserId(),
                    normalizedReason,
                    HttpContext.TraceIdentifier),
                cancellationToken);

            TempData[ApprovalErrorTempDataKey] = $"Approval {approvalReference} rejected.";
        }
        catch (Exception exception)
        {
            TempData[ApprovalErrorTempDataKey] = exception.Message;
        }

        return RedirectToAction(nameof(ApprovalDetail), new { approvalReference });
    }

    [Authorize(Policy = ApplicationPolicies.AuditRead)]
    public async Task<IActionResult> Incidents(
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        ApplyShellMeta(
            title: "Incident Merkezi",
            description: "Approval, crisis ve governance olaylarini timeline odakli incident read-model ile izleyen operasyon ekranı.",
            activeNav: "Incidents",
            breadcrumbItems: new[] { "Super Admin", "Governance", "Incidents" });

        var incidents = adminGovernanceReadModelService is null
            ? Array.Empty<IncidentListItem>()
            : await adminGovernanceReadModelService.ListIncidentsAsync(take, cancellationToken);

        return View(incidents);
    }

    [Authorize(Policy = ApplicationPolicies.AuditRead)]
    public async Task<IActionResult> IncidentDetail(
        string incidentReference,
        CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Incident Detail",
            description: "Incident timeline, approval/reject eventleri ve related trace linkleri tek sayfada toplar.",
            activeNav: "Incidents",
            breadcrumbItems: new[] { "Super Admin", "Governance", "Incident Detail" });

        if (adminGovernanceReadModelService is null)
        {
            return NotFound();
        }

        var detail = await adminGovernanceReadModelService.GetIncidentDetailAsync(incidentReference, cancellationToken);
        return detail is null ? NotFound() : View(detail);
    }

    [Authorize(Policy = ApplicationPolicies.AuditRead)]
    public async Task<IActionResult> ConfigHistory(CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Config History",
            description: "Global policy version history ve rollback zincirini tek read-only tarihte gösteren admin ekranı.",
            activeNav: "ConfigHistory",
            breadcrumbItems: new[] { "Super Admin", "Governance", "Config History" });

        ViewData["AdminCanEditGlobalPolicy"] = CanEditGlobalPolicy();
        ViewData["AdminGlobalPolicySnapshot"] = await LoadGlobalPolicySnapshotAsync(cancellationToken);
        return View();
    }

    [Authorize(Policy = ApplicationPolicies.AuditRead)]
    public async Task<IActionResult> ConfigHistoryDetail(
        int version,
        CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Config History Detail",
            description: "Seçili policy version için diff, rollback kaydi ve snapshot detaylarini gösteren inceleme sayfası.",
            activeNav: "ConfigHistory",
            breadcrumbItems: new[] { "Super Admin", "Governance", "Config History Detail" });

        if (globalPolicyEngine is null)
        {
            return NotFound();
        }

        var snapshot = await LoadGlobalPolicySnapshotAsync(cancellationToken);
        var selectedVersion = snapshot.Versions.FirstOrDefault(item => item.Version == version);

        if (selectedVersion is null)
        {
            return NotFound();
        }

        ViewData["AdminSelectedConfigVersion"] = selectedVersion;
        ViewData["AdminCanEditGlobalPolicy"] = CanEditGlobalPolicy();
        return View(snapshot);
    }

    [Authorize(Policy = ApplicationPolicies.AuditRead)]
    public async Task<IActionResult> SystemStateHistory(
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        ApplyShellMeta(
            title: "System State History",
            description: "Global system state write history, breaker bağlantıları ve audit-linked state transitions için timeline merkezidir.",
            activeNav: "SystemStateHistory",
            breadcrumbItems: new[] { "Super Admin", "Governance", "System State History" });

        var history = adminGovernanceReadModelService is null
            ? Array.Empty<SystemStateHistoryListItem>()
            : await adminGovernanceReadModelService.ListSystemStateHistoryAsync(take, cancellationToken);

        return View(history);
    }

    [Authorize(Policy = ApplicationPolicies.AuditRead)]
    public async Task<IActionResult> SystemStateHistoryDetail(
        string historyReference,
        CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "System State History Detail",
            description: "Seçili state history kaydının command, approval, incident ve breaker linklerini detaylandırır.",
            activeNav: "SystemStateHistory",
            breadcrumbItems: new[] { "Super Admin", "Governance", "System State History Detail" });

        if (adminGovernanceReadModelService is null)
        {
            return NotFound();
        }

        var detail = await adminGovernanceReadModelService.GetSystemStateHistoryDetailAsync(historyReference, cancellationToken);
        return detail is null ? NotFound() : View(detail);
    }

    [Authorize(Policy = ApplicationPolicies.AuditRead)]
    public async Task<IActionResult> SecurityEvents(
        string? query,
        string? severity,
        string? module,
        CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Güvenlik Olayları",
            description: "Failed login, invalid MFA, suspicious session ve risky permission sinyallerini triage paneli ve olay listesiyle toplayan admin security monitoring yüzeyi.",
            activeNav: "SecurityEvents",
            breadcrumbItems: new[] { "Super Admin", "Gözlem", "Güvenlik Olayları" });

        ViewData["AdminSecurityEventsPageSnapshot"] = await adminWorkspaceReadModelService.GetSecurityEventsAsync(query, severity, module, cancellationToken);
        return View();
    }

    [Authorize(Policy = ApplicationPolicies.AuditRead)]
    public async Task<IActionResult> Notifications(
        string? severity,
        string? category,
        CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Bildirim / Alarm Merkezi",
            description: "Platform seviyesindeki kritik alarmları, severity-kategori filtrelerini ve incident/health durumlarını merkezi bir alarm hub üzerinde toplar.",
            activeNav: "Notifications",
            breadcrumbItems: new[] { "Super Admin", "Gözlem", "Bildirim / Alarm" });

        ViewData["AdminNotificationsPageSnapshot"] = await adminWorkspaceReadModelService.GetNotificationsAsync(severity, category, cancellationToken);
        return View();
    }

    public async Task<IActionResult> SupportTools(string? query, CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Destek Araçları",
            description: "Kullanıcı arama, read-only diagnostic panel, kritik olay özeti ve güvenli destek CTA'larını tek ekranda toplar.",
            activeNav: "SupportTools",
            breadcrumbItems: new[] { "Super Admin", "Platform", "Destek Araçları" });

        ViewData["AdminSupportLookupPageSnapshot"] = await adminWorkspaceReadModelService.GetSupportLookupAsync(query, cancellationToken);
        return View();
    }

    [Authorize(Policy = ApplicationPolicies.AdminPortalAccess)]
    public async Task<IActionResult> Settings(CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Global Ayarlar",
            description: "Feature flag, retention, AI rollout, maintenance ve varsayılan politika alanlarını kullanıcı ayarlarından ayrı toplar.",
            activeNav: "Settings",
            breadcrumbItems: new[] { "Super Admin", "Platform", "Global Ayarlar" });

        var operationalContext = await LoadSettingsOperationalContextAsync(cancellationToken);

        ViewData[ExecutionSwitchSnapshotViewDataKey] = operationalContext.ExecutionSnapshot;
        ViewData[GlobalSystemStateSnapshotViewDataKey] = operationalContext.GlobalSystemStateSnapshot;
        ViewData[GlobalPolicySnapshotViewDataKey] = await LoadGlobalPolicySnapshotAsync(cancellationToken);
        if (logCenterRetentionService is not null)
        {
            try
            {
                ViewData[AdminLogCenterRetentionSnapshotViewDataKey] = await logCenterRetentionService.GetSnapshotAsync(cancellationToken);
            }
            catch
            {
                ViewData[AdminLogCenterRetentionSnapshotViewDataKey] = null;
            }
        }

        ViewData[ClockDriftSnapshotViewDataKey] = operationalContext.ClockDriftViewModel;
        ViewData[DriftGuardSnapshotViewDataKey] = operationalContext.DriftGuardViewModel;
        ViewData[CanRefreshClockDriftViewDataKey] = CanRefreshClockDrift();
        ViewData[CrisisPreviewViewDataKey] = LoadCrisisPreviewViewModelFromTempData();
        ViewData[PilotOrderNotionalSummaryViewDataKey] = operationalContext.PilotOrderNotionalSummary;
        ViewData[PilotOrderNotionalToneViewDataKey] = operationalContext.PilotOrderNotionalTone;
        ViewData[LongRegimePolicyStatusViewDataKey] = operationalContext.LongRegimePolicyStatus;
        ViewData[LongRegimePolicyToneViewDataKey] = operationalContext.LongRegimePolicyTone;
        ViewData[LongRegimePolicyDetailViewDataKey] = operationalContext.LongRegimePolicyDetail;
        ViewData[AdminCanEditGlobalPolicyViewDataKey] = CanEditGlobalPolicy();

        return View(operationalContext.ActivationControlCenter);
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshClockDrift(string? returnTarget = null, CancellationToken cancellationToken = default)
    {
        var redirectAction = ResolveOperationalFlowReturnAction(returnTarget);
        var refreshedSnapshot = await binanceTimeSyncService.GetSnapshotAsync(forceRefresh: true, cancellationToken);

        if (string.Equals(refreshedSnapshot.StatusCode, "Synchronized", StringComparison.OrdinalIgnoreCase))
        {
            TempData[ClockDriftSuccessTempDataKey] =
                $"Binance server time sync yenilendi. Son probe drift {refreshedSnapshot.ClockDriftMilliseconds?.ToString() ?? "n/a"} ms. Market heartbeat drift guard snapshot'i ayri izlenir.";
        }
        else
        {
            TempData[ClockDriftErrorTempDataKey] =
                refreshedSnapshot.FailureReason ??
                "Binance server time sync yenilenemedi. Son basarili offset kullanılmaya devam ediyor.";
        }

        return RedirectToAction(redirectAction);
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConnectBinanceForSetup(
        bool isDemo,
        string? apiKey,
        string? apiSecret,
        string? connectionName,
        string? setupCommand,
        CancellationToken cancellationToken = default)
    {
        if (userExchangeCommandCenterService is null)
        {
            TempData[SetupErrorTempDataKey] = "Sistem hazir degil";
            return RedirectToAction(nameof(Overview));
        }

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
        {
            TempData[SetupErrorTempDataKey] = "Exchange bagli degil";
            return RedirectToAction(nameof(Overview));
        }

        if (string.Equals(setupCommand, "test", StringComparison.OrdinalIgnoreCase))
        {
            await TestBinanceForSetupAsync(apiKey, apiSecret, isDemo, cancellationToken);
            return RedirectToAction(nameof(Overview));
        }

        try
        {
            var result = await userExchangeCommandCenterService.ConnectBinanceAsync(
                new ConnectUserBinanceCredentialRequest(
                    ResolveAdminUserId(),
                    null,
                    apiKey,
                    apiSecret,
                    isDemo ? ExecutionEnvironment.Demo : ExecutionEnvironment.Live,
                    ExchangeTradeModeSelection.Futures,
                    ResolveExecutionActor(),
                    HttpContext.TraceIdentifier,
                    connectionName),
                cancellationToken);

            TempData[result.IsValid ? SetupSuccessTempDataKey : SetupErrorTempDataKey] =
                BuildSetupConnectionMessage(result);
        }
        catch (ArgumentException)
        {
            TempData[SetupErrorTempDataKey] = "Exchange bagli degil";
        }
        catch (InvalidOperationException)
        {
            TempData[SetupErrorTempDataKey] = "Sistem hazir degil";
        }
        catch
        {
            TempData[SetupErrorTempDataKey] = "API erisimi basarisiz";
        }

        return RedirectToAction(nameof(Overview));
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ActivateSystem(
        string? reason,
        string? commandId,
        string? reauthToken,
        string? returnTarget = null,
        CancellationToken cancellationToken = default)
    {
        var redirectAction = ResolveOperationalFlowReturnAction(returnTarget);
        var mfaResult = await EnforcePlatformAdminMfaAsync(
            "Admin.Settings.Activation.Activate",
            ExecutionSwitchErrorTempDataKey,
            redirectAction,
            null,
            cancellationToken);

        if (mfaResult is not null)
        {
            return mfaResult;
        }

        if (!CanEditGlobalPolicy())
        {
            TempData[ExecutionSwitchErrorTempDataKey] = "ActivationRoleReadOnly: Bu rolde sistem aktivasyonu uygulanamaz.";
            return RedirectToAction(redirectAction);
        }

        var confirmationResult = EnforceCriticalActionConfirmation(
            reauthToken,
            ExecutionSwitchErrorTempDataKey,
            redirectAction,
            null);

        if (confirmationResult is not null)
        {
            return confirmationResult;
        }

        var normalizedReason = NormalizeRequiredReason(reason);

        if (normalizedReason is null)
        {
            TempData[ExecutionSwitchErrorTempDataKey] = "Audit reason zorunludur.";
            return RedirectToAction(redirectAction);
        }

        var actorUserId = ResolveAdminUserId();
        var correlationId = HttpContext.TraceIdentifier;
        var resolvedCommandId = ResolveCommandId(commandId);
        var operationalContext = await LoadSettingsOperationalContextAsync(cancellationToken);
        var requestedSummary = BuildActivationControlCenterSummary(operationalContext.ActivationControlCenter);
        var payloadHash = CreatePayloadHash($"ActivateSystem|{normalizedReason}|{operationalContext.ActivationControlCenter.LastDecision.Code}");
        var commandStartResult = await adminCommandRegistry.TryStartAsync(
            new AdminCommandStartRequest(
                resolvedCommandId,
                "Admin.Settings.Activation.Activate",
                actorUserId,
                "ActivationControlCenter.System",
                payloadHash,
                correlationId),
            cancellationToken);

        if (!await HandleCommandStartResultAsync(
                commandStartResult,
                actorUserId,
                "Admin.Settings.Activation.Activate",
                "ActivationControlCenter",
                "System",
                requestedSummary,
                normalizedReason,
                ExecutionSwitchSuccessTempDataKey,
                ExecutionSwitchErrorTempDataKey,
                cancellationToken))
        {
            return RedirectToAction(redirectAction);
        }

        if (!operationalContext.ActivationControlCenter.IsActivatable)
        {
            var blockedMessage = BuildActivationControlFailureMessage(operationalContext.ActivationControlCenter);

            await adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    actorUserId,
                    "Admin.Settings.Activation.ActivateBlocked",
                    "ActivationControlCenter",
                    "System",
                    BuildExecutionSwitchSummary(operationalContext.ExecutionSnapshot),
                    requestedSummary,
                    normalizedReason,
                    correlationId),
                cancellationToken);

            await adminCommandRegistry.CompleteAsync(
                new AdminCommandCompletionRequest(
                    resolvedCommandId,
                    payloadHash,
                    AdminCommandStatus.Failed,
                    blockedMessage,
                    correlationId),
                cancellationToken);

            TempData[ExecutionSwitchErrorTempDataKey] = blockedMessage;
            return RedirectToAction(redirectAction);
        }

        try
        {
            var updatedSnapshot = await globalExecutionSwitchService.SetTradeMasterStateAsync(
                TradeMasterSwitchState.Armed,
                ResolveExecutionActor(),
                BuildSwitchContext("ActivationCenter.Activate", normalizedReason, resolvedCommandId),
                correlationId,
                cancellationToken);
            var successMessage = operationalContext.ExecutionSnapshot.IsTradeMasterArmed
                ? "Sistem zaten armed durumdaydi; activation center allow karari korunuyor."
                : $"Sistem aktive edildi. Mode={updatedSnapshot.EffectiveEnvironment}; Decision={operationalContext.ActivationControlCenter.LastDecision.Code}.";

            await adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    actorUserId,
                    "Admin.Settings.Activation.Activate",
                    "ActivationControlCenter",
                    "System",
                    BuildExecutionSwitchSummary(operationalContext.ExecutionSnapshot),
                    BuildExecutionSwitchSummary(updatedSnapshot),
                    normalizedReason,
                    correlationId),
                cancellationToken);

            await adminCommandRegistry.CompleteAsync(
                new AdminCommandCompletionRequest(
                    resolvedCommandId,
                    payloadHash,
                    AdminCommandStatus.Completed,
                    successMessage,
                    correlationId),
                cancellationToken);

            TempData[ExecutionSwitchSuccessTempDataKey] = successMessage;
        }
        catch (Exception exception)
        {
            var failureMessage = BuildActivationCommandFailureMessage(
                "ActivationApplyRejected",
                exception.Message,
                "Aktivasyon komutu backend state degisikligini tamamlayamadi.");

            await adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    actorUserId,
                    "Admin.Settings.Activation.ActivateFailed",
                    "ActivationControlCenter",
                    "System",
                    BuildExecutionSwitchSummary(operationalContext.ExecutionSnapshot),
                    requestedSummary,
                    normalizedReason,
                    correlationId),
                cancellationToken);

            await adminCommandRegistry.CompleteAsync(
                new AdminCommandCompletionRequest(
                    resolvedCommandId,
                    payloadHash,
                    AdminCommandStatus.Failed,
                    failureMessage,
                    correlationId),
                cancellationToken);

            TempData[ExecutionSwitchErrorTempDataKey] = failureMessage;
        }

        return RedirectToAction(redirectAction);
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeactivateSystem(
        string? reason,
        string? commandId,
        string? reauthToken,
        string? returnTarget = null,
        CancellationToken cancellationToken = default)
    {
        var redirectAction = ResolveOperationalFlowReturnAction(returnTarget);
        var mfaResult = await EnforcePlatformAdminMfaAsync(
            "Admin.Settings.Activation.Deactivate",
            ExecutionSwitchErrorTempDataKey,
            redirectAction,
            null,
            cancellationToken);

        if (mfaResult is not null)
        {
            return mfaResult;
        }

        if (!CanEditGlobalPolicy())
        {
            TempData[ExecutionSwitchErrorTempDataKey] = "ActivationRoleReadOnly: Bu rolde sistem kapatma komutu uygulanamaz.";
            return RedirectToAction(redirectAction);
        }

        var confirmationResult = EnforceCriticalActionConfirmation(
            reauthToken,
            ExecutionSwitchErrorTempDataKey,
            redirectAction,
            null);

        if (confirmationResult is not null)
        {
            return confirmationResult;
        }

        var normalizedReason = NormalizeRequiredReason(reason);

        if (normalizedReason is null)
        {
            TempData[ExecutionSwitchErrorTempDataKey] = "Audit reason zorunludur.";
            return RedirectToAction(redirectAction);
        }

        var actorUserId = ResolveAdminUserId();
        var correlationId = HttpContext.TraceIdentifier;
        var resolvedCommandId = ResolveCommandId(commandId);
        var previousSnapshot = await LoadExecutionSwitchSnapshotSafeAsync(cancellationToken);
        var requestedSummary = $"DeactivateSystem | {BuildExecutionSwitchSummary(previousSnapshot)}";
        var payloadHash = CreatePayloadHash($"DeactivateSystem|{normalizedReason}|{previousSnapshot.IsTradeMasterArmed}");
        var commandStartResult = await adminCommandRegistry.TryStartAsync(
            new AdminCommandStartRequest(
                resolvedCommandId,
                "Admin.Settings.Activation.Deactivate",
                actorUserId,
                "ActivationControlCenter.System",
                payloadHash,
                correlationId),
            cancellationToken);

        if (!await HandleCommandStartResultAsync(
                commandStartResult,
                actorUserId,
                "Admin.Settings.Activation.Deactivate",
                "ActivationControlCenter",
                "System",
                requestedSummary,
                normalizedReason,
                ExecutionSwitchSuccessTempDataKey,
                ExecutionSwitchErrorTempDataKey,
                cancellationToken))
        {
            return RedirectToAction(redirectAction);
        }

        try
        {
            var updatedSnapshot = await globalExecutionSwitchService.SetTradeMasterStateAsync(
                TradeMasterSwitchState.Disarmed,
                ResolveExecutionActor(),
                BuildSwitchContext("ActivationCenter.Deactivate", normalizedReason, resolvedCommandId),
                correlationId,
                cancellationToken);
            var successMessage = previousSnapshot.IsTradeMasterArmed
                ? "Sistem fail-closed kapatildi. TradeMaster disarmed olarak kaydedildi."
                : "Sistem zaten pasifti; TradeMaster disarmed durumu korundu.";

            await adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    actorUserId,
                    "Admin.Settings.Activation.Deactivate",
                    "ActivationControlCenter",
                    "System",
                    BuildExecutionSwitchSummary(previousSnapshot),
                    BuildExecutionSwitchSummary(updatedSnapshot),
                    normalizedReason,
                    correlationId),
                cancellationToken);

            await adminCommandRegistry.CompleteAsync(
                new AdminCommandCompletionRequest(
                    resolvedCommandId,
                    payloadHash,
                    AdminCommandStatus.Completed,
                    successMessage,
                    correlationId),
                cancellationToken);

            TempData[ExecutionSwitchSuccessTempDataKey] = successMessage;
        }
        catch (Exception exception)
        {
            var failureMessage = BuildActivationCommandFailureMessage(
                "DeactivateApplyRejected",
                exception.Message,
                "Sistem kapatma komutu backend state degisikligini tamamlayamadi.");

            await adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    actorUserId,
                    "Admin.Settings.Activation.DeactivateFailed",
                    "ActivationControlCenter",
                    "System",
                    BuildExecutionSwitchSummary(previousSnapshot),
                    requestedSummary,
                    normalizedReason,
                    correlationId),
                cancellationToken);

            await adminCommandRegistry.CompleteAsync(
                new AdminCommandCompletionRequest(
                    resolvedCommandId,
                    payloadHash,
                    AdminCommandStatus.Failed,
                    failureMessage,
                    correlationId),
                cancellationToken);

            TempData[ExecutionSwitchErrorTempDataKey] = failureMessage;
        }

        return RedirectToAction(redirectAction);
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetTradeMasterState(
        bool isArmed,
        string? reason,
        string? commandId,
        string? reauthToken,
        CancellationToken cancellationToken)
    {
        var mfaResult = await EnforcePlatformAdminMfaAsync(
            "Admin.Settings.TradeMaster.Update",
            ExecutionSwitchErrorTempDataKey,
            nameof(Settings),
            null,
            cancellationToken);

        if (mfaResult is not null)
        {
            return mfaResult;
        }

        var confirmationResult = EnforceCriticalActionConfirmation(
            reauthToken,
            ExecutionSwitchErrorTempDataKey,
            nameof(Settings),
            null);

        if (confirmationResult is not null)
        {
            return confirmationResult;
        }

        var normalizedReason = NormalizeRequiredReason(reason);

        if (normalizedReason is null)
        {
            TempData[ExecutionSwitchErrorTempDataKey] = "Audit reason zorunludur.";
            return RedirectToAction(nameof(Settings));
        }

        var actorUserId = ResolveAdminUserId();
        var correlationId = HttpContext.TraceIdentifier;
        var resolvedCommandId = ResolveCommandId(commandId);
        var payloadHash = CreatePayloadHash($"TradeMaster|{isArmed}|{normalizedReason}");
        var requestedSummary = $"TradeMaster={(isArmed ? "Armed" : "Disarmed")}";
        var commandStartResult = await adminCommandRegistry.TryStartAsync(
            new AdminCommandStartRequest(
                resolvedCommandId,
                "Admin.Settings.TradeMaster.Update",
                actorUserId,
                "GlobalExecutionSwitch.TradeMaster",
                payloadHash,
                correlationId),
            cancellationToken);

        if (!await HandleCommandStartResultAsync(
                commandStartResult,
                actorUserId,
                "Admin.Settings.TradeMaster.Update",
                "GlobalExecutionSwitch",
                "TradeMaster",
                requestedSummary,
                normalizedReason,
                ExecutionSwitchSuccessTempDataKey,
                ExecutionSwitchErrorTempDataKey,
                cancellationToken))
        {
            return RedirectToAction(nameof(Settings));
        }

        var previousSnapshot = await globalExecutionSwitchService.GetSnapshotAsync(cancellationToken);

        try
        {
            var updatedSnapshot = await globalExecutionSwitchService.SetTradeMasterStateAsync(
                isArmed ? TradeMasterSwitchState.Armed : TradeMasterSwitchState.Disarmed,
                ResolveExecutionActor(),
                BuildSwitchContext("TradeMaster", normalizedReason, resolvedCommandId),
                correlationId,
                cancellationToken);

            var successMessage = isArmed
                ? "TradeMaster armed. Emir zinciri backend hard gate uzerinden acildi."
                : "TradeMaster disarmed. Emir zinciri fail-closed duruma alindi.";

            await adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    actorUserId,
                    "Admin.Settings.TradeMaster.Update",
                    "GlobalExecutionSwitch",
                    "TradeMaster",
                    BuildExecutionSwitchSummary(previousSnapshot),
                    BuildExecutionSwitchSummary(updatedSnapshot),
                    normalizedReason,
                    correlationId),
                cancellationToken);

            await adminCommandRegistry.CompleteAsync(
                new AdminCommandCompletionRequest(
                    resolvedCommandId,
                    payloadHash,
                    AdminCommandStatus.Completed,
                    successMessage,
                    correlationId),
                cancellationToken);

            TempData[ExecutionSwitchSuccessTempDataKey] = successMessage;
        }
        catch (Exception exception)
        {
            await adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    actorUserId,
                    "Admin.Settings.TradeMaster.UpdateFailed",
                    "GlobalExecutionSwitch",
                    "TradeMaster",
                    BuildExecutionSwitchSummary(previousSnapshot),
                    requestedSummary,
                    normalizedReason,
                    correlationId),
                cancellationToken);

            await adminCommandRegistry.CompleteAsync(
                new AdminCommandCompletionRequest(
                    resolvedCommandId,
                    payloadHash,
                    AdminCommandStatus.Failed,
                    Truncate(exception.Message, 512),
                    correlationId),
                cancellationToken);

            TempData[ExecutionSwitchErrorTempDataKey] = exception.Message;
        }

        return RedirectToAction(nameof(Settings));
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDemoMode(
        bool isEnabled,
        string? reason,
        string? commandId,
        string? reauthToken,
        string? liveApprovalReference,
        string? returnTarget = null,
        CancellationToken cancellationToken = default)
    {
        var redirectAction = ResolveOperationalFlowReturnAction(returnTarget);
        var mfaResult = await EnforcePlatformAdminMfaAsync(
            "Admin.Settings.DemoMode.Update",
            ExecutionSwitchErrorTempDataKey,
            redirectAction,
            null,
            cancellationToken);

        if (mfaResult is not null)
        {
            return mfaResult;
        }

        var confirmationResult = EnforceCriticalActionConfirmation(
            reauthToken,
            ExecutionSwitchErrorTempDataKey,
            redirectAction,
            null);

        if (confirmationResult is not null)
        {
            return confirmationResult;
        }

        var normalizedReason = NormalizeRequiredReason(reason);

        if (normalizedReason is null)
        {
            TempData[ExecutionSwitchErrorTempDataKey] = "Audit reason zorunludur.";
            return RedirectToAction(redirectAction);
        }

        var actorUserId = ResolveAdminUserId();
        var correlationId = HttpContext.TraceIdentifier;
        var resolvedCommandId = ResolveCommandId(commandId);
        var normalizedLiveApprovalReference = NormalizeOptionalInput(liveApprovalReference, 128);
        var payloadHash = CreatePayloadHash(
            $"DemoMode|{isEnabled}|{normalizedReason}|{normalizedLiveApprovalReference ?? "none"}");
        var requestedSummary = BuildRequestedDemoModeSummary(isEnabled, normalizedLiveApprovalReference);
        var commandStartResult = await adminCommandRegistry.TryStartAsync(
            new AdminCommandStartRequest(
                resolvedCommandId,
                "Admin.Settings.DemoMode.Update",
                actorUserId,
                "GlobalExecutionSwitch.DemoMode",
                payloadHash,
                correlationId),
            cancellationToken);

        if (!await HandleCommandStartResultAsync(
                commandStartResult,
                actorUserId,
                "Admin.Settings.DemoMode.Update",
                "GlobalExecutionSwitch",
                "DemoMode",
                requestedSummary,
                normalizedReason,
                ExecutionSwitchSuccessTempDataKey,
                ExecutionSwitchErrorTempDataKey,
                cancellationToken))
        {
            return RedirectToAction(redirectAction);
        }

        var previousSnapshot = await globalExecutionSwitchService.GetSnapshotAsync(cancellationToken);

        try
        {
            TradingModeLiveApproval? liveApproval = null;

            if (!isEnabled)
            {
                liveApproval = string.IsNullOrWhiteSpace(normalizedLiveApprovalReference)
                    ? null
                    : new TradingModeLiveApproval(normalizedLiveApprovalReference);
            }

            var updatedSnapshot = await globalExecutionSwitchService.SetDemoModeAsync(
                isEnabled,
                ResolveExecutionActor(),
                liveApproval,
                BuildSwitchContext("DemoMode", normalizedReason, resolvedCommandId),
                correlationId,
                cancellationToken);

            var successMessage = isEnabled
                ? "DemoMode enabled. Live execution yolu backend hard gate ile kapatildi."
                : "DemoMode disabled. Live execution yalnizca approval reference ile acildi.";

            await adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    actorUserId,
                    "Admin.Settings.DemoMode.Update",
                    "GlobalExecutionSwitch",
                    "DemoMode",
                    BuildExecutionSwitchSummary(previousSnapshot),
                    BuildExecutionSwitchSummary(updatedSnapshot),
                    normalizedReason,
                    correlationId),
                cancellationToken);

            await adminCommandRegistry.CompleteAsync(
                new AdminCommandCompletionRequest(
                    resolvedCommandId,
                    payloadHash,
                    AdminCommandStatus.Completed,
                    successMessage,
                    correlationId),
                cancellationToken);

            TempData[ExecutionSwitchSuccessTempDataKey] = successMessage;
        }
        catch (Exception exception)
        {
            await adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    actorUserId,
                    "Admin.Settings.DemoMode.UpdateFailed",
                    "GlobalExecutionSwitch",
                    "DemoMode",
                    BuildExecutionSwitchSummary(previousSnapshot),
                    requestedSummary,
                    normalizedReason,
                    correlationId),
                cancellationToken);

            await adminCommandRegistry.CompleteAsync(
                new AdminCommandCompletionRequest(
                    resolvedCommandId,
                    payloadHash,
                    AdminCommandStatus.Failed,
                    Truncate(exception.Message, 512),
                    correlationId),
                cancellationToken);

            TempData[ExecutionSwitchErrorTempDataKey] = exception.Message;
        }

        return RedirectToAction(redirectAction);
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetGlobalSystemState(
        GlobalSystemStateKind state,
        string? reason,
        string? reasonCode,
        string? message,
        DateTime? expiresAtUtc,
        string? commandId,
        string? reauthToken,
        CancellationToken cancellationToken)
    {
        var mfaResult = await EnforcePlatformAdminMfaAsync(
            "Admin.Settings.GlobalSystemState.Update",
            GlobalSystemStateErrorTempDataKey,
            nameof(Settings),
            null,
            cancellationToken);

        if (mfaResult is not null)
        {
            return mfaResult;
        }

        var confirmationResult = EnforceCriticalActionConfirmation(
            reauthToken,
            GlobalSystemStateErrorTempDataKey,
            nameof(Settings),
            null);

        if (confirmationResult is not null)
        {
            return confirmationResult;
        }

        var normalizedReason = NormalizeRequiredReason(reason);

        if (normalizedReason is null)
        {
            TempData[GlobalSystemStateErrorTempDataKey] = "Audit reason zorunludur.";
            return RedirectToAction(nameof(Settings));
        }

        var normalizedReasonCode = NormalizeRequiredInput(reasonCode, 64);

        if (normalizedReasonCode is null)
        {
            TempData[GlobalSystemStateErrorTempDataKey] = "ReasonCode zorunludur.";
            return RedirectToAction(nameof(Settings));
        }

        var actorUserId = ResolveAdminUserId();
        var correlationId = HttpContext.TraceIdentifier;
        var resolvedCommandId = ResolveCommandId(commandId);
        var normalizedMessage = NormalizeOptionalInput(message, 512);
        var requestedSummary = BuildRequestedGlobalSystemStateSummary(
            state,
            normalizedReasonCode,
            normalizedMessage,
            expiresAtUtc);
        var stateRequest = new GlobalSystemStateSetRequest(
            state,
            normalizedReasonCode,
            normalizedMessage,
            "AdminPortal.Settings",
            correlationId,
            IsManualOverride: true,
            ExpiresAtUtc: expiresAtUtc,
            UpdatedByUserId: actorUserId,
            UpdatedFromIp: ResolveMaskedRemoteIpAddress(),
            CommandId: resolvedCommandId,
            ChangeSummary: requestedSummary);
        var queuePayloadJson = approvalWorkflowService is not null
            ? JsonSerializer.Serialize(stateRequest, PolicyJsonSerializerOptions)
            : null;
        var payloadHash = queuePayloadJson is not null
            ? CreatePayloadHash(queuePayloadJson)
            : CreatePayloadHash(
                $"GlobalSystemState|{state}|{normalizedReasonCode}|{normalizedMessage ?? "none"}|{expiresAtUtc?.ToUniversalTime().ToString("O") ?? "none"}");
        var commandStartResult = await adminCommandRegistry.TryStartAsync(
            new AdminCommandStartRequest(
                resolvedCommandId,
                "Admin.Settings.GlobalSystemState.Update",
                actorUserId,
                "GlobalSystemState.Singleton",
                payloadHash,
                correlationId),
            cancellationToken);

        if (!await HandleCommandStartResultAsync(
                commandStartResult,
                actorUserId,
                "Admin.Settings.GlobalSystemState.Update",
                "GlobalSystemState",
                "Singleton",
                requestedSummary,
                normalizedReason,
                GlobalSystemStateSuccessTempDataKey,
                GlobalSystemStateErrorTempDataKey,
                cancellationToken))
        {
            return RedirectToAction(nameof(Settings));
        }

        if (approvalWorkflowService is not null)
        {
            try
            {
                var approval = await approvalWorkflowService.EnqueueAsync(
                    new ApprovalQueueEnqueueRequest(
                        ApprovalQueueOperationType.GlobalSystemStateUpdate,
                        state == GlobalSystemStateKind.Active ? IncidentSeverity.Info : IncidentSeverity.Warning,
                        "Global system state update",
                        requestedSummary,
                        actorUserId,
                        normalizedReason,
                        queuePayloadJson!,
                        RequiredApprovals: state == GlobalSystemStateKind.Active ? 1 : 2,
                        ExpiresAtUtc: DateTime.UtcNow.AddHours(8),
                        TargetType: "GlobalSystemState",
                        TargetId: "Singleton",
                        CorrelationId: correlationId,
                        CommandId: resolvedCommandId),
                    cancellationToken);

                TempData[GlobalSystemStateSuccessTempDataKey] = $"Approval {approval.ApprovalReference} queued. {approval.RequiredApprovals} approval(s) required.";
                return RedirectToAction(nameof(Settings));
            }
            catch (Exception exception)
            {
                await adminCommandRegistry.CompleteAsync(
                    new AdminCommandCompletionRequest(
                        resolvedCommandId,
                        payloadHash,
                        AdminCommandStatus.Failed,
                        Truncate(exception.Message, 512),
                        correlationId),
                    cancellationToken);

                TempData[GlobalSystemStateErrorTempDataKey] = exception.Message;
                return RedirectToAction(nameof(Settings));
            }
        }

        var previousSnapshot = await globalSystemStateService.GetSnapshotAsync(cancellationToken);

        try
        {
            var updatedSnapshot = await globalSystemStateService.SetStateAsync(
                stateRequest,
                cancellationToken);

            var successMessage = $"Global system state set to {updatedSnapshot.State}.";

            await adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    actorUserId,
                    "Admin.Settings.GlobalSystemState.Update",
                    "GlobalSystemState",
                    "Singleton",
                    BuildGlobalSystemStateSummary(previousSnapshot),
                    BuildGlobalSystemStateSummary(updatedSnapshot),
                    normalizedReason,
                    correlationId),
                cancellationToken);

            await adminCommandRegistry.CompleteAsync(
                new AdminCommandCompletionRequest(
                    resolvedCommandId,
                    payloadHash,
                    AdminCommandStatus.Completed,
                    successMessage,
                    correlationId),
                cancellationToken);

            TempData[GlobalSystemStateSuccessTempDataKey] = successMessage;
        }
        catch (Exception exception)
        {
            await adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    actorUserId,
                    "Admin.Settings.GlobalSystemState.UpdateFailed",
                    "GlobalSystemState",
                    "Singleton",
                    BuildGlobalSystemStateSummary(previousSnapshot),
                    requestedSummary,
                    normalizedReason,
                    correlationId),
                cancellationToken);

            await adminCommandRegistry.CompleteAsync(
                new AdminCommandCompletionRequest(
                    resolvedCommandId,
                    payloadHash,
                    AdminCommandStatus.Failed,
                    Truncate(exception.Message, 512),
                    correlationId),
                cancellationToken);

            TempData[GlobalSystemStateErrorTempDataKey] = exception.Message;
        }

        return RedirectToAction(nameof(Settings));
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PreviewCrisisEscalation(
        CrisisEscalationLevel level,
        string? scope,
        string? reasonCode,
        string? message,
        CancellationToken cancellationToken)
    {
        var mfaResult = await EnforcePlatformAdminMfaAsync(
            "Admin.Settings.CrisisEscalation.Preview",
            CrisisErrorTempDataKey,
            nameof(Settings),
            null,
            cancellationToken);

        if (mfaResult is not null)
        {
            return mfaResult;
        }

        if (crisisEscalationService is null)
        {
            TempData[CrisisErrorTempDataKey] = "Crisis escalation service unavailable.";
            return RedirectToAction(nameof(Settings));
        }

        var normalizedScope = NormalizeRequiredInput(scope, 128);

        if (normalizedScope is null)
        {
            TempData[CrisisErrorTempDataKey] = "Scope zorunludur.";
            return RedirectToAction(nameof(Settings));
        }

        var normalizedReasonCode = NormalizeOptionalInput(reasonCode, 64) ?? ResolveDefaultCrisisReasonCode(level);
        var normalizedMessage = NormalizeOptionalInput(message, 512);

        try
        {
            var preview = await crisisEscalationService.PreviewAsync(
                new CrisisEscalationPreviewRequest(level, normalizedScope),
                cancellationToken);

            StoreCrisisPreviewViewModelInTempData(
                new AdminCrisisEscalationPreviewViewModel(
                    preview.Level,
                    preview.Scope,
                    normalizedReasonCode,
                    normalizedMessage,
                    preview.AffectedUserCount,
                    preview.AffectedSymbolCount,
                    preview.OpenPositionCount,
                    preview.PendingOrderCount,
                    preview.EstimatedExposure,
                    preview.RequiresReauth,
                    preview.RequiresSecondApproval,
                    preview.PreviewStamp));
        }
        catch (Exception exception)
        {
            TempData[CrisisErrorTempDataKey] = exception.Message;
            TempData.Remove(CrisisPreviewTempDataKey);
        }

        return RedirectToAction(nameof(Settings));
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExecuteCrisisEscalation(
        CrisisEscalationLevel level,
        string? scope,
        string? reasonCode,
        string? message,
        string? reason,
        string? previewStamp,
        string? commandId,
        string? reauthToken,
        string? secondApprovalReference,
        CancellationToken cancellationToken)
    {
        var mfaResult = await EnforcePlatformAdminMfaAsync(
            "Admin.Settings.CrisisEscalation.Execute",
            CrisisErrorTempDataKey,
            nameof(Settings),
            null,
            cancellationToken);

        if (mfaResult is not null)
        {
            return mfaResult;
        }

        if (crisisEscalationService is null)
        {
            TempData[CrisisErrorTempDataKey] = "Crisis escalation service unavailable.";
            return RedirectToAction(nameof(Settings));
        }

        var normalizedReason = NormalizeRequiredReason(reason);

        if (normalizedReason is null)
        {
            TempData[CrisisErrorTempDataKey] = "Audit reason zorunludur.";
            return RedirectToAction(nameof(Settings));
        }

        var normalizedScope = NormalizeRequiredInput(scope, 128);

        if (normalizedScope is null)
        {
            TempData[CrisisErrorTempDataKey] = "Scope zorunludur.";
            return RedirectToAction(nameof(Settings));
        }

        var normalizedPreviewStamp = NormalizeRequiredInput(previewStamp, 128);

        if (normalizedPreviewStamp is null)
        {
            TempData[CrisisErrorTempDataKey] = "Impact preview zorunludur.";
            return RedirectToAction(nameof(Settings));
        }

        var actorUserId = ResolveAdminUserId();
        var correlationId = HttpContext.TraceIdentifier;
        var resolvedCommandId = ResolveCommandId(commandId);
        var normalizedReasonCode = NormalizeOptionalInput(reasonCode, 64) ?? ResolveDefaultCrisisReasonCode(level);
        var normalizedMessage = NormalizeOptionalInput(message, 512);
        var payloadHash = CreatePayloadHash(
            $"Crisis|{level}|{normalizedScope}|{normalizedReasonCode}|{normalizedMessage ?? "none"}|{normalizedPreviewStamp}");
        var requestedSummary = BuildRequestedCrisisSummary(
            level,
            normalizedScope,
            normalizedReasonCode,
            normalizedMessage,
            normalizedPreviewStamp);
        var commandStartResult = await adminCommandRegistry.TryStartAsync(
            new AdminCommandStartRequest(
                resolvedCommandId,
                "Admin.Settings.CrisisEscalation.Execute",
                actorUserId,
                normalizedScope,
                payloadHash,
                correlationId),
            cancellationToken);

        if (!await HandleCommandStartResultAsync(
                commandStartResult,
                actorUserId,
                "Admin.Settings.CrisisEscalation.Execute",
                "CrisisEscalation",
                normalizedScope,
                requestedSummary,
                normalizedReason,
                CrisisSuccessTempDataKey,
                CrisisErrorTempDataKey,
                cancellationToken))
        {
            return RedirectToAction(nameof(Settings));
        }

        try
        {
            var executionResult = await crisisEscalationService.ExecuteAsync(
                new CrisisEscalationExecuteRequest(
                    level,
                    normalizedScope,
                    resolvedCommandId,
                    actorUserId,
                    ResolveExecutionActor(),
                    normalizedReason,
                    normalizedReasonCode,
                    normalizedMessage,
                    normalizedPreviewStamp,
                    correlationId,
                    reauthToken,
                    secondApprovalReference,
                    ResolveMaskedRemoteIpAddress()),
                cancellationToken);
            var finalStatus = executionResult.HasPartialFailures
                ? AdminCommandStatus.Failed
                : AdminCommandStatus.Completed;
            var auditActionType = executionResult.HasPartialFailures
                ? "Admin.Settings.CrisisEscalation.PartialFailure"
                : "Admin.Settings.CrisisEscalation.Execute";
            var tempDataKey = executionResult.HasPartialFailures
                ? CrisisErrorTempDataKey
                : CrisisSuccessTempDataKey;

            await adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    actorUserId,
                    auditActionType,
                    "CrisisEscalation",
                    normalizedScope,
                    oldValueSummary: null,
                    newValueSummary: executionResult.Summary,
                    normalizedReason,
                    correlationId),
                cancellationToken);

            await adminCommandRegistry.CompleteAsync(
                new AdminCommandCompletionRequest(
                    resolvedCommandId,
                    payloadHash,
                    finalStatus,
                    executionResult.Summary,
                    correlationId),
                cancellationToken);

            TempData[tempDataKey] = executionResult.Summary;
            TempData.Remove(CrisisPreviewTempDataKey);
        }
        catch (Exception exception)
        {
            await adminAuditLogService.WriteAsync(
                BuildAdminAuditLogWriteRequest(
                    actorUserId,
                    "Admin.Settings.CrisisEscalation.ExecuteFailed",
                    "CrisisEscalation",
                    normalizedScope,
                    oldValueSummary: null,
                    newValueSummary: requestedSummary,
                    normalizedReason,
                    correlationId),
                cancellationToken);

            await adminCommandRegistry.CompleteAsync(
                new AdminCommandCompletionRequest(
                    resolvedCommandId,
                    payloadHash,
                    AdminCommandStatus.Failed,
                    Truncate(exception.Message, 512),
                    correlationId),
                cancellationToken);

            TempData[CrisisErrorTempDataKey] = exception.Message;
        }

        return RedirectToAction(nameof(Settings));
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateSymbolRestrictions(
        List<AdminSymbolRestrictionInputModel>? restrictions,
        string? reason,
        string? commandId,
        string? reauthToken,
        CancellationToken cancellationToken)
    {
        IActionResult RedirectToSymbolRestrictions() =>
            RedirectToAction(nameof(Settings), "Admin", new { area = "Admin" }, "cb_admin_settings_policy_restrictions");
        var mfaResult = await EnforcePlatformAdminMfaAsync(
            "Admin.Settings.SymbolRestrictions.Update",
            GlobalPolicyErrorTempDataKey,
            nameof(Settings),
            null,
            cancellationToken);

        if (mfaResult is not null)
        {
            return mfaResult;
        }

        _ = reauthToken;

        if (!CanEditGlobalPolicy())
        {
            TempData[GlobalPolicyErrorTempDataKey] = "Bu rolde symbol restriction degistirilemez.";
            return RedirectToSymbolRestrictions();
        }

        var normalizedReason = NormalizeRequiredReason(reason);

        if (normalizedReason is null)
        {
            TempData[GlobalPolicyErrorTempDataKey] = "Audit reason zorunludur.";
            return RedirectToSymbolRestrictions();
        }

        if (globalPolicyEngine is null)
        {
            TempData[GlobalPolicyErrorTempDataKey] = "Global policy engine is unavailable.";
            return RedirectToSymbolRestrictions();
        }

        var actorUserId = ResolveAdminUserId();
        var correlationId = HttpContext.TraceIdentifier;
        var resolvedCommandId = ResolveCommandId(commandId);

        GlobalPolicySnapshot currentSnapshot;
        IReadOnlyCollection<SymbolRestriction> normalizedRestrictions;

        try
        {
            currentSnapshot = await globalPolicyEngine.GetSnapshotAsync(cancellationToken);
            normalizedRestrictions = TryNormalizeSymbolRestrictions(
                    restrictions,
                    currentSnapshot.Policy.SymbolRestrictions,
                    actorUserId,
                    DateTime.UtcNow,
                    out var restrictionErrorMessage)
                ?? throw new InvalidOperationException(restrictionErrorMessage ?? "Symbol restriction listesi okunamadi.");
        }
        catch (Exception exception)
        {
            TempData[GlobalPolicyErrorTempDataKey] = exception.Message;
            return RedirectToSymbolRestrictions();
        }

        var updatedPolicy = currentSnapshot.Policy with
        {
            SymbolRestrictions = normalizedRestrictions
        };

        var requestedSummary = BuildSymbolRestrictionSummary(updatedPolicy.SymbolRestrictions);
        var policyUpdateRequest = new GlobalPolicyUpdateRequest(
            updatedPolicy,
            actorUserId,
            normalizedReason,
            correlationId,
            "AdminPortal.Settings.SymbolRestrictions",
            ResolveMaskedRemoteIpAddress(),
            ResolveMaskedUserAgent());
        var policyPayloadJson = JsonSerializer.Serialize(policyUpdateRequest, PolicyJsonSerializerOptions);
        var payloadHash = CreatePayloadHash(policyPayloadJson);
        var commandStartResult = await adminCommandRegistry.TryStartAsync(
            new AdminCommandStartRequest(
                resolvedCommandId,
                "Admin.Settings.SymbolRestrictions.Update",
                actorUserId,
                "RiskPolicy",
                payloadHash,
                correlationId),
            cancellationToken);

        if (!await HandleCommandStartResultAsync(
                commandStartResult,
                actorUserId,
                "Admin.Settings.SymbolRestrictions.Update",
                "RiskPolicy",
                "GlobalRiskPolicy",
                requestedSummary,
                normalizedReason,
                GlobalPolicySuccessTempDataKey,
                GlobalPolicyErrorTempDataKey,
                cancellationToken))
        {
            return RedirectToSymbolRestrictions();
        }


        try
        {
            var updatedSnapshot = await globalPolicyEngine.UpdateAsync(
                policyUpdateRequest,
                cancellationToken);

            var successMessage = updatedSnapshot.Policy.SymbolRestrictions.Count == 0
                ? $"Symbol restrictions cleared in global policy version {updatedSnapshot.CurrentVersion}."
                : $"Symbol restrictions updated in global policy version {updatedSnapshot.CurrentVersion}.";
            await adminCommandRegistry.CompleteAsync(
                new AdminCommandCompletionRequest(
                    resolvedCommandId,
                    payloadHash,
                    AdminCommandStatus.Completed,
                    successMessage,
                    correlationId),
                cancellationToken);

            TempData[GlobalPolicySuccessTempDataKey] = successMessage;
        }
        catch (Exception exception)
        {
            await adminCommandRegistry.CompleteAsync(
                new AdminCommandCompletionRequest(
                    resolvedCommandId,
                    payloadHash,
                    AdminCommandStatus.Failed,
                    Truncate(exception.Message, 512),
                    correlationId),
                cancellationToken);

            TempData[GlobalPolicyErrorTempDataKey] = exception.Message;
        }

        return RedirectToSymbolRestrictions();
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateGlobalPolicy(
        string? policyJson,
        string? reason,
        string? commandId,
        string? reauthToken,
        CancellationToken cancellationToken)
    {
        var mfaResult = await EnforcePlatformAdminMfaAsync(
            "Admin.Settings.GlobalPolicy.Update",
            GlobalPolicyErrorTempDataKey,
            nameof(Settings),
            null,
            cancellationToken);

        if (mfaResult is not null)
        {
            return mfaResult;
        }

        _ = reauthToken;

        if (!CanEditGlobalPolicy())
        {
            TempData[GlobalPolicyErrorTempDataKey] = "Bu rolde global policy degistirilemez.";
            return RedirectToAction(nameof(Settings));
        }

        var normalizedReason = NormalizeRequiredReason(reason);

        if (normalizedReason is null)
        {
            TempData[GlobalPolicyErrorTempDataKey] = "Audit reason zorunludur.";
            return RedirectToAction(nameof(Settings));
        }

        var normalizedPolicyJson = NormalizeRequiredInput(policyJson, 8192);

        if (normalizedPolicyJson is null)
        {
            TempData[GlobalPolicyErrorTempDataKey] = "Policy JSON zorunludur.";
            return RedirectToAction(nameof(Settings));
        }

        var policy = TryParsePolicySnapshot(normalizedPolicyJson, out var parseError);

        if (policy is null)
        {
            TempData[GlobalPolicyErrorTempDataKey] = parseError ?? "Policy JSON okunamadi.";
            return RedirectToAction(nameof(Settings));
        }

        var actorUserId = ResolveAdminUserId();
        var correlationId = HttpContext.TraceIdentifier;
        var resolvedCommandId = ResolveCommandId(commandId);
        var requestedSummary = BuildPolicySummary(policy);
        var policyUpdateRequest = new GlobalPolicyUpdateRequest(
            policy,
            actorUserId,
            normalizedReason,
            correlationId,
            "AdminPortal.Settings",
            ResolveMaskedRemoteIpAddress(),
            ResolveMaskedUserAgent());
        var queuePayloadJson = approvalWorkflowService is not null
            ? JsonSerializer.Serialize(policyUpdateRequest, PolicyJsonSerializerOptions)
            : null;
        var payloadHash = queuePayloadJson is not null
            ? CreatePayloadHash(queuePayloadJson)
            : CreatePayloadHash($"GlobalPolicy|Update|{normalizedReason}|{normalizedPolicyJson}");
        var commandStartResult = await adminCommandRegistry.TryStartAsync(
            new AdminCommandStartRequest(
                resolvedCommandId,
                "Admin.Settings.GlobalPolicy.Update",
                actorUserId,
                "RiskPolicy",
                payloadHash,
                correlationId),
            cancellationToken);

        if (!await HandleCommandStartResultAsync(
                commandStartResult,
                actorUserId,
                "Admin.Settings.GlobalPolicy.Update",
                "RiskPolicy",
                "GlobalRiskPolicy",
                requestedSummary,
                normalizedReason,
                GlobalPolicySuccessTempDataKey,
                GlobalPolicyErrorTempDataKey,
                cancellationToken))
        {
            return RedirectToAction(nameof(Settings));
        }

        if (approvalWorkflowService is not null)
        {
            try
            {
                var approval = await approvalWorkflowService.EnqueueAsync(
                    new ApprovalQueueEnqueueRequest(
                        ApprovalQueueOperationType.GlobalPolicyUpdate,
                        IncidentSeverity.Critical,
                        "Global policy update",
                        requestedSummary,
                        actorUserId,
                        normalizedReason,
                        queuePayloadJson!,
                        RequiredApprovals: 2,
                        ExpiresAtUtc: DateTime.UtcNow.AddHours(8),
                        TargetType: "RiskPolicy",
                        TargetId: "GlobalRiskPolicy",
                        CorrelationId: correlationId,
                        CommandId: resolvedCommandId),
                    cancellationToken);

                TempData[GlobalPolicySuccessTempDataKey] = $"Approval {approval.ApprovalReference} queued. 2 approval(s) required.";
                return RedirectToAction(nameof(Settings));
            }
            catch (Exception exception)
            {
                await adminCommandRegistry.CompleteAsync(
                    new AdminCommandCompletionRequest(
                        resolvedCommandId,
                        payloadHash,
                        AdminCommandStatus.Failed,
                        Truncate(exception.Message, 512),
                        correlationId),
                    cancellationToken);

                TempData[GlobalPolicyErrorTempDataKey] = exception.Message;
                return RedirectToAction(nameof(Settings));
            }
        }

        try
        {
            if (globalPolicyEngine is null)
            {
                throw new InvalidOperationException("Global policy engine is unavailable.");
            }

            var updatedSnapshot = await globalPolicyEngine.UpdateAsync(
                policyUpdateRequest,
                cancellationToken);

            var successMessage = $"Global policy updated to version {updatedSnapshot.CurrentVersion}.";
            await adminCommandRegistry.CompleteAsync(
                new AdminCommandCompletionRequest(
                    resolvedCommandId,
                    payloadHash,
                    AdminCommandStatus.Completed,
                    successMessage,
                    correlationId),
                cancellationToken);

            TempData[GlobalPolicySuccessTempDataKey] = successMessage;
        }
        catch (Exception exception)
        {
            await adminCommandRegistry.CompleteAsync(
                new AdminCommandCompletionRequest(
                    resolvedCommandId,
                    payloadHash,
                    AdminCommandStatus.Failed,
                    Truncate(exception.Message, 512),
                    correlationId),
                cancellationToken);

            TempData[GlobalPolicyErrorTempDataKey] = exception.Message;
        }

        return RedirectToAction(nameof(Settings));
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RollbackGlobalPolicy(
        int targetVersion,
        string? reason,
        string? commandId,
        string? reauthToken,
        CancellationToken cancellationToken)
    {
        var mfaResult = await EnforcePlatformAdminMfaAsync(
            "Admin.Settings.GlobalPolicy.Rollback",
            GlobalPolicyErrorTempDataKey,
            nameof(Settings),
            null,
            cancellationToken);

        if (mfaResult is not null)
        {
            return mfaResult;
        }

        _ = reauthToken;

        if (!CanEditGlobalPolicy())
        {
            TempData[GlobalPolicyErrorTempDataKey] = "Bu rolde global policy rollback yapilamaz.";
            return RedirectToAction(nameof(Settings));
        }

        if (targetVersion <= 0)
        {
            TempData[GlobalPolicyErrorTempDataKey] = "Target version pozitif olmali.";
            return RedirectToAction(nameof(Settings));
        }

        var normalizedReason = NormalizeRequiredReason(reason);

        if (normalizedReason is null)
        {
            TempData[GlobalPolicyErrorTempDataKey] = "Audit reason zorunludur.";
            return RedirectToAction(nameof(Settings));
        }

        var actorUserId = ResolveAdminUserId();
        var correlationId = HttpContext.TraceIdentifier;
        var resolvedCommandId = ResolveCommandId(commandId);
        var requestedSummary = $"TargetVersion={targetVersion}";
        var policyRollbackRequest = new GlobalPolicyRollbackRequest(
            targetVersion,
            actorUserId,
            normalizedReason,
            correlationId,
            "AdminPortal.Settings",
            ResolveMaskedRemoteIpAddress(),
            ResolveMaskedUserAgent());
        var queuePayloadJson = approvalWorkflowService is not null
            ? JsonSerializer.Serialize(policyRollbackRequest, PolicyJsonSerializerOptions)
            : null;
        var payloadHash = queuePayloadJson is not null
            ? CreatePayloadHash(queuePayloadJson)
            : CreatePayloadHash($"GlobalPolicy|Rollback|{targetVersion}|{normalizedReason}");
        var commandStartResult = await adminCommandRegistry.TryStartAsync(
            new AdminCommandStartRequest(
                resolvedCommandId,
                "Admin.Settings.GlobalPolicy.Rollback",
                actorUserId,
                "RiskPolicy",
                payloadHash,
                correlationId),
            cancellationToken);

        if (!await HandleCommandStartResultAsync(
                commandStartResult,
                actorUserId,
                "Admin.Settings.GlobalPolicy.Rollback",
                "RiskPolicy",
                "GlobalRiskPolicy",
                requestedSummary,
                normalizedReason,
                GlobalPolicySuccessTempDataKey,
                GlobalPolicyErrorTempDataKey,
                cancellationToken))
        {
            return RedirectToAction(nameof(Settings));
        }

        if (approvalWorkflowService is not null)
        {
            try
            {
                var approval = await approvalWorkflowService.EnqueueAsync(
                    new ApprovalQueueEnqueueRequest(
                        ApprovalQueueOperationType.GlobalPolicyRollback,
                        IncidentSeverity.Critical,
                        "Global policy rollback",
                        requestedSummary,
                        actorUserId,
                        normalizedReason,
                        queuePayloadJson!,
                        RequiredApprovals: 2,
                        ExpiresAtUtc: DateTime.UtcNow.AddHours(8),
                        TargetType: "RiskPolicy",
                        TargetId: "GlobalRiskPolicy",
                        CorrelationId: correlationId,
                        CommandId: resolvedCommandId),
                    cancellationToken);

                TempData[GlobalPolicySuccessTempDataKey] = $"Approval {approval.ApprovalReference} queued. 2 approval(s) required.";
                return RedirectToAction(nameof(Settings));
            }
            catch (Exception exception)
            {
                await adminCommandRegistry.CompleteAsync(
                    new AdminCommandCompletionRequest(
                        resolvedCommandId,
                        payloadHash,
                        AdminCommandStatus.Failed,
                        Truncate(exception.Message, 512),
                        correlationId),
                    cancellationToken);

                TempData[GlobalPolicyErrorTempDataKey] = exception.Message;
                return RedirectToAction(nameof(Settings));
            }
        }

        try
        {
            if (globalPolicyEngine is null)
            {
                throw new InvalidOperationException("Global policy engine is unavailable.");
            }

            var updatedSnapshot = await globalPolicyEngine.RollbackAsync(
                policyRollbackRequest,
                cancellationToken);

            var successMessage = $"Global policy rolled back to version {targetVersion} and republished as version {updatedSnapshot.CurrentVersion}.";
            await adminCommandRegistry.CompleteAsync(
                new AdminCommandCompletionRequest(
                    resolvedCommandId,
                    payloadHash,
                    AdminCommandStatus.Completed,
                    successMessage,
                    correlationId),
                cancellationToken);

            TempData[GlobalPolicySuccessTempDataKey] = successMessage;
        }
        catch (Exception exception)
        {
            await adminCommandRegistry.CompleteAsync(
                new AdminCommandCompletionRequest(
                    resolvedCommandId,
                    payloadHash,
                    AdminCommandStatus.Failed,
                    Truncate(exception.Message, 512),
                    correlationId),
                cancellationToken);

            TempData[GlobalPolicyErrorTempDataKey] = exception.Message;
        }

        return RedirectToAction(nameof(Settings));
    }

    //private async Task<AdminStrategyTemplateCatalogPageViewModel> BuildStrategyTemplateCatalogPageViewModelAsync(
    //    string? templateKey,
    //    CancellationToken cancellationToken)
    //{
    //    var templates = await strategyTemplateCatalogService.ListAllAsync(cancellationToken);
    //    var normalizedTemplateKey = NormalizeOptionalInput(templateKey, 128);
    //    StrategyTemplateSnapshot? selectedTemplate = null;

    //    if (normalizedTemplateKey is not null)
    //    {
    //        selectedTemplate = templates.FirstOrDefault(template =>
    //            string.Equals(template.TemplateKey, normalizedTemplateKey, StringComparison.OrdinalIgnoreCase));

    //        if (selectedTemplate is null && TempData[StrategyTemplateErrorTempDataKey] is null)
    //        {
    //            TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateInputMessage("Detail", "Secilen template bulunamadi. Liste ekranindan gecerli bir template secin.");
    //        }
    //    }

    //    selectedTemplate ??= templates
    //        .OrderBy(template => template.IsBuiltIn ? 1 : 0)
    //        .ThenBy(template => template.IsActive ? 0 : 1)
    //        .ThenBy(template => template.TemplateName, StringComparer.Ordinal)
    //        .FirstOrDefault();

    //    var revisions = selectedTemplate is null
    //        ? Array.Empty<StrategyTemplateRevisionSnapshot>()
    //        : await strategyTemplateCatalogService.ListRevisionsAsync(selectedTemplate.TemplateKey, cancellationToken);

    //    return new AdminStrategyTemplateCatalogPageViewModel(
    //        selectedTemplate?.TemplateKey,
    //        templates,
    //        selectedTemplate,
    //        revisions,
    //        CanManageStrategyTemplates(),
    //        DateTime.UtcNow);
    //}

    private async Task<AdminStrategyTemplateCatalogPageViewModel> BuildStrategyTemplateCatalogPageViewModelAsync(
    string? templateKey,
    CancellationToken cancellationToken,
    bool autoSelectFirstTemplate = true)
    {
        var normalizedTemplateKey = NormalizeOptionalInput(templateKey, 128);

        try
        {
            var templates = await strategyTemplateCatalogService.ListAllAsync(cancellationToken);
            StrategyTemplateSnapshot? selectedTemplate = null;

            if (normalizedTemplateKey is not null)
            {
                selectedTemplate = templates.FirstOrDefault(template =>
                    string.Equals(template.TemplateKey, normalizedTemplateKey, StringComparison.OrdinalIgnoreCase));

                if (selectedTemplate is null && TempData[StrategyTemplateErrorTempDataKey] is null)
                {
                    TempData[StrategyTemplateErrorTempDataKey] = BuildStrategyTemplateInputMessage("Detail", "Secilen template bulunamadi. Liste ekranindan gecerli bir template secin.");
                }
            }

            if (selectedTemplate is null && autoSelectFirstTemplate)
            {
                selectedTemplate = templates
                    .OrderBy(template => template.IsBuiltIn ? 1 : 0)
                    .ThenBy(template => template.IsActive ? 0 : 1)
                    .ThenBy(template => template.TemplateName, StringComparer.Ordinal)
                    .FirstOrDefault();
            }

            var revisions = selectedTemplate is null
                ? Array.Empty<StrategyTemplateRevisionSnapshot>()
                : await strategyTemplateCatalogService.ListRevisionsAsync(selectedTemplate.TemplateKey, cancellationToken);

            return new AdminStrategyTemplateCatalogPageViewModel(
                selectedTemplate?.TemplateKey,
                templates,
                selectedTemplate,
                revisions,
                CanManageStrategyTemplates(),
                DateTime.UtcNow);
        }
        catch (StrategyTemplateCatalogException exception)
        {
            TempData[StrategyTemplateErrorTempDataKey] ??= BuildStrategyTemplateFailureMessage("Catalog", normalizedTemplateKey, exception);
        }
        catch (StrategyDefinitionValidationException exception)
        {
            TempData[StrategyTemplateErrorTempDataKey] ??= BuildStrategyTemplateOperationMessage("Catalog", normalizedTemplateKey, $"Strategy template catalogu yuklenemedi. {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            TempData[StrategyTemplateErrorTempDataKey] ??= BuildStrategyTemplateOperationMessage("Catalog", normalizedTemplateKey, $"Strategy template catalogu yuklenemedi. {exception.Message}");
        }

        return new AdminStrategyTemplateCatalogPageViewModel(
            normalizedTemplateKey,
            Array.Empty<StrategyTemplateSnapshot>(),
            null,
            Array.Empty<StrategyTemplateRevisionSnapshot>(),
            CanManageStrategyTemplates(),
            DateTime.UtcNow);
    }


    private bool CanManageStrategyTemplates()
    {
        return User.HasClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.PlatformAdministration);
    }

    private static string BuildStrategyTemplateSummary(StrategyTemplateSnapshot snapshot)
    {
        return $"TemplateKey={snapshot.TemplateKey}; Name={snapshot.TemplateName}; Category={snapshot.Category}; Source={snapshot.TemplateSource}; Active={snapshot.IsActive}; CurrentRevision={snapshot.ActiveRevisionNumber}; PublishedRevision={snapshot.PublishedRevisionNumber}; LatestRevision={snapshot.LatestRevisionNumber}; ArchivedAtUtc={snapshot.ArchivedAtUtc?.ToString("O") ?? "none"}";
    }

    private async Task WriteStrategyTemplateFailureAuditAsync(
        string actorUserId,
        string actionType,
        string? targetId,
        string? oldValueSummary,
        string? newValueSummary,
        string reason,
        string failureCode,
        CancellationToken cancellationToken)
    {
        var attemptedSummary = string.IsNullOrWhiteSpace(newValueSummary)
            ? $"FailureCode={failureCode}"
            : $"{newValueSummary}; FailureCode={failureCode}";

        await adminAuditLogService.WriteAsync(
            BuildAdminAuditLogWriteRequest(
                actorUserId,
                actionType,
                "StrategyTemplate",
                targetId,
                oldValueSummary,
                attemptedSummary,
                reason,
                HttpContext.TraceIdentifier),
            cancellationToken);
    }

    private async Task<AdminSettingsOperationalContext> LoadSettingsOperationalContextAsync(CancellationToken cancellationToken)
    {
        var clockDriftTask = LoadClockDriftSnapshotSafeAsync(cancellationToken);
        var executionSnapshot = await LoadExecutionSwitchSnapshotSafeAsync(cancellationToken);
        var globalSystemStateSnapshot = await LoadGlobalSystemStateSnapshotSafeAsync(cancellationToken);
        var driftGuardSnapshot = await LoadDriftGuardSnapshotSafeAsync(cancellationToken);
        var clockDriftSnapshot = await clockDriftTask;
        var pilotOrderNotionalSummary = ResolvePilotOrderNotionalSummary();
        var pilotOrderNotionalTone = ResolvePilotOrderNotionalTone();
        var longRegimePolicyStatus = ResolveLongRegimePolicyStatus();
        var longRegimePolicyTone = ResolveLongRegimePolicyTone();
        var longRegimePolicyDetail = ResolveLongRegimePolicyDetail();
        var clockDriftViewModel = BuildClockDriftInfoViewModel(clockDriftSnapshot);
        var driftGuardViewModel = BuildMarketDriftGuardInfoViewModel(driftGuardSnapshot);
        var activationControlCenter = AdminActivationControlCenterComposer.Compose(
            executionSnapshot,
            globalSystemStateSnapshot,
            clockDriftSnapshot,
            driftGuardSnapshot,
            pilotOptionsValue,
            pilotOrderNotionalSummary,
            pilotOrderNotionalTone,
            DateTime.UtcNow);

        return new AdminSettingsOperationalContext(
            executionSnapshot,
            globalSystemStateSnapshot,
            clockDriftSnapshot,
            driftGuardSnapshot,
            clockDriftViewModel,
            driftGuardViewModel,
            pilotOrderNotionalSummary,
            pilotOrderNotionalTone,
            longRegimePolicyStatus,
            longRegimePolicyTone,
            longRegimePolicyDetail,
            activationControlCenter);
    }

    private async Task<GlobalExecutionSwitchSnapshot> LoadExecutionSwitchSnapshotSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await globalExecutionSwitchService.GetSnapshotAsync(cancellationToken);
        }
        catch
        {
            return new GlobalExecutionSwitchSnapshot(
                TradeMasterSwitchState.Disarmed,
                DemoModeEnabled: true,
                IsPersisted: false);
        }
    }

    private async Task<GlobalSystemStateSnapshot> LoadGlobalSystemStateSnapshotSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await globalSystemStateService.GetSnapshotAsync(cancellationToken);
        }
        catch
        {
            return new GlobalSystemStateSnapshot(
                GlobalSystemStateKind.Degraded,
                "SYSTEM_STATE_UNAVAILABLE",
                "Global system state snapshot unavailable.",
                "AdminPortal.Settings",
                HttpContext.TraceIdentifier,
                IsManualOverride: false,
                ExpiresAtUtc: null,
                UpdatedAtUtc: null,
                UpdatedByUserId: null,
                UpdatedFromIp: null,
                Version: 0,
                IsPersisted: false);
        }
    }

    private async Task<BinanceTimeSyncSnapshot> LoadClockDriftSnapshotSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await binanceTimeSyncService.GetSnapshotAsync(cancellationToken: cancellationToken);
        }
        catch
        {
            return new BinanceTimeSyncSnapshot(
                DateTime.UtcNow,
                ExchangeServerTimeUtc: null,
                OffsetMilliseconds: 0,
                RoundTripMilliseconds: null,
                LastSynchronizedAtUtc: null,
                StatusCode: "Unavailable",
                FailureReason: "Server-time sync snapshot unavailable.");
        }
    }

    private async Task<DegradedModeSnapshot> LoadDriftGuardSnapshotSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dataLatencyCircuitBreaker.GetSnapshotAsync(HttpContext.TraceIdentifier, cancellationToken: cancellationToken);
        }
        catch
        {
            return new DegradedModeSnapshot(
                DegradedModeStateCode.Stopped,
                DegradedModeReasonCode.MarketDataUnavailable,
                SignalFlowBlocked: true,
                ExecutionFlowBlocked: true,
                LatestDataTimestampAtUtc: null,
                LatestHeartbeatReceivedAtUtc: null,
                LatestDataAgeMilliseconds: null,
                LatestClockDriftMilliseconds: null,
                LastStateChangedAtUtc: null,
                IsPersisted: false);
        }
    }

    private static string BuildActivationControlCenterSummary(AdminActivationControlCenterViewModel model)
    {
        return string.Join(
            "; ",
            $"Status={model.StatusLabel}",
            $"IsActive={model.IsCurrentlyActive}",
            $"IsActivatable={model.IsActivatable}",
            $"DecisionType={model.LastDecision.TypeLabel}",
            $"DecisionCode={model.LastDecision.Code}",
            $"DecisionSource={model.LastDecision.Source}",
            $"Mode={model.CurrentModeLabel}");
    }

    private static string BuildActivationControlFailureMessage(AdminActivationControlCenterViewModel model)
    {
        return $"{model.LastDecision.Code}: {SanitizeOperationalMessage(model.LastDecision.Summary, "Aktivasyon karari bloklandi.")}";
    }

    private static string BuildActivationCommandFailureMessage(string code, string? message, string fallbackMessage)
    {
        return $"{code}: {SanitizeOperationalMessage(message, fallbackMessage)}";
    }

    private IActionResult? EnforceCriticalActionConfirmation(
        string? reauthToken,
        string errorTempDataKey,
        string redirectAction,
        object? routeValues)
    {
        var normalizedConfirmation = NormalizeOptionalInput(reauthToken, 64);

        if (string.Equals(normalizedConfirmation, CriticalActionConfirmationPhrase, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        TempData[errorTempDataKey] =
            $"CriticalActionConfirmationRequired: Bu kritik islem icin {CriticalActionConfirmationPhrase} ibaresi zorunludur.";

        return RedirectToAction(redirectAction, routeValues);
    }

    private static string SanitizeOperationalMessage(string? message, string fallbackMessage)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return fallbackMessage;
        }

        var normalizedMessage = message
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        return Truncate(normalizedMessage, 256) ?? fallbackMessage;
    }

    private sealed record AdminSettingsOperationalContext(
        GlobalExecutionSwitchSnapshot ExecutionSnapshot,
        GlobalSystemStateSnapshot GlobalSystemStateSnapshot,
        BinanceTimeSyncSnapshot ClockDriftSnapshot,
        DegradedModeSnapshot DriftGuardSnapshot,
        ClockDriftInfoViewModel ClockDriftViewModel,
        MarketDriftGuardInfoViewModel DriftGuardViewModel,
        string PilotOrderNotionalSummary,
        string PilotOrderNotionalTone,
        string LongRegimePolicyStatus,
        string LongRegimePolicyTone,
        string LongRegimePolicyDetail,
        AdminActivationControlCenterViewModel ActivationControlCenter);

    private static string BuildStrategyTemplateSuccessMessage(string action, StrategyTemplateSnapshot snapshot, int focusRevision)
    {
        var normalizedAction = action?.Trim() ?? "Update";
        var templateLabel = $"{snapshot.TemplateName} ({snapshot.TemplateKey})";
        var revisionSummary = $"Current r{(snapshot.ActiveRevisionNumber > 0 ? snapshot.ActiveRevisionNumber : 0)} · Published {(snapshot.PublishedRevisionNumber > 0 ? $"r{snapshot.PublishedRevisionNumber}" : "Unpublished")} · Latest r{(snapshot.LatestRevisionNumber > 0 ? snapshot.LatestRevisionNumber : 0)}";

        return normalizedAction switch
        {
            "Create" => $"Yeni template olusturuldu: {templateLabel}. Olusan revision r{focusRevision}. {revisionSummary}.",
            "Revise" => $"Template revize edildi: {templateLabel}. Yeni revision r{focusRevision}. {revisionSummary}.",
            "Save" => $"Template guncellendi: {templateLabel}. Mevcut kayit dogrudan guncellendi. {revisionSummary}.",
            "Publish" => $"Template publish edildi: {templateLabel}. Published revision artik r{focusRevision}. {revisionSummary}.",
            "Archive" => $"Template archive edildi: {templateLabel}. Template pasif duruma alindi. {revisionSummary}.",
            _ => $"Template islemi tamamlandi: {templateLabel}. {revisionSummary}."
        };
    }

    private static string BuildStrategyTemplateFailureMessage(string action, string? templateKey, StrategyTemplateCatalogException exception)
    {
        var label = string.IsNullOrWhiteSpace(templateKey) ? "secili template" : $"'{templateKey}'";
        return exception.FailureCode switch
        {
            "TemplateBuiltInImmutable" => $"Read-only source: {label} built-in kaynaktan geliyor. Dogrudan duzenleme/publish/archive yapilamaz; clone ederek custom olusturun.",
            "TemplateArchived" => $"Publish/active conflict: {label} archive durumda. Revize veya publish oncesi aktif bir custom template kullanin.",
            "TemplateRevisionArchived" => $"Publish/active conflict: {label} icin archive edilmis revision publish edilemez.",
            "TemplateRevisionNotFound" => $"Publish blocked: {label} icin secilen revision bulunamadi.",
            "TemplateNotFound" => $"Template bulunamadi: {label} katalogda yok veya erisilemiyor.",
            "TemplateKeyReserved" => $"Create blocked: secilen template key built-in kaynak tarafindan rezerve edilmis. Farkli bir custom key kullanin.",
            "TemplateKeyAlreadyExists" => $"Create blocked: secilen template key zaten var. Farkli bir key ile tekrar deneyin.",
            "TemplatePersistenceUnavailable" => "Catalog blocked: strategy template persistence su anda hazir degil.",
            "TemplateOwnershipScopeViolation" => $"Yetki blocked: {label} icin ownership/policy kapsami bu islemi izin vermiyor.",
            "TemplateUnpublished" => $"Publish blocked: {label} icin henuz published revision yok.",
            _ => BuildStrategyTemplateOperationMessage(action, templateKey, $"{exception.FailureCode}: {exception.Message}")
        };
    }

    private static string BuildStrategyTemplateValidationMessage(string action, string? templateKey, string? message)
    {
        var sanitized = SanitizeStrategyTemplateMessage(message);
        if (ContainsJsonKeyword(sanitized))
        {
            return $"JSON uretilemedi/dogrulanamadi: {ResolveTemplateActionLabel(action)}. {sanitized}";
        }

        return $"Validation blocked: {ResolveTemplateActionLabel(action)}. {sanitized}";
    }

    private static string BuildStrategyTemplateOperationMessage(string action, string? templateKey, string? message)
    {
        var sanitized = SanitizeStrategyTemplateMessage(message);
        var actionLabel = ResolveTemplateActionLabel(action);
        var templateLabel = string.IsNullOrWhiteSpace(templateKey) ? null : $" Template={templateKey}.";
        return $"{actionLabel} tamamlanamadi.{templateLabel ?? string.Empty} {sanitized}".Trim();
    }

    private static string BuildStrategyTemplateGenericFailureMessage(string action, string? templateKey)
    {
        var templateLabel = string.IsNullOrWhiteSpace(templateKey) ? string.Empty : $" Template={templateKey}.";
        return $"{ResolveTemplateActionLabel(action)} tamamlanamadi.{templateLabel} Teknik hata nedeniyle islem sonuclanamadi.".Trim();
    }

    private static string BuildStrategyTemplateInputMessage(string action, string message)
    {
        return $"{ResolveTemplateActionLabel(action)}: {SanitizeStrategyTemplateMessage(message)}";
    }

    private static string ResolveTemplateActionLabel(string? action)
    {
        return action?.Trim() switch
        {
            "Create" => "Create islemi",
            "Revise" => "Revise islemi",
            "Save" => "Guncelleme islemi",
            "Publish" => "Publish islemi",
            "Archive" => "Archive islemi",
            "Catalog" => "Catalog islemi",
            "Detail" => "Detay ekrani",
            _ => "Strategy template islemi"
        };
    }

    private static bool ContainsJsonKeyword(string message)
    {
        return message.Contains("json", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("definition", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("schema", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("parse", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeStrategyTemplateMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Strategy template islemi tamamlanamadi.";
        }

        var normalizedMessage = message
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        return Truncate(normalizedMessage, 256) ?? "Strategy template islemi tamamlanamadi.";
    }

    private void ApplyShellMeta(string title, string description, string activeNav, string[] breadcrumbItems)
    {
        var roleKey = ResolveAdminRoleKey();
        var freshness = ResolveFreshnessMeta(activeNav, title);

        ViewData["Title"] = title;
        ViewData["PageDescription"] = description;
        ViewData["AdminActiveNav"] = activeNav;
        ViewData["BreadcrumbItems"] = breadcrumbItems;
        ViewData["AdminEnvironment"] = "Prod Shadow";
        ViewData["AdminBuild"] = "Build vNext";
        ViewData["AdminTenantScope"] = "Tenant: Global";
        ViewData["AdminRoleKey"] = roleKey;
        ViewData["AdminRoleLabel"] = MapRoleLabel(roleKey);
        ViewData[AdminCanEditGlobalPolicyViewDataKey] = CanEditGlobalPolicy();
        ViewData["AdminFreshnessTone"] = freshness.Tone;
        ViewData["AdminFreshnessLabel"] = freshness.Label;
        ViewData["AdminFreshnessMessage"] = freshness.Message;
        ViewData["AdminFreshnessMeta"] = freshness.Meta;
    }


    private string ResolveAdminRoleKey()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return "SuperAdmin";
        }

        if (User.IsInRole(ApplicationRoles.SuperAdmin))
        {
            return "SuperAdmin";
        }

        if (User.IsInRole(ApplicationRoles.OpsAdmin) || User.IsInRole(ApplicationRoles.Admin))
        {
            return "OpsAdmin";
        }

        if (User.IsInRole(ApplicationRoles.SecurityAuditor) ||
            User.IsInRole(ApplicationRoles.Auditor) ||
            User.IsInRole(ApplicationRoles.Support))
        {
            return "SecurityAuditor";
        }

        return "SuperAdmin";
    }

    private static string MapRoleLabel(string roleKey)
    {
        return roleKey switch
        {
            "OpsAdmin" => "Ops Admin",
            "SecurityAuditor" => "Security Auditor",
            _ => "Super Admin"
        };
    }

    private static (string Tone, string Label, string Message, string Meta) ResolveFreshnessMeta(string activeNav, string title)
    {
        return activeNav switch
        {
            "Overview" => ("warning", "Delayed", "Platform özeti read-model üzerinden okunur; kritik karar öncesi modül detayına gidilmesi beklenir.", "Snapshot · 2 dk"),
            "ExchangeAccounts" => ("critical", "Stale", "Exchange permission ve connection görünümü stale olabilir; riskli hesaplarda detay drawer ve audit/log merkezi ile çapraz kontrol önerilir.", "Exchange sync · 4 dk"),
            "SystemHealth" or "Jobs" => ("degraded", "Degraded", "Monitoring read-model background worker üzerinden yenilenir; stale worker ve dependency health sinyalleri birlikte değerlendirilmelidir.", "Heartbeat · live worker"),
            "Audit" or "SecurityEvents" or "Notifications" => ("warning", "Delayed", $"{title} ekranı yüksek hacimli kayıtlarla çalıştığı için son pencere gecikmeli okunabilir.", "Trace window · 3 dk"),
            _ => ("healthy", "Fresh", $"{title} verisi güncel shell snapshot ile gösteriliyor; yine de kritik aksiyon öncesi modül detayları kontrol edilmelidir.", "Freshness · canlı")
        };
    }

    private void ApplyAdminAccessMeta(string title, string description, string stage, string progress, string[] highlights, string? backHref)
    {
        ViewData["Title"] = title;
        ViewData["AuthTitle"] = title;
        ViewData["AuthDescription"] = description;
        ViewData["AuthEyebrow"] = "Super Admin Access";
        ViewData["AuthStage"] = stage;
        ViewData["AuthProgress"] = progress;
        ViewData["AuthHighlights"] = highlights;
        ViewData["AuthBackHref"] = backHref;
    }

    private static AdminPlaceholderPageViewModel CreatePlaceholder(
        string eyebrow,
        string title,
        string description,
        string hintTitle,
        string hintMessage,
        string? primaryActionText,
        string? primaryActionHref,
        string? secondaryActionText,
        string? secondaryActionHref,
        AdminBadgeViewModel statusBadge,
        AdminInfoStripViewModel strip)
    {
        return new AdminPlaceholderPageViewModel
        {
            Eyebrow = eyebrow,
            Title = title,
            Description = description,
            HintTitle = hintTitle,
            HintMessage = hintMessage,
            PrimaryActionText = primaryActionText,
            PrimaryActionHref = primaryActionHref,
            SecondaryActionText = secondaryActionText,
            SecondaryActionHref = secondaryActionHref,
            StatusBadge = statusBadge,
            Strip = strip
        };
    }

    private bool CanEditGlobalPolicy()
    {
        return User.IsInRole(ApplicationRoles.SuperAdmin);
    }

    private bool CanManageApprovals()
    {
        return approvalWorkflowService is not null && User.IsInRole(ApplicationRoles.SuperAdmin);
    }

    private bool CanViewUserDirectory()
    {
        return User.HasClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.IdentityAdministration);
    }

    private async Task<MonitoringDashboardSnapshot> LoadMonitoringDashboardSnapshotAsync(CancellationToken cancellationToken)
    {
        if (adminMonitoringReadModelService is null)
        {
            return MonitoringDashboardSnapshot.Empty(DateTime.UtcNow);
        }

        try
        {
            return await adminMonitoringReadModelService.GetSnapshotAsync(cancellationToken);
        }
        catch
        {
            return MonitoringDashboardSnapshot.Empty(DateTime.UtcNow);
        }
    }

    private async Task<GlobalPolicySnapshot> LoadGlobalPolicySnapshotAsync(CancellationToken cancellationToken)
    {
        if (globalPolicyEngine is null)
        {
            return GlobalPolicySnapshot.CreateDefault(DateTime.UtcNow);
        }

        try
        {
            return await globalPolicyEngine.GetSnapshotAsync(cancellationToken);
        }
        catch
        {
            return GlobalPolicySnapshot.CreateDefault(DateTime.UtcNow);
        }
    }

    private async Task<LogCenterRetentionSnapshot?> LoadLogCenterRetentionSnapshotSafeAsync(CancellationToken cancellationToken)
    {
        if (logCenterRetentionService is null)
        {
            return null;
        }

        try
        {
            return await logCenterRetentionService.GetSnapshotAsync(cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private async Task<LogCenterPageSnapshot?> LoadRolloutLogCenterSnapshotSafeAsync(CancellationToken cancellationToken)
    {
        if (logCenterReadModelService is null)
        {
            return null;
        }

        try
        {
            return await logCenterReadModelService.GetPageAsync(
                new LogCenterQueryRequest(
                    Query: null,
                    CorrelationId: null,
                    DecisionId: null,
                    ExecutionAttemptId: null,
                    UserId: null,
                    Symbol: null,
                    Status: null,
                    FromUtc: null,
                    ToUtc: null,
                    Take: 40),
                cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyCollection<AdminRolloutEvidenceInput> LoadRolloutEvidenceInputs()
    {
        var repositoryRoot = ResolveRepositoryRootPath();
        var diagnosticsRoot = System.IO.Path.Combine(repositoryRoot, ".diag");
        var evidence = new List<AdminRolloutEvidenceInput>();

        AddBooleanRolloutEvidence(evidence, "build-clean", "Build temiz", System.IO.Path.Combine(diagnosticsRoot, "rollout-closure", "build-summary.json"), "dotnet build CoinBot.sln", "BuildClean", "BuildFailed");
        AddBooleanRolloutEvidence(evidence, "unit-tests-clean", "Unit test temiz", System.IO.Path.Combine(diagnosticsRoot, "rollout-closure", "unit-summary.json"), "dotnet test CoinBot.UnitTests", "UnitTestsClean", "UnitTestsFailed");
        AddSettingsSmokeEvidence(evidence, System.IO.Path.Combine(diagnosticsRoot, "settings-browser-smoke", "settings-browser-smoke-summary.json"));
        AddPilotLifecycleEvidence(evidence, FindLatestPilotLifecycleSummaryPath(diagnosticsRoot));
        AddUiLiveKillSwitchEvidence(evidence, System.IO.Path.Combine(diagnosticsRoot, "ui-live-browser-smoke", "ui-live-runtime-smoke-summary.json"));

        return evidence;
    }

    private static void AddBooleanRolloutEvidence(ICollection<AdminRolloutEvidenceInput> evidence, string key, string label, string summaryPath, string sourceLabel, string successReasonCode, string failureReasonCode)
    {
        if (string.IsNullOrWhiteSpace(summaryPath) || !System.IO.File.Exists(summaryPath))
        {
            return;
        }

        using var document = TryLoadJsonDocument(summaryPath!);
        if (document is null)
        {
            evidence.Add(new AdminRolloutEvidenceInput(key, label, false, "EvidenceMalformed", $"{label} icin kanit dosyasi okunamadi.", sourceLabel, System.IO.File.GetLastWriteTimeUtc(summaryPath)));
            return;
        }

        var root = document.RootElement;
        var succeeded = TryReadBoolean(root, "Succeeded");
        var reasonCode = TryReadString(root, "ReasonCode") ?? (succeeded == true ? successReasonCode : failureReasonCode);
        var summary = TryReadString(root, "Summary") ?? $"{label} sonucu okunamadi.";
        var occurredAtUtc = TryReadDateTime(root, "OccurredAtUtc") ?? System.IO.File.GetLastWriteTimeUtc(summaryPath);

        evidence.Add(new AdminRolloutEvidenceInput(key, label, succeeded == true, reasonCode, summary, sourceLabel, occurredAtUtc));
    }

    private static void AddSettingsSmokeEvidence(ICollection<AdminRolloutEvidenceInput> evidence, string summaryPath)
    {
        if (string.IsNullOrWhiteSpace(summaryPath) || !System.IO.File.Exists(summaryPath))
        {
            return;
        }

        using var document = TryLoadJsonDocument(summaryPath!);
        if (document is null)
        {
            evidence.Add(new AdminRolloutEvidenceInput("ui-smoke-clean", "UI smoke temiz", false, "UiSmokeEvidenceMalformed", "Settings browser smoke ozeti okunamadi; rollout UI kaniti dogrulanamadi.", "SettingsBrowserSmoke", System.IO.File.GetLastWriteTimeUtc(summaryPath)));
            return;
        }

        var root = document.RootElement;
        var tabCount = TryReadInt32(root, "adminOverviewTabCount") ?? 0;
        var hasSimpleFlow = TryReadBoolean(root, "adminOverviewHasSimpleFlow") == true;
        var hasSetupSection = TryReadBoolean(root, "adminOverviewHasSetupSection") == true;
        var hasActivationSection = TryReadBoolean(root, "adminOverviewHasActivationSection") == true;
        var hasMonitoringSection = TryReadBoolean(root, "adminOverviewHasMonitoringSection") == true;
        var hasAdvancedSection = TryReadBoolean(root, "adminOverviewHasAdvancedSection") == true;
        var advancedLinkCount = TryReadInt32(root, "adminOverviewAdvancedLinkCount") ?? 0;
        var activationVisible = TryReadBoolean(root, "adminActivationPaneVisible") == true;
        var auditVisible = TryReadBoolean(root, "adminAuditVisible") == true;
        var isPassing = tabCount >= 4 &&
                        hasSimpleFlow &&
                        hasSetupSection &&
                        hasActivationSection &&
                        hasMonitoringSection &&
                        hasAdvancedSection &&
                        advancedLinkCount >= 4 &&
                        activationVisible &&
                        auditVisible;
        var summary = isPassing
            ? "Settings browser smoke sade super admin akis, activation ve audit yuzeylerini dogruladi."
            : $"Overview sade akis smoke incomplete. Tabs={tabCount}; Setup={hasSetupSection}; Activation={hasActivationSection}; Monitoring={hasMonitoringSection}; Advanced={hasAdvancedSection}; AdvancedLinks={advancedLinkCount}; Activation={activationVisible}; Audit={auditVisible}.";

        evidence.Add(new AdminRolloutEvidenceInput("ui-smoke-clean", "UI smoke temiz", isPassing, isPassing ? "UiSmokeClean" : "UiSmokeRolloutSurfaceMissing", summary, "SettingsBrowserSmoke", System.IO.File.GetLastWriteTimeUtc(summaryPath)));
    }

    private static void AddPilotLifecycleEvidence(ICollection<AdminRolloutEvidenceInput> evidence, string? summaryPath)
    {
        if (string.IsNullOrWhiteSpace(summaryPath) || !System.IO.File.Exists(summaryPath))
        {
            return;
        }

        using var document = TryLoadJsonDocument(summaryPath!);
        if (document is null)
        {
            evidence.Add(new AdminRolloutEvidenceInput("pilot-lifecycle-clean", "Pilot lifecycle smoke temiz", false, "PilotLifecycleEvidenceMalformed", "Pilot lifecycle smoke ozeti okunamadi.", "PilotLifecycleRuntimeSmoke", System.IO.File.GetLastWriteTimeUtc(summaryPath)));
            return;
        }

        var root = document.RootElement;
        var brokerSubmitReached = TryReadBoolean(root, "BrokerSubmitReached") == true;
        var cleanupApplied = TryReadBoolean(root, "CleanupApplied") == true;
        var openExecutionOrderCount = TryReadInt32(root, "ScopeOpenExecutionOrderCount") ?? int.MaxValue;
        var openPositionCount = TryReadInt32(root, "ScopeOpenPositionCount") ?? int.MaxValue;
        var executionTraceCount = TryReadInt32(root, "ExecutionTraceCount") ?? 0;
        var finalOrderState = TryReadNestedString(root, "FinalOrder", "State") ?? "Unavailable";
        var isPassing = brokerSubmitReached && cleanupApplied && openExecutionOrderCount == 0 && openPositionCount == 0 && executionTraceCount >= 1 && string.Equals(finalOrderState, "Filled", StringComparison.OrdinalIgnoreCase);
        var summary = isPassing
            ? "Pilot lifecycle smoke broker submit, reconciliation ve cleanup closure ile basarili tamamlandi."
            : $"Pilot lifecycle smoke clean degil. BrokerSubmitReached={brokerSubmitReached}; FinalOrder={finalOrderState}; OpenOrders={openExecutionOrderCount}; OpenPositions={openPositionCount}; ExecutionTraces={executionTraceCount}.";

        evidence.Add(new AdminRolloutEvidenceInput("pilot-lifecycle-clean", "Pilot lifecycle smoke temiz", isPassing, isPassing ? "PilotLifecycleClean" : "PilotLifecycleFailed", summary, "PilotLifecycleRuntimeSmoke", System.IO.File.GetLastWriteTimeUtc(summaryPath)));
    }

    private static void AddUiLiveKillSwitchEvidence(ICollection<AdminRolloutEvidenceInput> evidence, string summaryPath)
    {
        if (string.IsNullOrWhiteSpace(summaryPath) || !System.IO.File.Exists(summaryPath))
        {
            return;
        }

        using var document = TryLoadJsonDocument(summaryPath!);
        if (document is null)
        {
            evidence.Add(new AdminRolloutEvidenceInput("kill-switch-tested", "Kill switch test edildi", false, "KillSwitchEvidenceMalformed", "Ui live smoke ozeti okunamadi; kill switch reject kaniti yok.", "UiLiveBrowserSmoke", System.IO.File.GetLastWriteTimeUtc(summaryPath)));
            return;
        }

        var root = document.RootElement;
        var latestRejectCode = TryReadNestedString(root, "Ui", "home", "latestRejectCode");
        var tradeMasterCode = TryReadNestedString(root, "Ui", "home", "tradeMasterCode");
        var isPassing = string.Equals(latestRejectCode, "TradeMasterDisarmed", StringComparison.OrdinalIgnoreCase) && string.Equals(tradeMasterCode, "Disarmed", StringComparison.OrdinalIgnoreCase);
        var summary = isPassing
            ? "Ui live smoke kill switch reject reasonini TradeMasterDisarmed olarak dogruladi."
            : $"Kill switch smoke kaniti yetersiz. LatestRejectCode={latestRejectCode ?? "Unavailable"}; TradeMasterCode={tradeMasterCode ?? "Unavailable"}.";

        evidence.Add(new AdminRolloutEvidenceInput("kill-switch-tested", "Kill switch test edildi", isPassing, isPassing ? "KillSwitchTested" : "KillSwitchEvidenceFailed", summary, "UiLiveBrowserSmoke", System.IO.File.GetLastWriteTimeUtc(summaryPath)));
    }

    private static string ResolveRepositoryRootPath()
    {
        foreach (var candidate in new[] { System.IO.Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new System.IO.DirectoryInfo(candidate);

            while (current is not null)
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(current.FullName, "CoinBot.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        return System.IO.Directory.GetCurrentDirectory();
    }

    private static string? FindLatestPilotLifecycleSummaryPath(string diagnosticsRoot)
    {
        var pilotRoot = System.IO.Path.Combine(diagnosticsRoot, "pilot-lifecycle-runtime-smoke");
        if (!System.IO.Directory.Exists(pilotRoot))
        {
            return null;
        }

        return System.IO.Directory.EnumerateFiles(pilotRoot, "pilot-lifecycle-runtime-smoke-summary.json", System.IO.SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }

    private static JsonDocument? TryLoadJsonDocument(string path)
    {
        try
        {
            return JsonDocument.Parse(System.IO.File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        return TryGetPropertyIgnoreCase(element, propertyName, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                _ => null
            }
            : null;
    }

    private static bool? TryReadBoolean(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static int? TryReadInt32(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static DateTime? TryReadDateTime(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    private static string? TryReadNestedString(JsonElement element, params string[] path)
    {
        if (!TryResolveElement(element, path, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static bool TryResolveElement(JsonElement element, IReadOnlyList<string> path, out JsonElement value)
    {
        value = element;

        foreach (var segment in path)
        {
            if (!TryGetPropertyIgnoreCase(value, segment, out value))
            {
                value = default;
                return false;
            }
        }

        return true;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(propertyName, out value))
            {
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static RiskPolicySnapshot? TryParsePolicySnapshot(string policyJson, out string? errorMessage)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<RiskPolicySnapshot>(policyJson, PolicyJsonSerializerOptions);

            if (snapshot is null)
            {
                errorMessage = "Policy JSON bos dondu.";
                return null;
            }

            errorMessage = null;
            return snapshot;
        }
        catch (JsonException exception)
        {
            errorMessage = $"Policy JSON okunamadi: {Truncate(exception.Message, 256)}";
            return null;
        }
    }

    private string ResolveAdminUserId()
    {
        var subjectId = User.FindFirstValue(ClaimTypes.NameIdentifier)?.Trim();
        return string.IsNullOrWhiteSpace(subjectId) ? "admin:unknown" : subjectId;
    }

    private string? ResolveAdminEmail()
    {
        var email = User.FindFirstValue(ClaimTypes.Email)?.Trim();
        if (!string.IsNullOrWhiteSpace(email))
        {
            return email;
        }

        var identityName = User.Identity?.Name?.Trim();
        return string.IsNullOrWhiteSpace(identityName) ? null : identityName;
    }

    private string ResolveExecutionActor()
    {
        return $"admin:{ResolveAdminUserId()}";
    }

    private async Task TestBinanceForSetupAsync(string apiKey, string apiSecret, bool isDemo, CancellationToken cancellationToken)
    {
        if (binanceCredentialProbeClient is null)
        {
            TempData[SetupErrorTempDataKey] = "Sistem hazir degil";
            return;
        }

        try
        {
            var probe = await binanceCredentialProbeClient.ProbeAsync(
                apiKey.Trim(),
                apiSecret.Trim(),
                isDemo ? ExecutionEnvironment.Demo : ExecutionEnvironment.Live,
                ExchangeTradeModeSelection.Futures,
                cancellationToken);
            var message = BuildSetupProbeMessage(probe, isDemo);
            TempData[message == "API erisimi basarili" ? SetupSuccessTempDataKey : SetupErrorTempDataKey] = message;
        }
        catch (HttpRequestException)
        {
            TempData[SetupErrorTempDataKey] = "Exchange bagli degil";
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TempData[SetupErrorTempDataKey] = "Exchange bagli degil";
        }
        catch (JsonException)
        {
            TempData[SetupErrorTempDataKey] = "API erisimi basarisiz";
        }
    }

    private static string BuildSetupProbeMessage(BinanceCredentialProbeSnapshot probe, bool isDemo)
    {
        if (!probe.IsKeyValid || probe.HasTimestampSkew || probe.HasIpRestrictionIssue)
        {
            return "API erisimi basarisiz";
        }

        var requestedEnvironmentLabel = isDemo ? "Demo" : "Live";

        if (!string.Equals(probe.FuturesEnvironmentScope, requestedEnvironmentLabel, StringComparison.OrdinalIgnoreCase))
        {
            return "Secilen ortam uygun degil";
        }

        return probe.SupportsFutures ? "API erisimi basarili" : "Futures erisimi dogrulanamadi";
    }
    private static string BuildSetupConnectionMessage(ConnectUserBinanceCredentialResult result)
    {
        if (result.IsValid)
        {
            return "Binance baglantisi dogrulandi";
        }

        var message = $"{result.SafeFailureReason} {result.UserMessage}";

        if (message.Contains("Futures", StringComparison.OrdinalIgnoreCase))
        {
            return "Futures erisimi dogrulanamadi";
        }

        if (message.Contains("ortam", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("environment", StringComparison.OrdinalIgnoreCase))
        {
            return "Secilen ortam uygun degil";
        }

        if (message.Contains("ulas", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("zaman asimi", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "Exchange bagli degil";
        }

        return "API erisimi basarisiz";
    }

    private static string? NormalizeRequiredReason(string? reason)
    {
        return NormalizeRequiredInput(reason, 256);
    }

    private static string? NormalizeRequiredInput(string? value, int maxLength)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue) || normalizedValue.Length > maxLength)
        {
            return null;
        }

        return normalizedValue;
    }

    private static string? NormalizeOptionalInput(string? value, int maxLength)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return null;
        }

        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : normalizedValue[..maxLength];
    }


    private static string ResolveCommandId(string? commandId)
    {
        var normalizedCommandId = commandId?.Trim();
        return string.IsNullOrWhiteSpace(normalizedCommandId)
            ? Guid.NewGuid().ToString("N")
            : normalizedCommandId;
    }

    private static IReadOnlyCollection<SymbolRestriction>? TryNormalizeSymbolRestrictions(
        IReadOnlyCollection<AdminSymbolRestrictionInputModel>? restrictions,
        IReadOnlyCollection<SymbolRestriction> currentRestrictions,
        string actorUserId,
        DateTime utcNow,
        out string? errorMessage)
    {
        errorMessage = null;
        var normalizedRestrictions = new List<SymbolRestriction>();
        var currentRestrictionsBySymbol = currentRestrictions.ToDictionary(
            item => item.Symbol,
            StringComparer.Ordinal);
        var seenSymbols = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in restrictions ?? Array.Empty<AdminSymbolRestrictionInputModel>())
        {
            var hasAnyValue =
                !string.IsNullOrWhiteSpace(candidate.Symbol) ||
                !string.IsNullOrWhiteSpace(candidate.State) ||
                !string.IsNullOrWhiteSpace(candidate.Reason);

            if (!hasAnyValue)
            {
                continue;
            }

            var normalizedSymbol = NormalizeRequiredInput(candidate.Symbol, 32)?.ToUpperInvariant();

            if (normalizedSymbol is null)
            {
                errorMessage = "Her restriction icin gecerli bir symbol zorunludur.";
                return null;
            }

            if (!seenSymbols.Add(normalizedSymbol))
            {
                errorMessage = $"Duplicate restriction symbol tespit edildi: {normalizedSymbol}.";
                return null;
            }

            var normalizedState = NormalizeRequiredInput(candidate.State, 32);

            if (!Enum.TryParse<SymbolRestrictionState>(normalizedState, ignoreCase: true, out var parsedState))
            {
                errorMessage = $"Restriction state gecersiz: {normalizedSymbol}.";
                return null;
            }

            var normalizedRestrictionReason = NormalizeRequiredInput(candidate.Reason, 256);

            if (normalizedRestrictionReason is null)
            {
                errorMessage = $"Reason zorunludur: {normalizedSymbol}.";
                return null;
            }

            if (currentRestrictionsBySymbol.TryGetValue(normalizedSymbol, out var currentRestriction) &&
                currentRestriction.State == parsedState &&
                string.Equals(currentRestriction.Reason, normalizedRestrictionReason, StringComparison.Ordinal))
            {
                normalizedRestrictions.Add(currentRestriction);
                continue;
            }

            normalizedRestrictions.Add(new SymbolRestriction(
                normalizedSymbol,
                parsedState,
                normalizedRestrictionReason,
                utcNow,
                actorUserId));
        }

        return normalizedRestrictions
            .OrderBy(item => item.Symbol, StringComparer.Ordinal)
            .ToArray();
    }

    private static string BuildSymbolRestrictionSummary(IReadOnlyCollection<SymbolRestriction> restrictions)
    {
        if (restrictions.Count == 0)
        {
            return "Restrictions=0";
        }

        var preview = string.Join(
            "; ",
            restrictions.Take(4).Select(item => $"{item.Symbol}={item.State}"));

        return restrictions.Count > 4
            ? $"Restrictions={restrictions.Count}; {preview}; more={restrictions.Count - 4}"
            : $"Restrictions={restrictions.Count}; {preview}";
    }

    private static string CreatePayloadHash(string rawPayload)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload));
        return Convert.ToHexStringLower(hash);
    }

    private async Task<bool> HandleCommandStartResultAsync(
        AdminCommandStartResult result,
        string actorUserId,
        string actionType,
        string targetType,
        string targetId,
        string requestedSummary,
        string reason,
        string successTempDataKey,
        string errorTempDataKey,
        CancellationToken cancellationToken)
    {
        switch (result.Disposition)
        {
            case AdminCommandStartDisposition.Started:
                return true;
            case AdminCommandStartDisposition.AlreadyCompleted:
            {
                var message = string.IsNullOrWhiteSpace(result.ResultSummary)
                    ? "Command already completed for the same payload."
                    : result.ResultSummary!;
                var tempDataKey = result.PersistedStatus == AdminCommandStatus.Completed
                    ? successTempDataKey
                    : errorTempDataKey;

                TempData[tempDataKey] = message;
                await adminAuditLogService.WriteAsync(
                    BuildAdminAuditLogWriteRequest(
                        actorUserId,
                        $"{actionType}.IdempotentHit",
                        targetType,
                        targetId,
                        oldValueSummary: null,
                        newValueSummary: requestedSummary,
                        reason,
                        HttpContext.TraceIdentifier),
                    cancellationToken);

                return false;
            }
            case AdminCommandStartDisposition.AlreadyRunning:
                TempData[errorTempDataKey] = "Command already running for the same CommandId.";
                await adminAuditLogService.WriteAsync(
                    BuildAdminAuditLogWriteRequest(
                        actorUserId,
                        $"{actionType}.AlreadyRunning",
                        targetType,
                        targetId,
                        oldValueSummary: null,
                        newValueSummary: requestedSummary,
                        reason,
                        HttpContext.TraceIdentifier),
                    cancellationToken);
                return false;
            default:
                TempData[errorTempDataKey] = "CommandId collision detected with a different payload.";
                await adminAuditLogService.WriteAsync(
                    BuildAdminAuditLogWriteRequest(
                        actorUserId,
                        $"{actionType}.PayloadConflict",
                        targetType,
                        targetId,
                        oldValueSummary: null,
                        newValueSummary: requestedSummary,
                        reason,
                        HttpContext.TraceIdentifier),
                    cancellationToken);
                return false;
        }
    }

    private AdminAuditLogWriteRequest BuildAdminAuditLogWriteRequest(
        string actorUserId,
        string actionType,
        string targetType,
        string? targetId,
        string? oldValueSummary,
        string? newValueSummary,
        string reason,
        string? correlationId)
    {
        return new AdminAuditLogWriteRequest(
            actorUserId,
            actionType,
            targetType,
            targetId,
            Truncate(oldValueSummary, 2048),
            Truncate(newValueSummary, 2048),
            Truncate(reason, 512) ?? "Administrative action",
            ResolveMaskedRemoteIpAddress(),
            ResolveMaskedUserAgent(),
            correlationId);
    }

    private string? ResolveMaskedRemoteIpAddress()
    {
        return AdminRequestValueMasker.MaskIpAddress(HttpContext.Connection.RemoteIpAddress?.ToString());
    }

    private string? ResolveMaskedUserAgent()
    {
        return AdminRequestValueMasker.MaskUserAgent(Request.Headers["User-Agent"].ToString());
    }

    private AdminCrisisEscalationPreviewViewModel? LoadCrisisPreviewViewModelFromTempData()
    {
        if (TempData[CrisisPreviewTempDataKey] is not string serializedPayload ||
            string.IsNullOrWhiteSpace(serializedPayload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AdminCrisisEscalationPreviewViewModel>(
                serializedPayload,
                PolicyJsonSerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void StoreCrisisPreviewViewModelInTempData(AdminCrisisEscalationPreviewViewModel viewModel)
    {
        TempData[CrisisPreviewTempDataKey] = JsonSerializer.Serialize(viewModel, PolicyJsonSerializerOptions);
    }

    private static string BuildSwitchContext(string scope, string reason, string commandId)
    {
        var context = $"AdminSettings.{scope} | CommandId={commandId} | Reason={reason}";

        return context.Length <= 512
            ? context
            : context[..512];
    }

    private static string ResolveDefaultCrisisReasonCode(CrisisEscalationLevel level)
    {
        return level switch
        {
            CrisisEscalationLevel.SoftHalt => "CRISIS_SOFT_HALT",
            CrisisEscalationLevel.OrderPurge => "CRISIS_ORDER_PURGE",
            CrisisEscalationLevel.EmergencyFlatten => "CRISIS_EMERGENCY_FLATTEN",
            _ => "CRISIS_ACTION"
        };
    }

    private string ResolvePilotOrderNotionalSummary()
    {
        if (!pilotOptionsValue.HasConfiguredMaxPilotOrderNotional())
        {
            return "Missing";
        }

        return pilotOptionsValue.TryResolveMaxPilotOrderNotional(out var maxPilotOrderNotional) && maxPilotOrderNotional > 0m
            ? maxPilotOrderNotional.ToString("0.##", CultureInfo.InvariantCulture)
            : $"Invalid ({Truncate(pilotOptionsValue.MaxPilotOrderNotional, 32) ?? "n/a"})";
    }

    private string ResolvePilotOrderNotionalTone()
    {
        return pilotOptionsValue.TryResolveMaxPilotOrderNotional(out var maxPilotOrderNotional) && maxPilotOrderNotional > 0m
            ? "healthy"
            : "critical";
    }

    private string ResolveLongRegimePolicyStatus()
    {
        return pilotOptionsValue.IsRegimeAwareEntryDisciplineEnabled(StrategyTradeDirection.Long)
            ? "Enabled"
            : "Disabled";
    }

    private string ResolveLongRegimePolicyTone()
    {
        return pilotOptionsValue.IsRegimeAwareEntryDisciplineEnabled(StrategyTradeDirection.Long)
            ? "healthy"
            : "warning";
    }

    private string ResolveLongRegimePolicyDetail()
    {
        return pilotOptionsValue.BuildRegimeThresholdSummary(StrategyTradeDirection.Long) +
               " | Runtime block ozetleri canli RSI/MACD/Bollinger width degerlerini ayni decision summary alaninda yazar.";
    }

    private ClockDriftInfoViewModel BuildClockDriftInfoViewModel(BinanceTimeSyncSnapshot snapshot)
    {
        return new ClockDriftInfoViewModel(
            "UTC",
            "UTC",
            FormatOperationalTimestamp(snapshot.LocalAppTimeUtc),
            FormatOperationalTimestamp(snapshot.ExchangeServerTimeUtc),
            $"{snapshot.OffsetMilliseconds} ms",
            snapshot.ClockDriftMilliseconds is int clockDriftMilliseconds
                ? $"{clockDriftMilliseconds} ms"
                : "Henüz yok",
            FormatOperationalTimestamp(snapshot.LastSynchronizedAtUtc),
            snapshot.StatusCode,
            snapshot.FailureReason,
            snapshot.RoundTripMilliseconds is int roundTripMilliseconds
                ? $"{roundTripMilliseconds} ms"
                : "Henüz yok",
            $"{privateDataOptions.ServerTimeSyncRefreshSeconds} sn");
    }

    private MarketDriftGuardInfoViewModel BuildMarketDriftGuardInfoViewModel(DegradedModeSnapshot snapshot)
    {
        var clockDriftThresholdMilliseconds = checked(dataLatencyGuardOptions.ClockDriftThresholdSeconds * 1000);

        return new MarketDriftGuardInfoViewModel(
            $"{clockDriftThresholdMilliseconds} ms",
            $"{snapshot.StateCode} • {(snapshot.ExecutionFlowBlocked ? "Execution blocked" : "Execution open")}",
            BuildGuardReason(snapshot, clockDriftThresholdMilliseconds),
            FormatOperationalTimestamp(snapshot.LatestHeartbeatReceivedAtUtc),
            FormatOperationalTimestamp(snapshot.LatestDataTimestampAtUtc),
            snapshot.LatestDataAgeMilliseconds is int latestDataAgeMilliseconds
                ? $"{latestDataAgeMilliseconds} ms"
                : "Henüz yok",
            snapshot.LatestClockDriftMilliseconds is int latestClockDriftMilliseconds
                ? $"{latestClockDriftMilliseconds} ms"
                : "Henüz yok",
            FormatOperationalTimestamp(snapshot.LastStateChangedAtUtc),
            "Market-data heartbeat (binance:kline)",
            BuildRetryExpectation(snapshot));
    }

    private static string BuildGuardReason(DegradedModeSnapshot snapshot, int clockDriftThresholdMilliseconds)
    {
        return snapshot.ReasonCode switch
        {
            DegradedModeReasonCode.None when snapshot.IsNormal =>
                $"Guard normal. Threshold {clockDriftThresholdMilliseconds} ms, latest heartbeat drift {(snapshot.LatestClockDriftMilliseconds?.ToString() ?? "n/a")} ms.",
            DegradedModeReasonCode.ClockDriftExceeded =>
                $"Clock drift block aktif. Market-data heartbeat drift {(snapshot.LatestClockDriftMilliseconds?.ToString() ?? "n/a")} ms, threshold {clockDriftThresholdMilliseconds} ms.",
            DegradedModeReasonCode.MarketDataLatencyBreached or DegradedModeReasonCode.MarketDataLatencyCritical =>
                $"Market-data freshness guard aktif. Data age {(snapshot.LatestDataAgeMilliseconds?.ToString() ?? "n/a")} ms, latest heartbeat drift {(snapshot.LatestClockDriftMilliseconds?.ToString() ?? "n/a")} ms.",
            DegradedModeReasonCode.MarketDataUnavailable =>
                "Market-data heartbeat henüz güvenli kabul edilecek kadar gelmedi.",
            _ =>
                $"Guard reason {snapshot.ReasonCode}. Latest heartbeat drift {(snapshot.LatestClockDriftMilliseconds?.ToString() ?? "n/a")} ms."
        };
    }

    private static string BuildRetryExpectation(DegradedModeSnapshot snapshot)
    {
        return snapshot.ReasonCode switch
        {
            DegradedModeReasonCode.ClockDriftExceeded =>
                "Server-time refresh yalnız signed REST offset'ini yeniler. Yeni order için market-data heartbeat drift'inin threshold altına inmesi gerekir.",
            DegradedModeReasonCode.MarketDataLatencyBreached or DegradedModeReasonCode.MarketDataLatencyCritical or DegradedModeReasonCode.MarketDataUnavailable =>
                "Retry öncesi fresh kline heartbeat beklenmelidir; yalnız server-time refresh bu blokları tek başına kaldırmaz.",
            _ =>
                "Server-time refresh sonrası signed REST timestamp yeniden senkronlanır. Guard normal ise sonraki retry order path'e ilerleyebilir."
        };
    }

    private bool CanRefreshClockDrift()
    {
        return User.HasClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.PlatformAdministration);
    }

    private static string FormatOperationalTimestamp(DateTime? utcTimestamp)
    {
        if (!utcTimestamp.HasValue)
        {
            return "Henüz yok";
        }

        var normalizedUtcTimestamp = utcTimestamp.Value.Kind == DateTimeKind.Utc
            ? utcTimestamp.Value
            : DateTime.SpecifyKind(utcTimestamp.Value, DateTimeKind.Utc);

        return $"{normalizedUtcTimestamp:yyyy-MM-dd HH:mm:ss} UTC";
    }

    private static string BuildExecutionSwitchSummary(GlobalExecutionSwitchSnapshot snapshot)
    {
        return
            $"TradeMaster={(snapshot.IsTradeMasterArmed ? "Armed" : "Disarmed")}; DemoMode={(snapshot.DemoModeEnabled ? "Enabled" : "Disabled")}; Effective={snapshot.EffectiveEnvironment}; Persisted={snapshot.IsPersisted}; LiveApprovedAtUtc={snapshot.LiveModeApprovedAtUtc?.ToString("O") ?? "none"}";
    }

    private static string BuildRequestedDemoModeSummary(bool isEnabled, string? liveApprovalReference)
    {
        return
            $"DemoMode={(isEnabled ? "Enabled" : "Disabled")}; LiveApprovalReference={liveApprovalReference ?? "none"}";
    }

    private static string BuildGlobalSystemStateSummary(GlobalSystemStateSnapshot snapshot)
    {
        return
            $"State={snapshot.State}; ReasonCode={snapshot.ReasonCode}; Message={Truncate(snapshot.Message, 128) ?? "none"}; Source={snapshot.Source}; Manual={snapshot.IsManualOverride}; ExpiresAtUtc={snapshot.ExpiresAtUtc?.ToString("O") ?? "none"}; Version={snapshot.Version}; UpdatedAtUtc={snapshot.UpdatedAtUtc?.ToString("O") ?? "none"}";
    }

    private static string BuildRequestedGlobalSystemStateSummary(
        GlobalSystemStateKind state,
        string reasonCode,
        string? message,
        DateTime? expiresAtUtc)
    {
        return
            $"State={state}; ReasonCode={reasonCode}; Message={Truncate(message, 128) ?? "none"}; ExpiresAtUtc={expiresAtUtc?.ToUniversalTime().ToString("O") ?? "none"}; Manual=True";
    }

    private static string BuildRequestedCrisisSummary(
        CrisisEscalationLevel level,
        string scope,
        string reasonCode,
        string? message,
        string previewStamp)
    {
        return
            $"Level={level}; Scope={scope}; ReasonCode={reasonCode}; Message={Truncate(message, 128) ?? "none"}; PreviewStamp={previewStamp}";
    }

    private static string BuildPolicySummary(RiskPolicySnapshot policy)
    {
        return
            $"PolicyKey={policy.PolicyKey}; Autonomy={policy.AutonomyPolicy.Mode}; RequireManualApprovalForLive={policy.AutonomyPolicy.RequireManualApprovalForLive}; MaxOrderNotional={policy.ExecutionGuardPolicy.MaxOrderNotional?.ToString("0.##") ?? "none"}; MaxPositionNotional={policy.ExecutionGuardPolicy.MaxPositionNotional?.ToString("0.##") ?? "none"}; MaxDailyTrades={policy.ExecutionGuardPolicy.MaxDailyTrades?.ToString() ?? "none"}; CloseOnlyBlocksNewPositions={policy.ExecutionGuardPolicy.CloseOnlyBlocksNewPositions}; Restrictions={policy.SymbolRestrictions.Count}";
    }

    private bool HasAdminPortalAccess()
    {
        return User.HasClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.AdminPortalAccess);
    }

    private async Task<IActionResult?> EnforcePlatformAdminMfaAsync(
        string operationKey,
        string errorTempDataKey,
        string redirectAction,
        object? routeValues,
        CancellationToken cancellationToken)
    {
        var adminUserId = ResolveAdminUserId();
        var authorization = await criticalUserOperationAuthorizer.AuthorizeAsync(
            new CriticalUserOperationAuthorizationRequest(
                adminUserId,
                $"admin:{adminUserId}",
                operationKey,
                $"Admin/{operationKey}",
                HttpContext.TraceIdentifier),
            cancellationToken);

        if (authorization.IsAuthorized)
        {
            return null;
        }

        TempData[errorTempDataKey] = authorization.FailureReason ?? "Bu yonetim islemi icin MFA zorunludur.";
        return RedirectToAction(redirectAction, routeValues);
    }

    private static string ResolveOperationalFlowReturnAction(string? returnTarget)
    {
        return string.Equals(returnTarget, nameof(Overview), StringComparison.OrdinalIgnoreCase)
            ? nameof(Overview)
            : nameof(Settings);
    }
    private static string NormalizeAdminReturnUrl(string? returnUrl)
    {
        var normalizedValue = string.IsNullOrWhiteSpace(returnUrl)
            ? "/admin"
            : returnUrl.Trim();

        if (!IsSafeLocalReturnUrl(normalizedValue))
        {
            return "/admin";
        }

        return IsAuthShellRoute(normalizedValue)
            ? "/admin"
            : normalizedValue;
    }

    private static bool IsSafeLocalReturnUrl(string returnUrl)
    {
        return returnUrl.StartsWith("/", StringComparison.Ordinal) &&
               !returnUrl.StartsWith("//", StringComparison.Ordinal) &&
               !returnUrl.StartsWith("/\\", StringComparison.Ordinal);
    }

    private static bool IsAuthShellRoute(string returnUrl)
    {
        var routePath = returnUrl.Split(['?', '#'], StringSplitOptions.None)[0];

        return routePath.Equals("/Auth/Login", StringComparison.OrdinalIgnoreCase) ||
               routePath.Equals("/Auth/Mfa", StringComparison.OrdinalIgnoreCase) ||
               routePath.Equals("/Auth/AccessDenied", StringComparison.OrdinalIgnoreCase) ||
               routePath.Equals("/Admin/Admin/Login", StringComparison.OrdinalIgnoreCase) ||
               routePath.Equals("/Admin/Admin/Mfa", StringComparison.OrdinalIgnoreCase) ||
               routePath.Equals("/Admin/Admin/AccessDenied", StringComparison.OrdinalIgnoreCase) ||
               routePath.Equals("/Admin/Admin/PermissionDenied", StringComparison.OrdinalIgnoreCase) ||
               routePath.Equals("/Admin/Admin/SessionExpired", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }
}
