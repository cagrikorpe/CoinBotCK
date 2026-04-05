namespace CoinBot.Domain.Entities;

public sealed class TradingStrategyTemplate : UserOwnedEntity
{
    public string TemplateKey { get; set; } = string.Empty;

    public string TemplateName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public int SchemaVersion { get; set; } = 1;

    public string DefinitionJson { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public Guid? ActiveTradingStrategyTemplateRevisionId { get; set; }

    public Guid? LatestTradingStrategyTemplateRevisionId { get; set; }

    public string? SourceTemplateKey { get; set; }

    public DateTime? ArchivedAtUtc { get; set; }
}
