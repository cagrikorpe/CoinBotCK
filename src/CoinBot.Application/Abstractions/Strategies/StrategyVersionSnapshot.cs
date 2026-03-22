using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategyVersionSnapshot(
    Guid StrategyVersionId,
    Guid StrategyId,
    int SchemaVersion,
    int VersionNumber,
    StrategyVersionStatus Status,
    DateTime? PublishedAtUtc,
    DateTime? ArchivedAtUtc);
