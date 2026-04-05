using System.IO;

namespace CoinBot.UnitTests.Web;

public sealed class ExecutionDecisionViewSelectorTests
{
    [Fact]
    public void BotsIndexView_ContainsExecutionDecisionSelectors()
    {
        var content = File.ReadAllText(Path.Combine(ResolveRepositoryRoot(), "src", "CoinBot.Web", "Views", "Bots", "Index.cshtml"));

        Assert.Contains("data-cb-bot-decision", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-decision-reason-code", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-decision-summary", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-market-threshold", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-market-gap-start", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-market-gap-last-seen", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-market-recovery", content, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminMarketScannerCardView_ContainsExecutionDecisionSelectors()
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

        Assert.Contains("data-cb-scanner-decision", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-decision-reason-type", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-decision-reason-code", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-decision-summary", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-market-last-candle", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-market-age", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-market-threshold", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-continuity-gap-start", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-continuity-gap-last-seen", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-continuity-recovery", content, StringComparison.Ordinal);
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
