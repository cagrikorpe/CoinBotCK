using System.IO;

namespace CoinBot.UnitTests.Web;

public sealed class AdminOverviewSurfaceTests
{
    [Fact]
    public void AdminOverviewView_RendersFiveOperationalCentersIncludingRolloutClosure()
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

        Assert.Contains("cb_admin_operations_tab_link_runtime", content, StringComparison.Ordinal);
        Assert.Contains("cb_admin_operations_tab_link_user_bot", content, StringComparison.Ordinal);
        Assert.Contains("cb_admin_operations_tab_link_exchange", content, StringComparison.Ordinal);
        Assert.Contains("cb_admin_operations_tab_link_policy", content, StringComparison.Ordinal);
        Assert.Contains("cb_admin_operations_tab_link_rollout", content, StringComparison.Ordinal);
        Assert.Contains("_AdminActivationControlCenterSection", content, StringComparison.Ordinal);
        Assert.Contains("_AdminSystemHealthFreshnessStrip", content, StringComparison.Ordinal);
        Assert.Contains("_AdminGlobalExecutionSwitchSection", content, StringComparison.Ordinal);
        Assert.Contains("_AdminRiskPolicyDefaultsSection", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-rollout-closure", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-rollout-gate", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-rollout-check", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-rollout-actions", content, StringComparison.Ordinal);
        Assert.Contains("Super Admin gerekli", content, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminSidebar_UsesOperationalCenterLabel()
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

        Assert.Contains("Label = \"Operasyon Merkezi\"", content, StringComparison.Ordinal);
        Assert.Contains("Action = \"Overview\"", content, StringComparison.Ordinal);
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
