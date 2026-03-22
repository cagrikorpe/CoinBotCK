using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class DemoLedgerTransaction : UserOwnedEntity
{
    public string OperationId { get; set; } = string.Empty;

    public DemoLedgerTransactionType TransactionType { get; set; }

    public Guid? BotId { get; set; }

    public string PositionScopeKey { get; set; } = string.Empty;

    public string? OrderId { get; set; }

    public string? FillId { get; set; }

    public string? Symbol { get; set; }

    public string? BaseAsset { get; set; }

    public string? QuoteAsset { get; set; }

    public DemoPositionKind? PositionKind { get; set; }

    public DemoMarginMode? MarginMode { get; set; }

    public DemoTradeSide? Side { get; set; }

    public decimal? Quantity { get; set; }

    public decimal? Price { get; set; }

    public string? FeeAsset { get; set; }

    public decimal? FeeAmount { get; set; }

    public decimal? FeeAmountInQuote { get; set; }

    public decimal? Leverage { get; set; }

    public decimal? FundingRate { get; set; }

    public decimal? FundingDeltaInQuote { get; set; }

    public decimal? RealizedPnlDelta { get; set; }

    public decimal? PositionQuantityAfter { get; set; }

    public decimal? PositionCostBasisAfter { get; set; }

    public decimal? PositionAverageEntryPriceAfter { get; set; }

    public decimal? CumulativeRealizedPnlAfter { get; set; }

    public decimal? UnrealizedPnlAfter { get; set; }

    public decimal? CumulativeFeesInQuoteAfter { get; set; }

    public decimal? NetFundingInQuoteAfter { get; set; }

    public decimal? LastPriceAfter { get; set; }

    public decimal? MarkPriceAfter { get; set; }

    public decimal? MaintenanceMarginRateAfter { get; set; }

    public decimal? MaintenanceMarginAfter { get; set; }

    public decimal? MarginBalanceAfter { get; set; }

    public decimal? LiquidationPriceAfter { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}
