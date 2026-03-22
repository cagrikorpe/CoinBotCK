using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class TradingStrategyVersion : UserOwnedEntity
{
    public Guid TradingStrategyId { get; set; }

    public int SchemaVersion { get; set; } = 1;

    public int VersionNumber { get; set; }

    public StrategyVersionStatus Status { get; set; } = StrategyVersionStatus.Draft;

    public string DefinitionJson { get; set; } = string.Empty;

    public DateTime? PublishedAtUtc { get; set; }

    public DateTime? ArchivedAtUtc { get; set; }
}
