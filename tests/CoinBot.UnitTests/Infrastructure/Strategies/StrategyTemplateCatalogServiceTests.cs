using CoinBot.Infrastructure.Strategies;

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
    }

    [Fact]
    public async Task GetAsync_FailsClosed_WhenTemplateKeyIsUnknown()
    {
        var service = new StrategyTemplateCatalogService(new StrategyRuleParser(), new StrategyDefinitionValidator());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAsync("unknown-template"));

        Assert.Contains("unknown-template", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
