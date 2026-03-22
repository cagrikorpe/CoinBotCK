namespace CoinBot.Domain.Entities;

public sealed class DemoPosition : UserOwnedEntity
{
    public Guid? BotId { get; set; }

    public string PositionScopeKey { get; set; } = string.Empty;

    public string Symbol { get; set; } = string.Empty;

    public string BaseAsset { get; set; } = string.Empty;

    public string QuoteAsset { get; set; } = string.Empty;

    public decimal Quantity { get; set; }

    public decimal CostBasis { get; set; }

    public decimal AverageEntryPrice { get; set; }

    public decimal RealizedPnl { get; set; }

    public decimal UnrealizedPnl { get; set; }

    public decimal TotalFeesInQuote { get; set; }

    public decimal? LastMarkPrice { get; set; }

    public decimal? LastFillPrice { get; set; }

    public DateTime? LastFilledAtUtc { get; set; }

    public DateTime? LastValuationAtUtc { get; set; }
}
