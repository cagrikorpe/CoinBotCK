namespace CoinBot.Domain.Entities;

public abstract class UserOwnedEntity : BaseEntity
{
    public string OwnerUserId { get; set; } = string.Empty;
}
