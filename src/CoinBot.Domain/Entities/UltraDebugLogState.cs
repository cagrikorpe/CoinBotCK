namespace CoinBot.Domain.Entities;

public sealed class UltraDebugLogState
{
    public Guid Id { get; set; }

    public bool IsEnabled { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    public string? DurationKey { get; set; }

    public string? EnabledByAdminId { get; set; }

    public string? EnabledByAdminEmail { get; set; }

    public string? AutoDisabledReason { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? NormalLogsLimitMb { get; set; }

    public int? UltraLogsLimitMb { get; set; }
}
