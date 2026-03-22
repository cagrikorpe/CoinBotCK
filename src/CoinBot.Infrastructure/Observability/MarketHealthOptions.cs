using System.ComponentModel.DataAnnotations;

namespace CoinBot.Infrastructure.Observability;

public sealed class MarketHealthOptions
{
    [Range(1, 1440)]
    public int ValidationFreshnessMinutes { get; set; } = 15;
}
