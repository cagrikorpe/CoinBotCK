namespace CoinBot.Domain.Entities;

public sealed class ExchangeAccount : UserOwnedEntity
{
    public string ExchangeName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool IsReadOnly { get; set; }

    public DateTime? LastValidatedAt { get; set; }
}
