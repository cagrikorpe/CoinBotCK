namespace CoinBot.Application.Abstractions.MarketData;

public sealed record SymbolMetadataSnapshot(
    string Symbol,
    string Exchange,
    string BaseAsset,
    string QuoteAsset,
    decimal TickSize,
    decimal StepSize,
    string TradingStatus,
    bool IsTradingEnabled,
    DateTime RefreshedAtUtc)
{
    public decimal? MinQuantity { get; init; }

    public decimal? MinNotional { get; init; }

    public int? PricePrecision { get; init; }

    public int? QuantityPrecision { get; init; }
}
