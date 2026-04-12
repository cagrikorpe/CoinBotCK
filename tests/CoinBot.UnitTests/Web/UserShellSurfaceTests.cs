using System.IO;

namespace CoinBot.UnitTests.Web;

public sealed class UserShellSurfaceTests
{
    [Fact]
    public void UserLayout_RendersPrimaryMenuItems_WithRiskLink_AndHidesAdminOperations()
    {
        var content = ReadRepoFile("src", "CoinBot.Web", "Views", "Shared", "_Layout.cshtml");

        Assert.Contains("Label = \"Ana Sayfa\"", content, StringComparison.Ordinal);
        Assert.Contains("Label = \"Botlarım\"", content, StringComparison.Ordinal);
        Assert.Contains("Label = \"İşlemler\"", content, StringComparison.Ordinal);
        Assert.Contains("Label = \"Risk\"", content, StringComparison.Ordinal);
        Assert.Contains("Label = \"Ayarlar\"", content, StringComparison.Ordinal);
        Assert.Contains("Controller = \"Positions\"", content, StringComparison.Ordinal);
        Assert.Contains("Controller = \"RiskCenter\"", content, StringComparison.Ordinal);

        Assert.DoesNotContain("Label = \"Borsalarım\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Strategy Builder", content, StringComparison.Ordinal);
        Assert.DoesNotContain("AI Robot", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Risk Merkezi", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Backtest", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Paper Trading", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Log Merkezi", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Global Ayarlar", content, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-area=\"Admin\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Quick Action", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Global search", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserHomeView_DoesNotRenderOperationsDump()
    {
        var content = ReadRepoFile("src", "CoinBot.Web", "Views", "Home", "Index.cshtml");

        Assert.Contains("data-cb-user-shell-home", content, StringComparison.Ordinal);
        Assert.Contains("Günlük özet", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Operations Dashboard", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Dashboard/_OperationsSummaryCard", content, StringComparison.Ordinal);
        Assert.DoesNotContain("TradeMaster", content, StringComparison.Ordinal);
        Assert.DoesNotContain("PilotActivation", content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-cb-operations-summary", content, StringComparison.Ordinal);
    }

    [Fact]
    public void UserExchangeSurface_DoesNotExposeDemoLiveSelection()
    {
        var content = ReadRepoFile("src", "CoinBot.Web", "Views", "Shared", "Exchanges", "_UserExchangeCommandCenter.cshtml");

        Assert.Contains("type=\"hidden\"", content, StringComparison.Ordinal);
        Assert.Contains("Form.RequestedEnvironment", content, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecutionEnvironment.Demo", content, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecutionEnvironment.Live", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Live approval", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Etkin mod", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("environment guard", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserSettingsSurface_UsesUserLanguageWithoutAdminOperationalJargon()
    {
        var content = ReadRepoFile("src", "CoinBot.Web", "Views", "Settings", "Index.cshtml");

        Assert.Contains("Günlük kullanım tercihlerinizi", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Super Admin", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ops", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Global Ayarlar", content, StringComparison.Ordinal);
        Assert.DoesNotContain("drift guard", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("server-time sync", content, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadRepoFile(params string[] relativePath)
    {
        return File.ReadAllText(Path.Combine(ResolveRepositoryRoot(), Path.Combine(relativePath)));
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