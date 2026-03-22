namespace CoinBot.Domain.Entities;

public sealed class BackgroundJobLock : BaseEntity
{
    public string JobKey { get; set; } = string.Empty;

    public string JobType { get; set; } = string.Empty;

    public string WorkerInstanceId { get; set; } = string.Empty;

    public DateTime AcquiredAtUtc { get; set; }

    public DateTime LastKeepAliveAtUtc { get; set; }

    public DateTime LeaseExpiresAtUtc { get; set; }
}
