namespace CoinBot.Domain.Entities;

public sealed class AuditLog : BaseEntity
{
    public string Actor { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;

    public string? Context { get; set; }

    public string CorrelationId { get; set; } = string.Empty;

    public string Outcome { get; set; } = string.Empty;

    public string Environment { get; set; } = string.Empty;
}
