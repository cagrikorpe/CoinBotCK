using System.ComponentModel.DataAnnotations;

namespace CoinBot.Infrastructure.DemoPortfolio;

public sealed class DemoSessionOptions
{
    [Required]
    [MaxLength(32)]
    public string DefaultSeedAsset { get; set; } = "USDT";

    [Range(typeof(decimal), "0.000000000000000001", "79228162514264337593543950335")]
    public decimal DefaultSeedAmount { get; set; } = 10000m;

    [Range(typeof(decimal), "0.000000000000000001", "1")]
    public decimal ConsistencyTolerance { get; set; } = 0.00000001m;
}
