using System.Linq;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Contracts.Common;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Web.ViewModels.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    private const string GlobalPolicySnapshotViewDataKey = "AdminGlobalPolicySnapshot";
    private const string GlobalPolicySuccessTempDataKey = "AdminGlobalPolicySuccess";
    private const string GlobalPolicyErrorTempDataKey = "AdminGlobalPolicyError";
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
    private readonly IAdminMonitoringReadModelService? adminMonitoringReadModelService;
    private readonly IApprovalWorkflowService? approvalWorkflowService;
    private readonly ICrisisEscalationService? crisisEscalationService;
    private readonly IGlobalPolicyEngine? globalPolicyEngine;
    private readonly IGlobalExecutionSwitchService globalExecutionSwitchService;
    private readonly IGlobalSystemStateService globalSystemStateService;
    private readonly ITraceService traceService;

    public AdminController(
        IGlobalExecutionSwitchService globalExecutionSwitchService,
        IGlobalSystemStateService globalSystemStateService,
        IAdminCommandRegistry adminCommandRegistry,
        IAdminAuditLogService adminAuditLogService,
        ITraceService traceService,
        IApiCredentialValidationService apiCredentialValidationService,
        IApprovalWorkflowService? approvalWorkflowService = null,
        IAdminGovernanceReadModelService? adminGovernanceReadModelService = null,
        IAdminMonitoringReadModelService? adminMonitoringReadModelService = null,
        IGlobalPolicyEngine? globalPolicyEngine = null,
        ICrisisEscalationService? crisisEscalationService = null)
    {
        this.globalExecutionSwitchService = globalExecutionSwitchService;
        this.globalSystemStateService = globalSystemStateService;
        this.adminCommandRegistry = adminCommandRegistry;
        this.adminAuditLogService = adminAuditLogService;
        this.traceService = traceService;
        this.apiCredentialValidationService = apiCredentialValidationService;
        this.approvalWorkflowService = approvalWorkflowService;
        this.adminGovernanceReadModelService = adminGovernanceReadModelService;
        this.adminMonitoringReadModelService = adminMonitoringReadModelService;
        this.globalPolicyEngine = globalPolicyEngine;
        this.crisisEscalationService = crisisEscalationService;
    }

    [AllowAnonymous]
    public IActionResult Login()
    {
        ApplyAdminAccessMeta(
            title: "Admin Login",
            description: "Super Admin paneli için kullanıcı auth yüzeyinden ayrı, daha kontrollü giriş foundation ekranı.",
            stage: "Admin Login",
            progress: "1 / 2",
            highlights: new[]
            {
                "Admin login kartı kullanıcı auth ekranından ayrışır",
                "MFA zorunluluğu ilk ekranda görünür",
                "Validation ve general error alanı güvenlik dili ile sunulur"
            },
            backHref: Url.Action(nameof(Overview), "Admin", new { area = "Admin" }));

        return View();
    }

    [AllowAnonymous]
    public IActionResult Mfa()
    {
        ApplyAdminAccessMeta(
            title: "Admin MFA",
            description: "Admin alanında zorunlu ikinci doğrulama adımı için profesyonel MFA foundation ekranı.",
            stage: "Admin MFA",
            progress: "2 / 2",
            highlights: new[]
            {
                "Authenticator ve email code placeholder",
                "OTP input, resend ve countdown state'leri",
                "Step-up security ile aynı ürün dilini korur"
            },
            backHref: Url.Action(nameof(Login), "Admin", new { area = "Admin" }));

        return View();
    }

    [AllowAnonymous]
    public IActionResult AccessDenied()
    {
        ApplyShellMeta(
            title: "Access Denied",
            description: "Admin alanında yetkisiz erişim durumları için yönlendirici ve profesyonel ekran foundation'ı.",
            activeNav: "Overview",
            breadcrumbItems: new[] { "Super Admin", "Security", "Access Denied" });

        return View();
    }

    [AllowAnonymous]
    public IActionResult PermissionDenied()
    {
        ApplyShellMeta(
            title: "Insufficient Permission",
            description: "Rol var ama kapsam yetersiz olduğunda gösterilecek temiz admin ekranı.",
            activeNav: "Users",
            breadcrumbItems: new[] { "Super Admin", "Security", "Insufficient Permission" });

        return View();
    }

    [AllowAnonymous]
    public IActionResult SessionExpired()
    {
        ApplyShellMeta(
            title: "Session Expired",
            description: "Idle timeout, re-auth ve session boundary uyarıları için admin placeholder ekranı.",
            activeNav: "Overview",
            breadcrumbItems: new[] { "Super Admin", "Security", "Session Expired" });

        return View();
    }

    [Authorize(Policy = ApplicationPolicies.IdentityAdministration)]
    public IActionResult RoleMatrix()
    {
        ApplyShellMeta(
            title: "Role Matrix",
            description: "Rol bazlı kaba erişim görünümü ve permission matrix foundation ekranı.",
            activeNav: "Users",
            breadcrumbItems: new[] { "Super Admin", "Identity", "Role Matrix" });

        return View();
    }

    public IActionResult Search(string? query)
    {
        var normalizedQuery = query?.Trim();

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
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
            description: "CorrelationId, DecisionId, ExecutionAttemptId, IncidentId ve UserId bazli admin global search route stub yuzeyi.",
            activeNav: "Search",
            breadcrumbItems: new[] { "Super Admin", "Search", "Global Search" });

        ViewData["AdminSearchQuery"] = normalizedQuery;

        return View(
            "Placeholder",
            CreatePlaceholder(
                eyebrow: "Search",
                title: "Global Search",
                description: "CorrelationId, DecisionId, ExecutionAttemptId, IncidentId ve UserId icin ortak admin arama giris noktasi hazirlandi. Ilk surum route stub seviyesinde kalir.",
                hintTitle: string.IsNullOrWhiteSpace(normalizedQuery)
                    ? "Aramaya hazir route"
                    : $"Hazir query: {Truncate(normalizedQuery, 48)}",
                hintMessage: string.IsNullOrWhiteSpace(normalizedQuery)
                    ? "Topbar arama kutusu bu route'a baglidir. Gercek index/read-model wiring sonraki fazda eklenecek."
                    : "Ilk surumde query ekrana tasinir; correlation, decision, execution ve incident arama read-model'leri sonraki fazda baglanir.",
                primaryActionText: "Audit merkezi",
                primaryActionHref: Url.Action(nameof(Audit), "Admin", new { area = "Admin" }),
                secondaryActionText: "Sistem sagligi",
                secondaryActionHref: Url.Action(nameof(SystemHealth), "Admin", new { area = "Admin" }),
                statusBadge: new AdminBadgeViewModel
                {
                    Label = string.IsNullOrWhiteSpace(normalizedQuery) ? "Route Stub" : "Prepared Query",
                    Tone = "info",
                    IconText = "GS"
                },
                strip: new AdminInfoStripViewModel
                {
                    Tone = "info",
                    Title = "Search foundation hazir",
                    Message = "Topbar aramasi agir canli sorguya baglanmadan snapshot/read-model katmanina hazirlandi.",
                    Meta = string.IsNullOrWhiteSpace(normalizedQuery)
                        ? "Stub"
                        : $"Q={Truncate(normalizedQuery, 36)}"
                }));
    }

    public IActionResult Overview()
    {
        ApplyShellMeta(
            title: "Genel Bakış",
            description: "Super Admin platform overview; kullanıcı, exchange, bot, AI, alarm ve worker özetini tek ekranda toplayan operasyon dashboard foundation'ı.",
            activeNav: "Overview",
            breadcrumbItems: new[] { "Super Admin", "Genel Bakış" });

        return View();
    }

    [Authorize(Policy = ApplicationPolicies.IdentityAdministration)]
    public IActionResult Users()
    {
        ApplyShellMeta(
            title: "Kullanıcılar",
            description: "Kullanıcı listesi, filtre barı, güvenlik sinyalleri ve kontrollü admin aksiyonları için operasyonel foundation yüzeyi.",
            activeNav: "Users",
            breadcrumbItems: new[] { "Super Admin", "Kimlik", "Kullanıcılar" });

        return View();
    }

    [Authorize(Policy = ApplicationPolicies.IdentityAdministration)]
    public IActionResult UserDetail(string? id)
    {
        ApplyShellMeta(
            title: "Kullanıcı Detayı",
            description: "Profil özeti, güvenlik durumu, bot ve exchange summary ile admin aksiyonlarını bir araya getiren operasyon odaklı detail foundation.",
            activeNav: "UserDetail",
            breadcrumbItems: new[] { "Super Admin", "Kimlik", "Kullanıcı Detayı" });

        ViewData["AdminEntityId"] = string.IsNullOrWhiteSpace(id) ? "usr-placeholder-01" : id;
        ViewData["AdminEntityLabel"] = string.IsNullOrWhiteSpace(id) ? "Placeholder kullanıcı" : $"Kullanıcı {id}";

        return View();
    }

    [Authorize(Policy = ApplicationPolicies.AuditRead)]
    public async Task<IActionResult> ExchangeAccounts(CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Exchange Hesapları",
            description: "Platform çapındaki exchange bağlantılarını sağlık, permission, freshness ve risk bayraklarıyla izleyen admin monitoring foundation yüzeyi.",
            activeNav: "ExchangeAccounts",
            breadcrumbItems: new[] { "Super Admin", "Operasyon", "Exchange Hesapları" });

        var summaries = await apiCredentialValidationService.ListAdminSummariesAsync(cancellationToken: cancellationToken);
        return View(summaries);
    }

    [Authorize(Policy = ApplicationPolicies.TradeOperations)]
    public IActionResult BotOperations()
    {
        ApplyShellMeta(
            title: "Bot Operasyonları",
            description: "Platform genelindeki botları durum, strategy, risk ve AI etkisi perspektifiyle izleyen operasyonel admin foundation ekranı.",
            activeNav: "BotOperations",
            breadcrumbItems: new[] { "Super Admin", "Operasyon", "Bot Operasyonları" });

        return View();
    }

    [Authorize(Policy = ApplicationPolicies.TradeOperations)]
    public IActionResult StrategyAiMonitoring()
    {
        ApplyShellMeta(
            title: "Strategy / AI İzleme",
            description: "Platform genelindeki strategy template kullanımı, AI health, confidence/veto sinyalleri ve explainability örneklerini izleyen admin monitoring foundation ekranı.",
            activeNav: "StrategyAiMonitoring",
            breadcrumbItems: new[] { "Super Admin", "İzleme", "Strategy / AI" });

        return View();
    }

    public async Task<IActionResult> SystemHealth(CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Sistem Sağlığı",
            description: "API, web app, queue ve worker health sinyallerini failed jobs ve stale warning yüzeyiyle tek ekranda toplayan monitoring foundation.",
            activeNav: "SystemHealth",
            breadcrumbItems: new[] { "Super Admin", "Runtime", "Sistem Sağlığı" });

        ViewData[MonitoringDashboardSnapshotViewDataKey] = await LoadMonitoringDashboardSnapshotAsync(cancellationToken);

        return View();
    }

    public async Task<IActionResult> Jobs(CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Job / Worker Durumu",
            description: "Worker health, last heartbeat, failed jobs ve retry placeholder akışlarını ortak system health foundation üstünde toplar.",
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
            description: "Platform genelindeki audit, security, runtime, AI, trading ve admin action loglarını gelişmiş filtre ve detail drawer ile izleyen global log center foundation.",
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
            description: "CorrelationId bazli signal, decision ve execution zincirini detay seviyesinde izleyen minimal admin detail ekranı.",
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
        if (approvalWorkflowService is null)
        {
            TempData[ApprovalErrorTempDataKey] = "Approval workflow service hazir degil.";
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
        if (approvalWorkflowService is null)
        {
            TempData[ApprovalErrorTempDataKey] = "Approval workflow service hazir degil.";
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
    public IActionResult SecurityEvents()
    {
        ApplyShellMeta(
            title: "Güvenlik Olayları",
            description: "Failed login, invalid MFA, suspicious session ve risky permission sinyallerini triage paneli ve security detail drawer ile toplayan admin security monitoring foundation.",
            activeNav: "SecurityEvents",
            breadcrumbItems: new[] { "Super Admin", "Gözlem", "Güvenlik Olayları" });

        return View();
    }

    [Authorize(Policy = ApplicationPolicies.AuditRead)]
    public IActionResult Notifications()
    {
        ApplyShellMeta(
            title: "Bildirim / Alarm Merkezi",
            description: "Platform seviyesindeki kritik alarmları, unread/read akışını ve admin bildirimlerini merkezi bir alarm hub üzerinde toplayan notification center foundation.",
            activeNav: "Notifications",
            breadcrumbItems: new[] { "Super Admin", "Gözlem", "Bildirim / Alarm" });

        return View();
    }

    public IActionResult SupportTools()
    {
        ApplyShellMeta(
            title: "Destek Araçları",
            description: "Kullanıcı arama, read-only diagnostic panel, kritik olay özeti ve güvenli destek CTA'larını tek ekranda toplayan support operations foundation.",
            activeNav: "SupportTools",
            breadcrumbItems: new[] { "Super Admin", "Platform", "Destek Araçları" });

        return View();
    }

    [Authorize(Policy = ApplicationPolicies.AdminPortalAccess)]
    public async Task<IActionResult> Settings(CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Global Ayarlar",
            description: "Feature flag, retention, AI rollout, maintenance ve varsayılan politika alanlarını kullanıcı ayarlarından ayrı toplayan global admin settings foundation.",
            activeNav: "Settings",
            breadcrumbItems: new[] { "Super Admin", "Platform", "Global Ayarlar" });

        ViewData[ExecutionSwitchSnapshotViewDataKey] = await globalExecutionSwitchService.GetSnapshotAsync(cancellationToken);
        ViewData[GlobalSystemStateSnapshotViewDataKey] = await globalSystemStateService.GetSnapshotAsync(cancellationToken);
        ViewData[GlobalPolicySnapshotViewDataKey] = await LoadGlobalPolicySnapshotAsync(cancellationToken);
        ViewData[CrisisPreviewViewDataKey] = LoadCrisisPreviewViewModelFromTempData();
        ViewData[AdminCanEditGlobalPolicyViewDataKey] = CanEditGlobalPolicy();

        return View();
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
        if (crisisEscalationService is null)
        {
            TempData[CrisisErrorTempDataKey] = "Crisis escalation service hazir degil.";
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
        if (crisisEscalationService is null)
        {
            TempData[CrisisErrorTempDataKey] = "Crisis escalation service hazir degil.";
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
    public async Task<IActionResult> UpdateGlobalPolicy(
        string? policyJson,
        string? reason,
        string? commandId,
        string? reauthToken,
        CancellationToken cancellationToken)
    {
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
            "Overview" => ("warning", "Delayed", "Platform özeti placeholder olduğu için bazı KPI blokları gecikmeli okunabilir; kritik karar öncesi modül detayına gidilmesi beklenir.", "Snapshot · 2 dk"),
            "ExchangeAccounts" => ("critical", "Stale", "Exchange permission ve connection görünümü stale olabilir; riskli hesaplarda detay drawer ve audit/log merkezi ile çapraz kontrol önerilir.", "Exchange sync · 4 dk"),
            "SystemHealth" or "Jobs" => ("degraded", "Degraded", "Monitoring read-model background worker üzerinden yenilenir; stale worker ve dependency health sinyalleri birlikte değerlendirilmelidir.", "Heartbeat · live worker"),
            "Audit" or "SecurityEvents" or "Notifications" => ("warning", "Delayed", $"{title} ekranı yüksek hacimli kayıtlarla çalıştığı için placeholder veriler gecikmeli olabilir.", "Trace window · 3 dk"),
            _ => ("healthy", "Fresh", $"{title} verisi güncel placeholder akış ile gösteriliyor; yine de kritik aksiyon öncesi modül detayları kontrol edilmelidir.", "Freshness · canlı")
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
