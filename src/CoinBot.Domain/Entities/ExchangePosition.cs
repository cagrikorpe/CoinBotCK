using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class ExchangePosition : UserOwnedEntity
{
    public Guid ExchangeAccountId { get; set; }

    public ExchangeDataPlane Plane { get; set; } = ExchangeDataPlane.Futures;

    public string Symbol { get; set; } = string.Empty;

    public string PositionSide { get; set; } = string.Empty;

    public decimal Quantity { get; set; }

    public decimal EntryPrice { get; set; }

    public decimal BreakEvenPrice { get; set; }

    public decimal UnrealizedProfit { get; set; }

    public string MarginType { get; set; } = string.Empty;

    public decimal IsolatedWallet { get; set; }

    public DateTime ExchangeUpdatedAtUtc { get; set; }

    public DateTime SyncedAtUtc { get; set; }
}
