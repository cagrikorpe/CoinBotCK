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
    DateTime RefreshedAtUtc);
