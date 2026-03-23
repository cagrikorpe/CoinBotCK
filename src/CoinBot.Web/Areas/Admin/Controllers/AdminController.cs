using System.Linq;
using System.Security.Claims;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Contracts.Common;
using CoinBot.Domain.Enums;
using CoinBot.Web.ViewModels.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Areas.Admin.Controllers;

[Area("Admin")]
[Authorize(Policy = ApplicationPolicies.AdminPortalAccess)]
public sealed class AdminController : Controller
{
    private const string ExecutionSwitchSnapshotViewDataKey = "AdminExecutionSwitchSnapshot";
    private readonly IGlobalExecutionSwitchService globalExecutionSwitchService;

    public AdminController(IGlobalExecutionSwitchService globalExecutionSwitchService)
    {
        this.globalExecutionSwitchService = globalExecutionSwitchService;
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

    [Authorize(Policy = ApplicationPolicies.ExchangeManagement)]
    public IActionResult ExchangeAccounts()
    {
        ApplyShellMeta(
            title: "Exchange Hesapları",
            description: "Platform çapındaki exchange bağlantılarını sağlık, permission, freshness ve risk bayraklarıyla izleyen admin monitoring foundation yüzeyi.",
            activeNav: "ExchangeAccounts",
            breadcrumbItems: new[] { "Super Admin", "Operasyon", "Exchange Hesapları" });

        return View();
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

    public IActionResult SystemHealth()
    {
        ApplyShellMeta(
            title: "Sistem Sağlığı",
            description: "API, web app, queue ve worker health sinyallerini failed jobs ve stale warning yüzeyiyle tek ekranda toplayan monitoring foundation.",
            activeNav: "SystemHealth",
            breadcrumbItems: new[] { "Super Admin", "Runtime", "Sistem Sağlığı" });

        return View();
    }

    public IActionResult Jobs()
    {
        ApplyShellMeta(
            title: "Job / Worker Durumu",
            description: "Worker health, last heartbeat, failed jobs ve retry placeholder akışlarını ortak system health foundation üstünde toplar.",
            activeNav: "Jobs",
            breadcrumbItems: new[] { "Super Admin", "Runtime", "Job / Worker" });

        return View("SystemHealth");
    }

    [Authorize(Policy = ApplicationPolicies.AuditRead)]
    public IActionResult Audit()
    {
        ApplyShellMeta(
            title: "Audit & Log Merkezi",
            description: "Platform genelindeki audit, security, runtime, AI, trading ve admin action loglarını gelişmiş filtre ve detail drawer ile izleyen global log center foundation.",
            activeNav: "Audit",
            breadcrumbItems: new[] { "Super Admin", "Gözlem", "Audit & Log" });

        return View();
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

    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    public async Task<IActionResult> Settings(CancellationToken cancellationToken)
    {
        ApplyShellMeta(
            title: "Global Ayarlar",
            description: "Feature flag, retention, AI rollout, maintenance ve varsayılan politika alanlarını kullanıcı ayarlarından ayrı toplayan global admin settings foundation.",
            activeNav: "Settings",
            breadcrumbItems: new[] { "Super Admin", "Platform", "Global Ayarlar" });

        ViewData[ExecutionSwitchSnapshotViewDataKey] = await globalExecutionSwitchService.GetSnapshotAsync(cancellationToken);

        return View();
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetTradeMasterState(bool isArmed, string? reason, CancellationToken cancellationToken)
    {
        try
        {
            await globalExecutionSwitchService.SetTradeMasterStateAsync(
                isArmed ? TradeMasterSwitchState.Armed : TradeMasterSwitchState.Disarmed,
                ResolveAdminActor(),
                BuildSwitchContext("TradeMaster", reason),
                HttpContext.TraceIdentifier,
                cancellationToken);

            TempData["AdminExecutionSwitchSuccess"] = isArmed
                ? "TradeMaster armed. Emir zinciri backend hard gate uzerinden acildi."
                : "TradeMaster disarmed. Emir zinciri fail-closed duruma alindi.";
        }
        catch (Exception exception)
        {
            TempData["AdminExecutionSwitchError"] = exception.Message;
        }

        return RedirectToAction(nameof(Settings));
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.PlatformAdministration)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDemoMode(bool isEnabled, string? reason, string? liveApprovalReference, CancellationToken cancellationToken)
    {
        try
        {
            TradingModeLiveApproval? liveApproval = null;

            if (!isEnabled)
            {
                var approvalReference = liveApprovalReference?.Trim();
                liveApproval = string.IsNullOrWhiteSpace(approvalReference)
                    ? null
                    : new TradingModeLiveApproval(approvalReference);
            }

            await globalExecutionSwitchService.SetDemoModeAsync(
                isEnabled,
                ResolveAdminActor(),
                liveApproval,
                BuildSwitchContext("DemoMode", reason),
                HttpContext.TraceIdentifier,
                cancellationToken);

            TempData["AdminExecutionSwitchSuccess"] = isEnabled
                ? "DemoMode enabled. Live execution yolu backend hard gate ile kapatildi."
                : "DemoMode disabled. Live execution yalnizca approval reference ile acildi.";
        }
        catch (Exception exception)
        {
            TempData["AdminExecutionSwitchError"] = exception.Message;
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
        ViewData["AdminFreshnessTone"] = freshness.Tone;
        ViewData["AdminFreshnessLabel"] = freshness.Label;
        ViewData["AdminFreshnessMessage"] = freshness.Message;
        ViewData["AdminFreshnessMeta"] = freshness.Meta;
    }


    private string ResolveAdminRoleKey()
    {
        var rawRole = Request.Query["role"].FirstOrDefault();

        return string.IsNullOrWhiteSpace(rawRole)
            ? "SuperAdmin"
            : NormalizeRoleKey(rawRole);
    }

    private static string NormalizeRoleKey(string role)
    {
        return role.Trim().ToLowerInvariant() switch
        {
            "superadmin" or "super-admin" or "super_admin" => "SuperAdmin",
            "admin" => "Admin",
            "support" => "Support",
            "auditor" => "Auditor",
            "viewer" => "Viewer",
            _ => "SuperAdmin"
        };
    }

    private static string MapRoleLabel(string roleKey)
    {
        return roleKey switch
        {
            "Admin" => "Admin",
            "Support" => "Support",
            "Auditor" => "Auditor",
            "Viewer" => "Viewer",
            _ => "Super Admin"
        };
    }

    private static (string Tone, string Label, string Message, string Meta) ResolveFreshnessMeta(string activeNav, string title)
    {
        return activeNav switch
        {
            "Overview" => ("warning", "Delayed", "Platform özeti placeholder olduğu için bazı KPI blokları gecikmeli okunabilir; kritik karar öncesi modül detayına gidilmesi beklenir.", "Snapshot · 2 dk"),
            "ExchangeAccounts" => ("critical", "Stale", "Exchange permission ve connection görünümü stale olabilir; riskli hesaplarda detay drawer ve audit/log merkezi ile çapraz kontrol önerilir.", "Exchange sync · 4 dk"),
            "SystemHealth" or "Jobs" => ("degraded", "Degraded", "Worker heartbeat ve failed jobs yüzeyi birlikte değerlendirilmelidir; stale worker sinyalleri operasyon kararını etkileyebilir.", "Heartbeat · 91 sn"),
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

    private string ResolveAdminActor()
    {
        var subjectId = User.FindFirstValue(ClaimTypes.NameIdentifier)?.Trim();
        return string.IsNullOrWhiteSpace(subjectId) ? "admin:unknown" : $"admin:{subjectId}";
    }

    private static string BuildSwitchContext(string scope, string? reason)
    {
        var context = string.IsNullOrWhiteSpace(reason)
            ? $"AdminSettings.{scope}"
            : $"AdminSettings.{scope} | Reason={reason.Trim()}";

        return context.Length <= 512
            ? context
            : context[..512];
    }
}
