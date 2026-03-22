using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.DemoPortfolio;

public sealed record DemoLedgerTransactionSnapshot(
    Guid TransactionId,
    string OperationId,
    DemoLedgerTransactionType TransactionType,
    Guid? BotId,
    string PositionScopeKey,
    string? OrderId,
    string? FillId,
    string? Symbol,
    string? BaseAsset,
    string? QuoteAsset,
    DemoTradeSide? Side,
    decimal? Quantity,
    decimal? Price,
    string? FeeAsset,
    decimal? FeeAmount,
    decimal? FeeAmountInQuote,
    decimal? RealizedPnlDelta,
    decimal? PositionQuantityAfter,
    decimal? PositionCostBasisAfter,
    decimal? PositionAverageEntryPriceAfter,
    decimal? CumulativeRealizedPnlAfter,
    decimal? UnrealizedPnlAfter,
    decimal? CumulativeFeesInQuoteAfter,
    decimal? MarkPriceAfter,
    DateTime OccurredAtUtc,
    IReadOnlyCollection<DemoLedgerEntrySnapshot> Entries);
