namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategyTemplateSnapshot(
    string TemplateKey,
    string TemplateName,
    string Description,
    string Category,
    int SchemaVersion,
    string DefinitionJson,
    StrategyDefinitionValidationSnapshot Validation);
