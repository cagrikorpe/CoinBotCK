using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.DemoPortfolio;

public sealed record DemoMarkPriceUpdateRequest(
    string OwnerUserId,
    ExecutionEnvironment Environment,
    string OperationId,
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    decimal MarkPrice,
    Guid? BotId = null,
    DateTime? OccurredAtUtc = null,
    DemoPositionKind PositionKind = DemoPositionKind.Spot,
    decimal? LastPrice = null,
    decimal? FundingRate = null);
