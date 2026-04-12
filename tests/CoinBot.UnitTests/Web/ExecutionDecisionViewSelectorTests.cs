using System.IO;

namespace CoinBot.UnitTests.Web;

public sealed class ExecutionDecisionViewSelectorTests
{
    [Fact]
    public void BotsIndexView_KeepsExecutionDecisionSelectorsOutOfUserMainFlow()
    {
        var content = File.ReadAllText(Path.Combine(ResolveRepositoryRoot(), "src", "CoinBot.Web", "Views", "Bots", "Index.cshtml"));

        Assert.Contains("data-cb-bot-state-badge", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-state-summary", content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-cb-bot-decision", content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-cb-bot-decision-reason-code", content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-cb-bot-decision-summary", content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-cb-bot-market-threshold", content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-cb-bot-market-gap-start", content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-cb-bot-market-gap-last-seen", content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-cb-bot-market-recovery", content, StringComparison.Ordinal);
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
        Assert.Contains("data-cb-scanner-risk-outcome", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-risk-reason", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-risk-summary", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-risk-daily", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-risk-weekly", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-risk-symbol-exposure", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-risk-concurrent", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-risk-coin", content, StringComparison.Ordinal);
    }

    [Fact]
    public void TraceDetailView_ContainsExecutionDecisionSelectors()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Admin",
            "TraceDetail.cshtml"));

        Assert.Contains("data-cb-trace-decision-outcome", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-trace-decision-reason-type", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-trace-decision-reason-code", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-trace-decision-summary", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-trace-market-last-candle", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-trace-market-age", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-trace-market-threshold", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-trace-market-stale-reason", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-trace-continuity-state", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-trace-continuity-gap-start", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-trace-continuity-gap-last-seen", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-trace-continuity-recovery", content, StringComparison.Ordinal);
    }

    [Fact]
    public void LogCenterViews_ContainExecutionDecisionSelectors()
    {
        var traceInfoContent = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Views",
            "Shared",
            "LogCenter",
            "_TraceInfoCard.cshtml"));
        var drawerContent = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Views",
            "Shared",
            "LogCenter",
            "_DetailDrawer.cshtml"));

        Assert.Contains("data-cb-logcenter-decision-outcome", traceInfoContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-logcenter-decision-reason-code", traceInfoContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-logcenter-market-last-candle", traceInfoContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-logcenter-continuity-state", traceInfoContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-log-drawer-decision-outcome", drawerContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-log-drawer-decision-reason-code", drawerContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-log-drawer-market-last-candle", drawerContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-log-drawer-continuity-state", drawerContent, StringComparison.Ordinal);
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

