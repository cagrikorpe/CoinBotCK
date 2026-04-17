using System.IO;

namespace CoinBot.UnitTests.Web;

public sealed class StrategyBuilderSurfaceTests
{
    [Fact]
    public void StrategyBuilderView_RendersUltraSoftTemplateStartFlow()
    {
        var indexContent = ReadRepoFile("src", "CoinBot.Web", "Views", "StrategyBuilder", "Index.cshtml");
        var galleryContent = ReadRepoFile("src", "CoinBot.Web", "Views", "Shared", "StrategyBuilder", "_TemplateGallery.cshtml");
        var scriptContent = ReadRepoFile("src", "CoinBot.Web", "wwwroot", "js", "strategy-builder-foundation.js");

        Assert.Contains("Strateji oluştur", indexContent, StringComparison.Ordinal);
        Assert.Contains("Bota bağla", indexContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-strategy-builder", indexContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-runtime-config", indexContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-template-start-form", galleryContent, StringComparison.Ordinal);
        Assert.Contains("StartFromTemplate", galleryContent, StringComparison.Ordinal);
        Assert.Contains("Strateji taslağı oluştur", galleryContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-builder-form-root", galleryContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-builder-json-preview", galleryContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-builder-definition-json", galleryContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-builder-validation-summary", galleryContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-builder-explainability-summary", galleryContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-builder-parity-body", galleryContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-builder-advanced-toggle", galleryContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-builder-advanced-json", galleryContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-strategy-empty-state", galleryContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-template-definition", galleryContent, StringComparison.Ordinal);
        Assert.Contains("syncBuilderFromSelection", scriptContent, StringComparison.Ordinal);
        Assert.Contains("validateBuilderDefinition", scriptContent, StringComparison.Ordinal);
        Assert.Contains("readRuntimeConfig", scriptContent, StringComparison.Ordinal);
        Assert.Contains("buildExplainabilityAnalysis", scriptContent, StringComparison.Ordinal);
        Assert.Contains("applyAdvancedJsonToBuilder", scriptContent, StringComparison.Ordinal);
        Assert.Contains("currentDefinitionJson", scriptContent, StringComparison.Ordinal);
        Assert.Contains("data-cb-builder-rule-path", scriptContent, StringComparison.Ordinal);

        Assert.DoesNotContain("Builder Senaryoları", indexContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Template lifecycle", indexContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Rule toolbox", indexContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("State patterns", indexContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("debug", indexContent, StringComparison.OrdinalIgnoreCase);
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
