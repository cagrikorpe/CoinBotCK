namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategyDefinitionMetadata(
    string? TemplateKey,
    string? TemplateName)
{
    public static StrategyDefinitionMetadata Empty { get; } = new(null, null);
}
