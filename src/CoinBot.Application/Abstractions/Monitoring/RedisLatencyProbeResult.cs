namespace CoinBot.Application.Abstractions.Monitoring;

public enum RedisProbeStatus
{
    NotConfigured = 0,
    Succeeded = 1,
    Failed = 2
}

public sealed record RedisLatencyProbeResult(
    RedisProbeStatus Status,
    TimeSpan? Latency,
    string? Endpoint,
    string? FailureCode)
{
    public bool IsConfigured => Status != RedisProbeStatus.NotConfigured;

    public bool IsHealthy => Status == RedisProbeStatus.Succeeded;
}
