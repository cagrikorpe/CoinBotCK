namespace CoinBot.Domain.Entities;

public sealed class DemoWallet : UserOwnedEntity
{
    public string Asset { get; set; } = string.Empty;

    public decimal AvailableBalance { get; set; }

    public decimal ReservedBalance { get; set; }

    public DateTime LastActivityAtUtc { get; set; }
}
