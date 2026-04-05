using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Exchange;

public sealed record ExchangePositionSnapshot(
    string Symbol,
    string PositionSide,
    decimal Quantity,
    decimal EntryPrice,
    decimal BreakEvenPrice,
    decimal UnrealizedProfit,
    string MarginType,
    decimal IsolatedWallet,
    DateTime ExchangeUpdatedAtUtc,
    ExchangeDataPlane Plane = ExchangeDataPlane.Futures);
