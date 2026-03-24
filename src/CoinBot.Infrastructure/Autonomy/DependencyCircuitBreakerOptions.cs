using System.ComponentModel.DataAnnotations;

namespace CoinBot.Infrastructure.Autonomy;

public sealed class DependencyCircuitBreakerOptions
{
    [Range(1, 10)]
    public int FailureThreshold { get; set; } = 3;

    [Range(5, 3600)]
    public int CooldownSeconds { get; set; } = 60;

    [Range(1, 1440)]
    public int ReviewQueueTtlMinutes { get; set; } = 30;
}
