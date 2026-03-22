namespace CoinBot.Domain.Entities;

public sealed class DemoLedgerEntry : UserOwnedEntity
{
    public Guid DemoLedgerTransactionId { get; set; }

    public string Asset { get; set; } = string.Empty;

    public decimal AvailableDelta { get; set; }

    public decimal ReservedDelta { get; set; }

    public decimal AvailableBalanceAfter { get; set; }

    public decimal ReservedBalanceAfter { get; set; }
}
