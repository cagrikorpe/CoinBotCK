using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategyVersionSnapshot(
    Guid StrategyVersionId,
    Guid StrategyId,
    int SchemaVersion,
    int VersionNumber,
    StrategyVersionStatus Status,
    DateTime? PublishedAtUtc,
    DateTime? ArchivedAtUtc,
    string? TemplateKey = null,
    string? TemplateName = null,
    string ValidationStatusCode = "Unknown",
    string ValidationSummary = "Not validated");
