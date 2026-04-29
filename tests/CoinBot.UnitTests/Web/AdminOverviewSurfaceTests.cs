using System.IO;

namespace CoinBot.UnitTests.Web;

public sealed class AdminOverviewSurfaceTests
{
    [Fact]
    public void AdminOverviewView_RendersFourPrimaryFlowTabs_AndKeepsTechnicalCentersOutOfMainSurface()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Admin",
            "Overview.cshtml"));

        Assert.Contains("data-cb-super-admin-simple-flow", content, StringComparison.Ordinal);
        Assert.Contains("cb_super_admin_flow_tab_link_setup", content, StringComparison.Ordinal);
        Assert.Contains("cb_super_admin_flow_tab_link_activation", content, StringComparison.Ordinal);
        Assert.Contains("cb_super_admin_flow_tab_link_monitoring", content, StringComparison.Ordinal);
        Assert.Contains("cb_super_admin_flow_tab_link_advanced", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-setup-form", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-setup-result=\"success\"", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-setup-result=\"error\"", content, StringComparison.Ordinal);
        Assert.Contains("asp-action=\"ConnectBinanceForSetup\"", content, StringComparison.Ordinal);
        Assert.Contains("API Key", content, StringComparison.Ordinal);
        Assert.Contains("Secret Key", content, StringComparison.Ordinal);
        Assert.Contains("Baglanti Adi", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-flow-visible=\"@setup.IsVisible.ToString().ToLowerInvariant()\"", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-flow-accessible=\"@setup.IsAccessible.ToString().ToLowerInvariant()\"", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-action-reason=\"activate\"", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-action-reason=\"refresh\"", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-activation-panel", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-activation-status", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-activation-actions", content, StringComparison.Ordinal);
        Assert.Contains("Ac, kapat veya acil durdur", content, StringComparison.Ordinal);
        Assert.Contains("Sade kontrol", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-monitoring-panel", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-monitoring-summary", content, StringComparison.Ordinal);
        Assert.Contains("Sistem calisiyor mu, son hata ne ve calisan bot sayisi kac", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-advanced-links", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-advanced-list", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-advanced-link=\"@item.Key\"", content, StringComparison.Ordinal);
        Assert.Contains("Sistem Kurulumu", content, StringComparison.Ordinal);
        Assert.Contains("Sistemi Aktiflestir", content, StringComparison.Ordinal);
        Assert.Contains("Sistemi Izle", content, StringComparison.Ordinal);
        Assert.Contains("Gelismis", content, StringComparison.Ordinal);
        Assert.Contains("Audit, incidents, health, loglar, trace ve rollout kanitlari", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Runtime &amp; Health Center", content, StringComparison.Ordinal);
        Assert.DoesNotContain("User / Bot Governance Center", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Exchange / Credential Governance", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Policy / Limit Governance", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Son karar", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Audit reason", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Aktivasyon detaylari", content, StringComparison.Ordinal);
        Assert.DoesNotContain("blocker", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("readiness checklist", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdminSystemHealthView_RendersMarketScannerParitySelectors()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Admin",
            "SystemHealth.cshtml"));

        Assert.Contains("data-cb-system-health-parity", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-system-health-parity-summary", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-system-health-core-tone", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-system-health-market-scanner-tone", content, StringComparison.Ordinal);
        Assert.Contains("Runtime parity", content, StringComparison.Ordinal);
        Assert.Contains("_AdminUltraDebugLogHealthCard", content, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminSystemHealthLogCard_RendersBoundedDiskPressureSummary()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Shared",
            "Foundation",
            "_AdminUltraDebugLogHealthCard.cshtml"));

        Assert.Contains("data-cb-ultra-log-health-card", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-ultra-log-disk-state", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-ultra-log-free-space", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-ultra-log-retention-heartbeat", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-ultra-log-escalation-reason", content, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentRootPath", content, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminDashboardView_RendersCriticalSections()
    {
        var systemHealthContent = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Admin",
            "SystemHealth.cshtml"));
        var operationalCardContent = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Shared",
            "Foundation",
            "_AdminOperationalObservabilityCard.cshtml"));

        Assert.Contains("_AdminOperationalObservabilityCard", systemHealthContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-operational-observability-card", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-ops-system-health-state", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-ops-execution-readiness", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-ops-ai-coverage", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-ops-log-system-state", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-ops-pilot-config", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-ops-private-sync", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-ops-blocked-reasons", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-ops-no-submit-reasons", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-ops-warning-list", operationalCardContent, StringComparison.Ordinal);
        Assert.DoesNotContain("CorrelationId", operationalCardContent, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerUserId", operationalCardContent, StringComparison.Ordinal);
        Assert.DoesNotContain("ScoringSummary", operationalCardContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminMarketScannerCard_RendersScannerDecisionObservabilitySurface()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Shared",
            "Foundation",
            "_AdminMarketScannerCard.cshtml"));

        Assert.Contains("data-cb-scanner-top-count", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-best-ranking-summary", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-observability-summary", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-directional-conflict-summary", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-same-direction-summary", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-duplicate-summary", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-guardrail-summary", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-top-ranking-decision", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-top-quality-summary", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-rejected-ranking-decision", content, StringComparison.Ordinal);
        Assert.DoesNotContain("CorrelationId", content, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerUserId", content, StringComparison.Ordinal);
        Assert.DoesNotContain("ScoringSummary", content, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminSystemHealthView_RendersExitPnlGuardEvidenceSurface()
    {
        var operationalCardContent = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Shared",
            "Foundation",
            "_AdminOperationalObservabilityCard.cshtml"));

        Assert.Contains("data-cb-admin-exit-pnl-evidence", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-ops-exit-pnl-guard", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("Exit PnL Guard", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("Recent exit orders", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("Recent entry orders", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("ExitSource", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("ExitPolicyDecision", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("ExitPolicyReason", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("PnL", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("Last exit summary", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("reverse blocked", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("Last exit reason", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("Last estimated PnL", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("Reduce-only exits", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-ops-exit-pnl-last-summary", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-ops-exit-pnl-blocked-summary", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-recent-exit-orders", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-recent-entry-orders", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-ops-exit-orders", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-ops-entry-orders", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-ops-exit-order-row", operationalCardContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-ops-entry-order-row", operationalCardContent, StringComparison.Ordinal);
        Assert.DoesNotContain("CorrelationId", operationalCardContent, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerUserId", operationalCardContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminBotOperationsTable_RendersManualCloseDoubleConfirmSurface()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Shared",
            "Foundation",
            "_AdminBotOperationsTable.cshtml"));

        Assert.Contains("data-cb-admin-manual-close-panel", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-manual-close-button", content, StringComparison.Ordinal);
        Assert.Contains("Close Position", content, StringComparison.Ordinal);
        Assert.Contains("Confirm Reduce-Only Close", content, StringComparison.Ordinal);
        Assert.Contains("ReduceOnly=True", content, StringComparison.Ordinal);
        Assert.Contains("Environment=@row.ManualCloseEnvironmentLabel", content, StringComparison.Ordinal);
        Assert.Contains("method=\"post\"", content, StringComparison.Ordinal);
        Assert.Contains("asp-action=\"ManualCloseBotPosition\"", content, StringComparison.Ordinal);
        Assert.Contains("name=\"botId\"", content, StringComparison.Ordinal);
        Assert.Contains("name=\"exchangeAccountId\"", content, StringComparison.Ordinal);
        Assert.Contains("name=\"symbol\"", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-reauth-required=\"true\"", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-manual-close-unavailable-reason", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-bot-last-error", content, StringComparison.Ordinal);
        Assert.Contains("title=\"@row.LastError\"", content, StringComparison.Ordinal);
        Assert.Contains("lastErrorPreview", content, StringComparison.Ordinal);
        Assert.Contains("@if (row.CanManualClose)", content, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminBotOperationsTable_RendersPositionAdoptionSurface_ForManualCloseCandidates()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Shared",
            "Foundation",
            "_AdminBotOperationsTable.cshtml"));

        Assert.Contains("data-cb-admin-position-adoption", content, StringComparison.Ordinal);
        Assert.Contains("@row.PositionAdoption", content, StringComparison.Ordinal);
        Assert.Contains("@row.AdoptedPositionSymbol", content, StringComparison.Ordinal);
        Assert.Contains("@row.AdoptedPositionQuantity", content, StringComparison.Ordinal);
        Assert.Contains("@row.AdoptionReason", content, StringComparison.Ordinal);
    }

    [Fact]
    public void SiteJs_WiresManualCloseSummaryClick_ToRequestSubmit()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "wwwroot",
            "js",
            "site.js"));

        Assert.Contains("data-cb-admin-manual-close-button", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-manual-close-form", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-manual-close-confirm", content, StringComparison.Ordinal);
        Assert.Contains("window.confirm", content, StringComparison.Ordinal);
        Assert.Contains("form.requestSubmit", content, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminTopbar_PrefersPageSpecificLastUpdatedTimestamp_WhenProvided()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Shared",
            "_AdminTopbar.cshtml"));

        Assert.Contains("ViewData[\"AdminLastUpdatedAtUtc\"]", content, StringComparison.Ordinal);
        Assert.Contains("pageLastUpdatedAtUtc", content, StringComparison.Ordinal);
        Assert.Contains("healthSnapshot?.LastUpdatedAtUtc", content, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminBotOperationsView_RendersFullWidthTableLayout_AndMovesHelpCardBelow()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Admin",
            "BotOperations.cshtml"));

        Assert.Contains("<div class=\"col-xl-12\">", content, StringComparison.Ordinal);
        Assert.DoesNotContain("col-xl-8", content, StringComparison.Ordinal);
        Assert.DoesNotContain("col-xl-4", content, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminSuperAdminPrimaryFlowModel_ExposesAdvancedTechnicalLinks()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "ViewModels",
            "Admin",
            "AdminSuperAdminPrimaryFlowViewModel.cs"));

        Assert.Contains("Audit ve Trace", content, StringComparison.Ordinal);
        Assert.Contains("Loglar / Diagnostik", content, StringComparison.Ordinal);
        Assert.Contains("Incidents", content, StringComparison.Ordinal);
        Assert.Contains("Health detaylari", content, StringComparison.Ordinal);
        Assert.Contains("Trace arama", content, StringComparison.Ordinal);
        Assert.Contains("Stratejiler", content, StringComparison.Ordinal);
        Assert.Contains("Strateji Builder", content, StringComparison.Ordinal);
        Assert.Contains("Rollout Kanitlari", content, StringComparison.Ordinal);
        Assert.Contains("Execution debugger", content, StringComparison.Ordinal);
        Assert.Contains("Idempotency / rebuild", content, StringComparison.Ordinal);
        Assert.Contains("Symbol Restrictions", content, StringComparison.Ordinal);
        Assert.Contains("/admin/Audit", content, StringComparison.Ordinal);
        Assert.Contains("/admin/Search", content, StringComparison.Ordinal);
        Assert.Contains("/admin/SystemHealth", content, StringComparison.Ordinal);
        Assert.Contains("/admin/StrategyTemplates", content, StringComparison.Ordinal);
        Assert.Contains("/admin/StrategyBuilder", content, StringComparison.Ordinal);
        Assert.Contains("/admin/Incidents", content, StringComparison.Ordinal);
        Assert.Contains("/admin/SupportTools", content, StringComparison.Ordinal);
        Assert.Contains("/admin/ConfigHistory", content, StringComparison.Ordinal);
        Assert.Contains("/admin/Jobs", content, StringComparison.Ordinal);
        Assert.Contains("/admin/Settings#cb_admin_settings_policy_restrictions", content, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminSidebar_ContainsSuperAdminPrimaryFlowLabels()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Shared",
            "_AdminSidebar.cshtml"));

        Assert.Contains("Ana Akis", content, StringComparison.Ordinal);
        Assert.Contains("Sistem Kurulumu", content, StringComparison.Ordinal);
        Assert.Contains("Sistemi Aktiflestir", content, StringComparison.Ordinal);
        Assert.Contains("Sistemi Izle", content, StringComparison.Ordinal);
        Assert.Contains("Kullanicilar", content, StringComparison.Ordinal);
        Assert.Contains("Gelismis", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-primary-link=\"@item.Key\"", content, StringComparison.Ordinal);
        Assert.Contains("BuildPrimaryFlowHref(item.Action, item.Fragment)", content, StringComparison.Ordinal);
        Assert.Contains("!superAdminPrimaryNavigationKeys.Contains(item.Key)", content, StringComparison.Ordinal);
        Assert.Contains("Guncellenecek -", content, StringComparison.Ordinal);
        Assert.Contains("superAdminUpdatePendingKeys", content, StringComparison.Ordinal);
        Assert.Contains("Ana akis ustte kalir", content, StringComparison.Ordinal);
        Assert.Contains("Global Ayarlar", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Platform Control Center", content, StringComparison.Ordinal);
    }

    private static string ResolveRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "CoinBot.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("CoinBot repository root could not be resolved.");
        }

        return directory.FullName;
    }
}
