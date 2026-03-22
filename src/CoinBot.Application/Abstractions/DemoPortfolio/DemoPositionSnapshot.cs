namespace CoinBot.Application.Abstractions.DemoPortfolio;

public sealed record DemoPositionSnapshot(
    Guid? BotId,
    string PositionScopeKey,
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    decimal Quantity,
    decimal CostBasis,
    decimal AverageEntryPrice,
    decimal RealizedPnl,
    decimal UnrealizedPnl,
    decimal TotalFeesInQuote,
    decimal? LastMarkPrice,
    decimal? LastFillPrice,
    DateTime? LastFilledAtUtc,
    DateTime? LastValuationAtUtc);
