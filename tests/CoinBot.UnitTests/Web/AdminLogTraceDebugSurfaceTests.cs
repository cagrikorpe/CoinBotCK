using System.IO;

namespace CoinBot.UnitTests.Web;

public sealed class AdminLogTraceDebugSurfaceTests
{
    [Fact]
    public void AdminAuditView_RendersAdvancedLogTraceDebugDeepDive()
    {
        var root = ResolveRepositoryRoot();
        var auditContent = File.ReadAllText(Path.Combine(root, "src", "CoinBot.Web", "Areas", "Admin", "Views", "Admin", "Audit.cshtml"));
        var deepDiveContent = File.ReadAllText(Path.Combine(root, "src", "CoinBot.Web", "Areas", "Admin", "Views", "Shared", "Foundation", "_AdminLogTraceDebugDeepDive.cshtml"));

        Assert.Contains("_AdminLogTraceDebugDeepDive", auditContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-log-trace-debug", deepDiveContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-detailed-audit", deepDiveContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-trace-chain", deepDiveContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-execution-debugger", deepDiveContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-idempotency-tab", deepDiveContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-rebuild-tab", deepDiveContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-incident-root-cause", deepDiveContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-health-details", deepDiveContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-diagnostics-summary", deepDiveContent, StringComparison.Ordinal);
        Assert.Contains("TraceDetail", deepDiveContent, StringComparison.Ordinal);
        Assert.Contains("SystemHealth", deepDiveContent, StringComparison.Ordinal);
        Assert.Contains("SupportTools", deepDiveContent, StringComparison.Ordinal);
        Assert.Contains("Jobs", deepDiveContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminOverview_OnlyLinksAdvancedTechnicalDeepDiveWithoutRenderingItOnMainFlow()
    {
        var root = ResolveRepositoryRoot();
        var overviewContent = File.ReadAllText(Path.Combine(root, "src", "CoinBot.Web", "Areas", "Admin", "Views", "Admin", "Overview.cshtml"));
        var flowModelContent = File.ReadAllText(Path.Combine(root, "src", "CoinBot.Web", "ViewModels", "Admin", "AdminSuperAdminPrimaryFlowViewModel.cs"));

        Assert.DoesNotContain("data-cb-admin-log-trace-debug", overviewContent, StringComparison.Ordinal);
        Assert.Contains("/admin/Audit", flowModelContent, StringComparison.Ordinal);
        Assert.Contains("/admin/Audit?query=Execution", flowModelContent, StringComparison.Ordinal);
        Assert.Contains("/admin/Jobs", flowModelContent, StringComparison.Ordinal);
        Assert.Contains("Teknik detaylar ana akistan ayrik", overviewContent, StringComparison.Ordinal);
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


