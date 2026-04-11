using System.IO;

namespace CoinBot.UnitTests.Web;

public sealed class BotIndexSurfaceTests
{
    [Fact]
    public void BotsIndexView_RendersUltraSoftBotListWithoutExecutionAnalytics()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Views",
            "Bots",
            "Index.cshtml"));

        Assert.Contains("data-cb-user-bots-page", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-user-bot-list", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-user-bot-row", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-state-badge", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-state-value", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-state-summary", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-detail", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-toggle", content, StringComparison.Ordinal);
        Assert.Contains("LIVE", content, StringComparison.Ordinal);
        Assert.Contains("SHADOW", content, StringComparison.Ordinal);
        Assert.Contains("PAUSED", content, StringComparison.Ordinal);
        Assert.Contains("Botunuzu görün, durumunu anlayın ve açıp kapatın.", content, StringComparison.Ordinal);

        Assert.DoesNotContain("data-cb-bot-market-diagnostics", content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-cb-bot-exec-submit", content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-cb-bot-exec-retry", content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-cb-bot-exec-protection", content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-cb-bot-exec-stage", content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-cb-bot-exec-transition", content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-cb-bot-exec-correlation", content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-cb-bot-exec-client-order", content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-cb-bot-decision", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Decision", content, StringComparison.Ordinal);
        Assert.DoesNotContain("ReasonCode", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Open orders", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Execution", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Worker</th>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Pilot</th>", content, StringComparison.Ordinal);
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