namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategyDefinitionMetadata(
    string? TemplateKey,
    string? TemplateName,
    int? TemplateRevisionNumber = null,
    string? TemplateSource = null)
{
    public static StrategyDefinitionMetadata Empty { get; } = new(null, null, null, null);
}
