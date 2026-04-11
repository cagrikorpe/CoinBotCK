using System.IO;

namespace CoinBot.UnitTests.Web;

public sealed class UserExecutionSurfaceTests
{
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

        Assert.DoesNotContain("correlation", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ClientOrderId", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("retry", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Execution Detail", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lifecycle", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AI score", content, StringComparison.OrdinalIgnoreCase);
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

