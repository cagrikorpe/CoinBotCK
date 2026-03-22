using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class BackgroundJobState : BaseEntity
{
    public string JobKey { get; set; } = string.Empty;

    public string JobType { get; set; } = string.Empty;

    public Guid? BotId { get; set; }

    public BackgroundJobStatus Status { get; set; } = BackgroundJobStatus.Pending;

    public DateTime NextRunAtUtc { get; set; }

    public DateTime? LastStartedAtUtc { get; set; }

    public DateTime? LastCompletedAtUtc { get; set; }

    public DateTime? LastFailedAtUtc { get; set; }

    public DateTime? LastHeartbeatAtUtc { get; set; }

    public int ConsecutiveFailureCount { get; set; }

    public string? IdempotencyKey { get; set; }

    public string? LastErrorCode { get; set; }
}
