using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.DemoPortfolio;

public sealed record DemoFillAccountingRequest(
    string OwnerUserId,
    ExecutionEnvironment Environment,
    string OperationId,
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    DemoTradeSide Side,
    decimal Quantity,
    decimal Price,
    decimal ConsumedReservedDebitAmount,
    Guid? BotId = null,
    string? OrderId = null,
    string? FillId = null,
    string? FeeAsset = null,
    decimal FeeAmount = 0m,
    decimal? FeeAmountInQuote = null,
    decimal? MarkPrice = null,
    DateTime? OccurredAtUtc = null,
    DemoPositionKind PositionKind = DemoPositionKind.Spot,
    DemoMarginMode? MarginMode = null,
    decimal? Leverage = null,
    decimal? MaintenanceMarginRate = null,
    decimal? FundingRate = null);
