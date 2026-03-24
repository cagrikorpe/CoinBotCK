using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class GlobalSystemState
{
    public Guid Id { get; set; }

    public GlobalSystemStateKind State { get; set; } = GlobalSystemStateKind.Active;

    public string ReasonCode { get; set; } = string.Empty;

    public string? Message { get; set; }

    public string Source { get; set; } = string.Empty;

    public string? CorrelationId { get; set; }

    public bool IsManualOverride { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public string? UpdatedByUserId { get; set; }

    public string? UpdatedFromIp { get; set; }

    public long Version { get; set; }
}
