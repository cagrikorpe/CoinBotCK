using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class ExchangeBalance : UserOwnedEntity
{
    public Guid ExchangeAccountId { get; set; }

    public ExchangeDataPlane Plane { get; set; } = ExchangeDataPlane.Futures;

    public string Asset { get; set; } = string.Empty;

    public decimal WalletBalance { get; set; }

    public decimal CrossWalletBalance { get; set; }

    public decimal? AvailableBalance { get; set; }

    public decimal? MaxWithdrawAmount { get; set; }

    public decimal? LockedBalance { get; set; }

    public DateTime ExchangeUpdatedAtUtc { get; set; }

    public DateTime SyncedAtUtc { get; set; }
}
