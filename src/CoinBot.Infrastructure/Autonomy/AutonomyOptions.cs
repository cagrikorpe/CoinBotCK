using System.ComponentModel.DataAnnotations;

namespace CoinBot.Infrastructure.Autonomy;

public sealed class AutonomyOptions
{
    [Range(1, 300)]
    public int SelfHealingIntervalSeconds { get; set; } = 15;

    [Range(0.01, 1.0)]
    public decimal DefaultConfidenceScore { get; set; } = 0.85m;

    [Range(0.0, 1.0)]
    public decimal MaxFalsePositiveProbability { get; set; } = 0.35m;

    [Range(1, 1440)]
    public int ReviewQueueTtlMinutes { get; set; } = 30;
}
