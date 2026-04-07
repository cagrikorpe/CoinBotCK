using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Contracts.Common;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Mfa;
using CoinBot.Web.ViewModels.Admin;
using CoinBot.Web.ViewModels.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    private const string GlobalPolicySnapshotViewDataKey = "AdminGlobalPolicySnapshot";
    private const string GlobalPolicySuccessTempDataKey = "AdminGlobalPolicySuccess";
    private const string GlobalPolicyErrorTempDataKey = "AdminGlobalPolicyError";
    private const string AdminLogCenterRetentionSnapshotViewDataKey = "AdminLogCenterRetentionSnapshot";
    private const string ClockDriftSnapshotViewDataKey = "AdminClockDriftSnapshot";
    private const string DriftGuardSnapshotViewDataKey = "AdminDriftGuardSnapshot";
    private const string CanRefreshClockDriftViewDataKey = "AdminCanRefreshClockDrift";
    private const string ClockDriftSuccessTempDataKey = "AdminClockDriftSuccess";
    private const string ClockDriftErrorTempDataKey = "AdminClockDriftError";
    private const string ApprovalSuccessTempDataKey = "AdminApprovalSuccess";
    private const string ApprovalErrorTempDataKey = "AdminApprovalError";
    private const string CrisisPreviewViewDataKey = "AdminCrisisEscalationPreview";
    private const string CrisisSuccessTempDataKey = "AdminCrisisEscalationSuccess";
    private const string CrisisErrorTempDataKey = "AdminCrisisEscalationError";
    private const string CrisisPreviewTempDataKey = "AdminCrisisEscalationPreviewState";
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
    private readonly ILogCenterRetentionService? logCenterRetentionService;
    private readonly IGlobalPolicyEngine? globalPolicyEngine;
    private readonly IGlobalExecutionSwitchService globalExecutionSwitchService;
    private readonly IGlobalSystemStateService globalSystemStateService;
    private readonly IBinanceTimeSyncService binanceTimeSyncService;
    private readonly IDataLatencyCircuitBreaker dataLatencyCircuitBreaker;
    private readonly DataLatencyGuardOptions dataLatencyGuardOptions;
    private readonly BinancePrivateDataOptions privateDataOptions;
    private readonly ITraceService traceService;
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
        ICriticalUserOperationAuthorizer criticalUserOperationAuthorizer,
        IApprovalWorkflowService? approvalWorkflowService = null,
        IAdminGovernanceReadModelService? adminGovernanceReadModelService = null,
        IAdminMonitoringReadModelService? adminMonitoringReadModelService = null,
        ILogCenterRetentionService? logCenterRetentionService = null,
        IGlobalPolicyEngine? globalPolicyEngine = null,
        ICrisisEscalationService? crisisEscalationService = null,
        IOptions<BotExecutionPilotOptions>? botExecutionPilotOptions = null,
        IOptions<DataLatencyGuardOptions>? dataLatencyGuardOptions = null,
        IOptions<BinancePrivateDataOptions>? privateDataOptions = null)
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
        this.criticalUserOperationAuthorizer = criticalUserOperationAuthorizer;
        this.approvalWorkflowService = approvalWorkflowService;
        this.adminGovernanceReadModelService = adminGovernanceReadModelService;
        this.adminMonitoringReadModelService = adminMonitoringReadModelService;
        this.logCenterRetentionService = logCenterRetentionService;
        this.globalPolicyEngine = globalPolicyEngine;
        this.crisisEscalationService = crisisEscalationService;
        this.dataLatencyGuardOptions = dataLatencyGuardOptions?.Value ?? new DataLatencyGuardOptions();
        this.privateDataOptions = privateDataOptions?.Value ?? new BinancePrivateDataOptions();
        pilotOptionsValue = botExecutionPilotOptions?.Value ?? new BotExecutionPilotOptions();
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

            var searchResults = await traceService.SearchAsync(
                new AdminTraceSearchRequest(
                    Query: normalizedQuery,
                    Take: 20),
                cancellationToken);

            var exactCorrelationMatch = searchResults.FirstOrDefault(row =>
                string.Equals(row.CorrelationId, normalizedQuery, StringComparison.OrdinalIgnoreCase));
            var parsedExecutionOrderId = Guid.TryParse(normalizedQuery, out var executionOrderId)
                ? executionOrderId
                : (Guid?)null;

            if (exactCorrelationMatch is not null)
            {
                return RedirectToAction(
                    nameof(TraceDetail),
                    "Admin",
                    new
                    {
                        area = "Admin",
                        correlationId = exactCorrelationMatch.CorrelationId
                    });
            }

            foreach (var row in searchResults)
            {
                var detail = await traceService.GetDetailAsync(
                    row.CorrelationId,
                    cancellationToken: cancellationToken);

                if (detail is null)
                {
                    continue;
                }

                var matchingDecision = detail.DecisionTraces.FirstOrDefault(decision =>
                    string.Equals(decision.DecisionId, normalizedQuery, StringComparison.OrdinalIgnoreCase));

                if (matchingDecision is not null)
                {
                    return RedirectToAction(
                        nameof(TraceDetail),
                        "Admin",
                        new
                        {
                            area = "Admin",
                            correlationId = row.CorrelationId,
                            decisionId = matchingDecision.DecisionId
                        });
                }

                var matchingExecution = detail.ExecutionTraces.FirstOrDefault(execution =>
                    string.Equals(execution.ExecutionAttemptId, normalizedQuery, StringComparison.OrdinalIgnoreCase));

                if (matchingExecution is not null)
                {
                    return RedirectToAction(
                        nameof(TraceDetail),
                        "Admin",
                        new
                        {
                            area = "Admin",
                            correlationId = row.CorrelationId,
                            executionAttemptId = matchingExecution.ExecutionAttemptId
                        });
                }

                if (parsedExecutionOrderId.HasValue)
                {
                    var matchingExecutionOrder = detail.ExecutionTraces.FirstOrDefault(execution =>
                        execution.ExecutionOrderId == parsedExecutionOrderId.Value);

                    if (matchingExecutionOrder is not null)
                    {
                        return RedirectToAction(
                            nameof(TraceDetail),
                            "Admin",
                            new
                            {
                                area = "Admin",
                                correlationId = row.CorrelationId,
                                executionAttemptId = matchingExecutionOrder.ExecutionAttemptId
                            });
                    }
                }
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

    public IActionResult Overview()
    {
        ApplyShellMeta(
            title: "Genel Bakış",
            description: "Kullanıcı, exchange, bot, AI, alarm ve worker özetini tek ekranda toplayan operasyon dashboard'u.",
            activeNav: "Overview",
            breadcrumbItems: new[] { "Super Admin", "Genel Bakış" });

        return View();
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
    public async Task<IActionResult> Audit(
        string? query,
        string? correlationId,
        string? decisionId,
        string? executionAttemptId,
        string? userId,
        CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Audit & Log Merkezi",
            description: "Platform genelindeki audit, security, runtime, AI, trading ve admin action loglarını gelişmiş filtreler ve trace detay sayfalarıyla izleyen global log merkezi.",
            activeNav: "Audit",
            breadcrumbItems: new[] { "Super Admin", "Gözlem", "Audit & Log" });

        var normalizedQuery = NormalizeOptionalInput(query, 128);
        ViewData["AdminTraceQuery"] = normalizedQuery;

        var traces = await traceService.SearchAsync(
            new AdminTraceSearchRequest(
                normalizedQuery,
                NormalizeOptionalInput(correlationId, 128),
                NormalizeOptionalInput(decisionId, 64),
                NormalizeOptionalInput(executionAttemptId, 64),
                NormalizeOptionalInput(userId, 450),
                Take: 50),
            cancellationToken);

        return View(traces);
    }

    [Authorize(Policy = ApplicationPolicies.AuditRead)]
    public async Task<IActionResult> TraceDetail(
        string correlationId,
        string? decisionId,
        string? executionAttemptId,
        CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Trace Detail",
            description: "CorrelationId bazli signal, decision ve execution zincirini detay seviyesinde izleyen masked admin detail ekranı.",
            activeNav: "Audit",
            breadcrumbItems: new[] { "Super Admin", "Gözlem", "Trace Detail" });

        var detail = await traceService.GetDetailAsync(
            correlationId,
            NormalizeOptionalInput(decisionId, 64),
            NormalizeOptionalInput(executionAttemptId, 64),
            cancellationToken);

        return detail is null
            ? NotFound()
            : View(detail);
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

        ViewData[ExecutionSwitchSnapshotViewDataKey] = await globalExecutionSwitchService.GetSnapshotAsync(cancellationToken);
        ViewData[GlobalSystemStateSnapshotViewDataKey] = await globalSystemStateService.GetSnapshotAsync(cancellationToken);
        ViewData[GlobalPolicySnapshotViewDataKey] = await LoadGlobalPolicySnapshotAsync(cancellationToken);
        if (logCenterRetentionService is not null)
        {
            ViewData[AdminLogCenterRetentionSnapshotViewDataKey] = await logCenterRetentionService.GetSnapshotAsync(cancellationToken);
        }

        var clockDriftSnapshot = await binanceTimeSyncService.GetSnapshotAsync(cancellationToken: cancellationToken);
        var driftGuardSnapshot = await dataLatencyCircuitBreaker.GetSnapshotAsync(HttpContext.TraceIdentifier, cancellationToken: cancellationToken);
        ViewData[ClockDriftSnapshotViewDataKey] = BuildClockDriftInfoViewModel(clockDriftSnapshot);
        ViewData[DriftGuardSnapshotViewDataKey] = BuildMarketDriftGuardInfoViewModel(driftGuardSnapshot);
        ViewData[CanRefreshClockDriftViewDataKey] = CanRefreshClockDrift();
        ViewData[CrisisPreviewViewDataKey] = LoadCrisisPreviewViewModelFromTempData();
        ViewData[PilotOrderNotionalSummaryViewDataKey] = ResolvePilotOrderNotionalSummary();
        ViewData[PilotOrderNotionalToneViewDataKey] = ResolvePilotOrderNotionalTone();
        ViewData[AdminCanEditGlobalPolicyViewDataKey] = CanEditGlobalPolicy();

        return View();
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshClockDrift(CancellationToken cancellationToken)
    {
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

        return RedirectToAction(nameof(Settings));
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

        _ = reauthToken;
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
        CancellationToken cancellationToken)
    {
        var mfaResult = await EnforcePlatformAdminMfaAsync(
            "Admin.Settings.DemoMode.Update",
            ExecutionSwitchErrorTempDataKey,
            nameof(Settings),
            null,
            cancellationToken);

        if (mfaResult is not null)
        {
            return mfaResult;
        }

        _ = reauthToken;
        var normalizedReason = NormalizeRequiredReason(reason);

        if (normalizedReason is null)
        {
            TempData[ExecutionSwitchErrorTempDataKey] = "Audit reason zorunludur.";
            return RedirectToAction(nameof(Settings));
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
            return RedirectToAction(nameof(Settings));
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

        return RedirectToAction(nameof(Settings));
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

        _ = reauthToken;
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
            return RedirectToAction(nameof(Settings));
        }

        var normalizedReason = NormalizeRequiredReason(reason);

        if (normalizedReason is null)
        {
            TempData[GlobalPolicyErrorTempDataKey] = "Audit reason zorunludur.";
            return RedirectToAction(nameof(Settings));
        }

        if (globalPolicyEngine is null)
        {
            TempData[GlobalPolicyErrorTempDataKey] = "Global policy engine is unavailable.";
            return RedirectToAction(nameof(Settings));
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
            return RedirectToAction(nameof(Settings));
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
        var queuePayloadJson = approvalWorkflowService is not null
            ? JsonSerializer.Serialize(policyUpdateRequest, PolicyJsonSerializerOptions)
            : null;
        var payloadHash = queuePayloadJson is not null
            ? CreatePayloadHash(queuePayloadJson)
            : CreatePayloadHash($"GlobalPolicy|SymbolRestrictions|{normalizedReason}|{requestedSummary}");
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
                        "Symbol restriction update",
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

        return RedirectToAction(nameof(Settings));
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

    private string ResolveExecutionActor()
    {
        return $"admin:{ResolveAdminUserId()}";
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













