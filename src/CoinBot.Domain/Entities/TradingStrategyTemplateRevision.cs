namespace CoinBot.Domain.Entities;

public sealed class TradingStrategyTemplateRevision : UserOwnedEntity
{
    public Guid TradingStrategyTemplateId { get; set; }

    public int RevisionNumber { get; set; }

    public int SchemaVersion { get; set; } = 1;

    public string DefinitionJson { get; set; } = string.Empty;

    public string ValidationStatusCode { get; set; } = "Unknown";

    public string ValidationSummary { get; set; } = "Not validated";

    public string? SourceTemplateKey { get; set; }

    public int? SourceRevisionNumber { get; set; }

    public DateTime? ArchivedAtUtc { get; set; }
}
