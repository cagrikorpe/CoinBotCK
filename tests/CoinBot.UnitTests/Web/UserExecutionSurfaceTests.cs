using System.IO;
using System.Text.RegularExpressions;

namespace CoinBot.UnitTests.Web;

public sealed class UserExecutionSurfaceTests
{
    [Fact]
    public void PositionsFoundationScript_DoesNotOverrideLiveSnapshot_WithDemoScenario()
    {
        var root = ResolveRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "src", "CoinBot.Web", "wwwroot", "js", "positions-foundation.js"));
        var view = File.ReadAllText(Path.Combine(root, "src", "CoinBot.Web", "Views", "Positions", "Index.cshtml"));

        Assert.Contains("window.location.reload()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("applyScenario", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Alpha Spot Pulse", script, StringComparison.Ordinal);
        Assert.DoesNotContain("data-cb-positions-scenario=\"active\"", view, StringComparison.Ordinal);
    }
    [Fact]
    public void PositionsSurface_RendersUltraSoftUserExecutionFlow()
    {
        var root = ResolveRepositoryRoot();
        var content = string.Concat(
            File.ReadAllText(Path.Combine(root, "src", "CoinBot.Web", "Views", "Positions", "Index.cshtml")),
            File.ReadAllText(Path.Combine(root, "src", "CoinBot.Web", "Views", "Shared", "Positions", "_SummaryCards.cshtml")),
            File.ReadAllText(Path.Combine(root, "src", "CoinBot.Web", "Views", "Shared", "Positions", "_OpenPositionsCard.cshtml")),
            File.ReadAllText(Path.Combine(root, "src", "CoinBot.Web", "Views", "Shared", "Positions", "_OrderHistoryCard.cshtml")),
            File.ReadAllText(Path.Combine(root, "src", "CoinBot.Web", "Views", "Shared", "Positions", "_ClosedPositionsCard.cshtml")),
            File.ReadAllText(Path.Combine(root, "src", "CoinBot.Web", "Views", "Shared", "Positions", "_HelpCard.cshtml")));

        Assert.Contains("data-cb-user-exec-page", content, StringComparison.Ordinal);
        Assert.Contains("Pozisyonlar", content, StringComparison.Ordinal);
        Assert.Contains("Emirler", content, StringComparison.Ordinal);
        Assert.Contains("İşlem Geçmişi", content, StringComparison.Ordinal);
        Assert.Contains("Kâr / Zarar", content, StringComparison.Ordinal);
        Assert.Contains("Portföy Özeti", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-open-position-row", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-order-history-row", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-closed-position-row", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-positions-total-pnl", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-positions-pnl-consistency", content, StringComparison.Ordinal);
        Assert.Contains("Futures pozisyonlari", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-user-manual-close-form", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-user-manual-close-button", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-user-manual-close-checkbox", content, StringComparison.Ordinal);
        Assert.Contains("Reduce-only close emrini onayliyorum.", content, StringComparison.Ordinal);
        Assert.Contains("Bu buton borsaya reduce-only close emri gonderir.", content, StringComparison.Ordinal);
        Assert.Contains("asp-action=\"ManualClose\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("multiple", content, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("correlation", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ClientOrderId", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("retry", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Execution Detail", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lifecycle", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AI score", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PositionsFoundationScript_WiresUserManualCloseDoubleConfirm()
    {
        var root = ResolveRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "src", "CoinBot.Web", "wwwroot", "js", "positions-foundation.js"));

        Assert.Contains("data-cb-user-manual-close-button", script, StringComparison.Ordinal);
        Assert.Contains("data-cb-user-manual-close-form", script, StringComparison.Ordinal);
        Assert.Contains("data-cb-user-manual-close-checkbox", script, StringComparison.Ordinal);
        Assert.Contains("window.confirm", script, StringComparison.Ordinal);
        Assert.Contains("reduce-only close emri gonderilsin mi?", script, StringComparison.Ordinal);
    }

    [Fact]
    public void UserLayout_RendersExactlyOnePositionsNavigationLink()
    {
        var root = ResolveRepositoryRoot();
        var layout = File.ReadAllText(Path.Combine(root, "src", "CoinBot.Web", "Views", "Shared", "_Layout.cshtml"));
        var positionsNavCount = Regex.Matches(layout, "asp-controller=\"Positions\"").Count;

        Assert.Equal(1, positionsNavCount);
        Assert.Contains("asp-action=\"Index\"", layout, StringComparison.Ordinal);
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

