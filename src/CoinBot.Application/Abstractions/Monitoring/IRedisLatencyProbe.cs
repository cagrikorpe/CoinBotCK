namespace CoinBot.Application.Abstractions.Monitoring;

public interface IRedisLatencyProbe
{
    Task<RedisLatencyProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
}
