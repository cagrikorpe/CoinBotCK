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