using System.IO;

namespace CoinBot.UnitTests.Web;

public sealed class BotIndexSurfaceTests
{
    [Fact]
    public void BotsIndexView_RendersBotListWithSingleScreenOperationParity()
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
        Assert.Contains("data-cb-bot-long-regime-card", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-long-regime-status", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-long-regime-policy", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-long-regime-live", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-long-regime-explain", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-detail", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-toggle", content, StringComparison.Ordinal);
        Assert.Contains("LIVE", content, StringComparison.Ordinal);
        Assert.Contains("SHADOW", content, StringComparison.Ordinal);
        Assert.Contains("PAUSED", content, StringComparison.Ordinal);
        Assert.Contains("Botunuzu görün, stratejisini bağlayın ve durumunu anlayın.", content, StringComparison.Ordinal);
        Assert.Contains("Strateji oluştur", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-direction-mode-select", content, StringComparison.Ordinal);
        Assert.Contains("LongOnly", content, StringComparison.Ordinal);
        Assert.Contains("ShortOnly", content, StringComparison.Ordinal);
        Assert.Contains("LongShort", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-start-block-reason", content, StringComparison.Ordinal);
        Assert.Contains("Strateji hazır değil", content, StringComparison.Ordinal);

        Assert.Contains("data-cb-bot-ops-card", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-ops-signal", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-ops-decision", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-ops-order", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-ops-reason", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-entry-counters", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-exit-counters", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-entry-generated", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-entry-skipped", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-entry-vetoed", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-entry-ordered", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-entry-filled", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-exit-generated", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-exit-skipped", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-exit-vetoed", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-exit-ordered", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-exit-filled", content, StringComparison.Ordinal);
        Assert.Contains("Signal / Decision / Order", content, StringComparison.Ordinal);
        Assert.Contains("Runtime decision:", content, StringComparison.Ordinal);
        Assert.Contains("Rejection / failure:", content, StringComparison.Ordinal);
    }

    [Fact]
    public void BotsEditorView_RendersStrategyBindingAndNoStrategyMessage()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Views",
            "Bots",
            "Editor.cshtml"));

        Assert.Contains("data-cb-bot-strategy-select", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-no-strategy-message", content, StringComparison.Ordinal);
        Assert.Contains("Önce strateji oluşturun. Strateji olmadan bot başlatılamaz.", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-enable-block-reason", content, StringComparison.Ordinal);
        Assert.Contains("Strateji hazır değil.", content, StringComparison.Ordinal);
        Assert.Contains("Strateji oluştur", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-direction-mode-select", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-scanner-universe", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-scanner-universe-empty", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-scanner-universe-list", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-symbol-select", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-allowed-symbols", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-allowed-symbols-empty", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-bot-allowed-symbols-list", content, StringComparison.Ordinal);
        Assert.Contains("name=\"AllowedSymbols\"", content, StringComparison.Ordinal);
        Assert.Contains("Primary Symbol", content, StringComparison.Ordinal);
        Assert.Contains("Scanner Universe", content, StringComparison.Ordinal);
        Assert.Contains("Allowed Symbols", content, StringComparison.Ordinal);
        Assert.Contains("Primary Symbol bu botun backward-compatible tekil fallback semboludur.", content, StringComparison.Ordinal);
        Assert.Contains("Allowed Symbols bu botun multi-symbol scanner scope'unu tanımlar. Boş bırakılırsa bot Primary Symbol fallback kullanır.", content, StringComparison.Ordinal);
        Assert.Contains("Scanner Universe read-only'dir ve runtime market-data configuration kaynağından gelir.", content, StringComparison.Ordinal);
        Assert.Contains("Allowed symbol seçimi için scanner universe configured değil.", content, StringComparison.Ordinal);
        Assert.Contains("Scanner universe configured değil.", content, StringComparison.Ordinal);
        Assert.Contains("LongOnly", content, StringComparison.Ordinal);
        Assert.Contains("ShortOnly", content, StringComparison.Ordinal);
        Assert.Contains("LongShort", content, StringComparison.Ordinal);
        Assert.DoesNotContain("multiple", content, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("execution analytics", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ReasonCode", content, StringComparison.Ordinal);
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
