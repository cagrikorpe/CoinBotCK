using CoinBot.Domain.Enums;

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
    DateTime? LastValuationAtUtc,
    DemoPositionKind PositionKind = DemoPositionKind.Spot,
    DemoMarginMode? MarginMode = null,
    decimal? Leverage = null,
    decimal? LastPrice = null,
    decimal? MaintenanceMarginRate = null,
    decimal? MaintenanceMargin = null,
    decimal? MarginBalance = null,
    decimal NetFundingInQuote = 0m,
    decimal? LiquidationPrice = null);
