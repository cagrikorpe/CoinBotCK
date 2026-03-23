using System.ComponentModel.DataAnnotations;

namespace CoinBot.Infrastructure.Execution;

public sealed class DemoFillSimulatorOptions
{
    [Range(0, 1000)]
    public int MakerFeeBps { get; set; } = 8;

    [Range(0, 1000)]
    public int TakerFeeBps { get; set; } = 10;

    [Range(0, 1000)]
    public int MarketOrderBaseSlippageBps { get; set; } = 6;

    [Range(0, 1000)]
    public int MissingPriceFallbackPenaltyBps { get; set; } = 4;

    [Range(0, 1000)]
    public int SizeImpactStepBps { get; set; } = 2;

    [Range(typeof(decimal), "1", "1000000000")]
    public decimal SizeImpactNotionalStep { get; set; } = 5000m;

    [Range(0, 1000)]
    public int MaxMarketOrderSlippageBps { get; set; } = 20;

    [Range(typeof(decimal), "0.10", "0.95")]
    public decimal PartialFillRatio { get; set; } = 0.60m;

    [Range(typeof(decimal), "1", "1000000000")]
    public decimal PartialFillMinNotional { get; set; } = 10000m;
}
