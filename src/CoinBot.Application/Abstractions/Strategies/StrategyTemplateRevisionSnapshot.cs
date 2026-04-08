namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategyTemplateRevisionSnapshot(
    Guid RevisionId,
    Guid? TemplateId,
    string TemplateKey,
    int RevisionNumber,
    int SchemaVersion,
    string ValidationStatusCode,
    string ValidationSummary,
    bool IsActive,
    bool IsLatest,
    bool IsArchived,
    string? SourceTemplateKey = null,
    int? SourceRevisionNumber = null,
    DateTime? ArchivedAtUtc = null,
    bool IsPublished = false);
