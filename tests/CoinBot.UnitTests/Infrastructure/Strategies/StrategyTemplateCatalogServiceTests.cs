using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Strategies;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.UnitTests.Infrastructure.Strategies;

public sealed class StrategyTemplateCatalogServiceTests
{
    [Fact]
    public async Task ListAsync_ReturnsValidatedBuiltInTemplates()
    {
        var service = new StrategyTemplateCatalogService(new StrategyRuleParser(), new StrategyDefinitionValidator());

        var templates = await service.ListAsync();

        Assert.True(templates.Count >= 3);
        Assert.Contains(templates, template => template.TemplateKey == "rsi-reversal" && template.Validation.IsValid && template.SchemaVersion == 2);
        Assert.All(templates, template =>
        {
            Assert.True(template.Validation.IsValid);
            Assert.False(string.IsNullOrWhiteSpace(template.Description));
        });
    }

    [Fact]
    public async Task GetAsync_ReturnsExactTemplateDefinition_ForKnownTemplateKey()
    {
        var service = new StrategyTemplateCatalogService(new StrategyRuleParser(), new StrategyDefinitionValidator());

        var template = await service.GetAsync("macd-trend");

        Assert.Equal("macd-trend", template.TemplateKey);
        Assert.Equal("MACD Trend", template.TemplateName);
        Assert.Contains("\"ruleType\": \"macd\"", template.DefinitionJson, StringComparison.Ordinal);
        Assert.Equal("Valid", template.Validation.StatusCode);
        Assert.True(template.IsBuiltIn);
    }

    [Fact]
    public async Task GetAsync_FailsClosed_WhenTemplateKeyIsUnknown()
    {
        var service = new StrategyTemplateCatalogService(new StrategyRuleParser(), new StrategyDefinitionValidator());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAsync("unknown-template"));

        Assert.Contains("unknown-template", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateCustomAsync_PersistsValidatedTemplate_AndListIncludesCustomEntry()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var created = await service.CreateCustomAsync(
            "template-owner",
            "custom-rsi-live",
            "Custom RSI Live",
            "Custom template for live RSI entries.",
            "Custom",
            CreateTemplateDefinitionJson());
        var listedTemplates = await service.ListAsync();

        Assert.Equal("custom-rsi-live", created.TemplateKey);
        Assert.Equal("Custom RSI Live", created.TemplateName);
        Assert.False(created.IsBuiltIn);
        Assert.True(created.IsActive);
        Assert.Equal("Custom", created.TemplateSource);
        Assert.Equal("Valid", created.Validation.StatusCode);
        Assert.Contains("\"templateKey\": \"custom-rsi-live\"", created.DefinitionJson, StringComparison.Ordinal);
        Assert.Contains(listedTemplates, template => template.TemplateKey == "custom-rsi-live" && !template.IsBuiltIn && template.TemplateSource == "Custom");
    }

    [Fact]
    public async Task CloneAsync_CreatesCustomTemplate_WithSourceTemplateMetadata()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var cloned = await service.CloneAsync(
            "template-owner",
            "rsi-reversal",
            "rsi-reversal-clone",
            "RSI Reversal Clone",
            "Cloned built-in template.",
            "Clone");

        Assert.Equal("rsi-reversal-clone", cloned.TemplateKey);
        Assert.Equal("RSI Reversal Clone", cloned.TemplateName);
        Assert.Equal("rsi-reversal", cloned.SourceTemplateKey);
        Assert.False(cloned.IsBuiltIn);
        Assert.Contains("\"templateKey\": \"rsi-reversal-clone\"", cloned.DefinitionJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchiveAsync_HidesCustomTemplateFromActiveCatalog()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        _ = await service.CreateCustomAsync(
            "template-owner",
            "archivable-template",
            "Archivable Template",
            "Archivable custom template.",
            "Custom",
            CreateTemplateDefinitionJson());

        var archived = await service.ArchiveAsync("archivable-template");
        var listedTemplates = await service.ListAsync();

        Assert.False(archived.IsActive);
        Assert.DoesNotContain(listedTemplates, template => template.TemplateKey == "archivable-template");
    }

    [Fact]
    public async Task CreateCustomAsync_RejectsInvalidDefinition_FailClosed()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var exception = await Assert.ThrowsAsync<StrategyDefinitionValidationException>(() => service.CreateCustomAsync(
            "template-owner",
            "invalid-template",
            "Invalid Template",
            "Invalid custom template.",
            "Custom",
            """
            {
              "schemaVersion": 2,
              "entry": {
                "path": "indicator.rsi.value",
                "comparison": "lessThanOrEqual",
                "value": 101,
                "ruleId": "entry-rsi",
                "ruleType": "rsi",
                "weight": 10,
                "enabled": true
              }
            }
            """));

        Assert.Equal("InvalidRsiThreshold:entry:101", exception.StatusCode);
        Assert.Empty(await dbContext.TradingStrategyTemplates.ToListAsync());
    }

    private static StrategyTemplateCatalogService CreateService(ApplicationDbContext dbContext)
    {
        return new StrategyTemplateCatalogService(
            new StrategyRuleParser(),
            new StrategyDefinitionValidator(),
            dbContext);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private static string CreateTemplateDefinitionJson()
    {
        return
            """
            {
              "schemaVersion": 2,
              "entry": {
                "operator": "all",
                "ruleId": "entry-root",
                "ruleType": "group",
                "timeframe": "1m",
                "weight": 1,
                "enabled": true,
                "rules": [
                  {
                    "ruleId": "entry-mode",
                    "ruleType": "context",
                    "path": "context.mode",
                    "comparison": "equals",
                    "value": "Live",
                    "timeframe": "1m",
                    "weight": 20,
                    "enabled": true
                  },
                  {
                    "ruleId": "entry-rsi",
                    "ruleType": "rsi",
                    "path": "indicator.rsi.value",
                    "comparison": "lessThanOrEqual",
                    "value": 30,
                    "timeframe": "1m",
                    "weight": 80,
                    "enabled": true
                  }
                ]
              }
            }
            """;
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }
}
