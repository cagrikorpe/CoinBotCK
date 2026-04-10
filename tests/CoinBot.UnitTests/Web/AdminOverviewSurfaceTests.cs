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
        Assert.Contains("data-cb-super-admin-flow-visible=\"@setup.IsVisible.ToString().ToLowerInvariant()\"", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-flow-accessible=\"@setup.IsAccessible.ToString().ToLowerInvariant()\"", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-action-reason=\"activate\"", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-action-reason=\"refresh\"", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-activation-panel", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-monitoring-panel", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-super-admin-advanced-links", content, StringComparison.Ordinal);
        Assert.Contains("Sistem Kurulumu", content, StringComparison.Ordinal);
        Assert.Contains("Sistemi Aktiflestir", content, StringComparison.Ordinal);
        Assert.Contains("Sistemi Izle", content, StringComparison.Ordinal);
        Assert.Contains("Gelismis", content, StringComparison.Ordinal);
        Assert.Contains("Islem notu", content, StringComparison.Ordinal);
        Assert.Contains("Audit, incidents, health, loglar, trace ve rollout kanitlari", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Runtime &amp; Health Center", content, StringComparison.Ordinal);
        Assert.DoesNotContain("User / Bot Governance Center", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Exchange / Credential Governance", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Policy / Limit Governance", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Son karar", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Audit reason", content, StringComparison.Ordinal);
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
        Assert.Contains("Gelismis", content, StringComparison.Ordinal);
        Assert.Contains("asp-fragment=\"@item.Fragment\"", content, StringComparison.Ordinal);
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
