namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategyTemplateSnapshot(
    string TemplateKey,
    string TemplateName,
    string Description,
    string Category,
    int SchemaVersion,
    string DefinitionJson,
    StrategyDefinitionValidationSnapshot Validation,
    bool IsBuiltIn = false,
    bool IsActive = true,
    string TemplateSource = "Custom",
    string? SourceTemplateKey = null,
    int ActiveRevisionNumber = 1,
    int LatestRevisionNumber = 1,
    int? SourceRevisionNumber = null,
    Guid? TemplateId = null,
    Guid? ActiveRevisionId = null,
    Guid? LatestRevisionId = null,
    DateTime? ArchivedAtUtc = null);
