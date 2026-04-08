using System.IO;

namespace CoinBot.UnitTests.Web;

public sealed class AdminIncidentAuditDecisionSurfaceTests
{
    [Fact]
    public void AdminAuditView_RendersIncidentAuditDecisionCenterSurface()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Admin",
            "Audit.cshtml"));

        Assert.Contains("Incident / Audit / Decision Center", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-decision-center", content, StringComparison.Ordinal);
        Assert.Contains("cb_admin_audit_filter_outcome", content, StringComparison.Ordinal);
        Assert.Contains("cb_admin_audit_filter_reason_code", content, StringComparison.Ordinal);
        Assert.Contains("LastDecision", content, StringComparison.Ordinal);
        Assert.Contains("ChangedBy", content, StringComparison.Ordinal);
        Assert.Contains("Before", content, StringComparison.Ordinal);
        Assert.Contains("After", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-approval-history", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-incident-timeline", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-audit-trail", content, StringComparison.Ordinal);
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
