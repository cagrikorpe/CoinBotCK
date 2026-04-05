using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class SpotPortfolioFill : UserOwnedEntity
{
    public Guid ExchangeAccountId { get; set; }

    public Guid ExecutionOrderId { get; set; }

    public ExchangeDataPlane Plane { get; set; } = ExchangeDataPlane.Spot;

    public string Symbol { get; set; } = string.Empty;

    public string BaseAsset { get; set; } = string.Empty;

    public string QuoteAsset { get; set; } = string.Empty;

    public ExecutionOrderSide Side { get; set; }

    public string ExchangeOrderId { get; set; } = string.Empty;

    public string ClientOrderId { get; set; } = string.Empty;

    public long TradeId { get; set; }

    public decimal Quantity { get; set; }

    public decimal QuoteQuantity { get; set; }

    public decimal Price { get; set; }

    public string? FeeAsset { get; set; }

    public decimal? FeeAmount { get; set; }

    public decimal FeeAmountInQuote { get; set; }

    public decimal RealizedPnlDelta { get; set; }

    public decimal HoldingQuantityAfter { get; set; }

    public decimal HoldingCostBasisAfter { get; set; }

    public decimal HoldingAverageCostAfter { get; set; }

    public decimal CumulativeRealizedPnlAfter { get; set; }

    public decimal CumulativeFeesInQuoteAfter { get; set; }

    public string Source { get; set; } = string.Empty;

    public string RootCorrelationId { get; set; } = string.Empty;

    public DateTime OccurredAtUtc { get; set; }
}
