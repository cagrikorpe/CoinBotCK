using CoinBot.Domain.Entities;

namespace CoinBot.Infrastructure.Identity;

public sealed class MfaRecoveryCode : BaseEntity
{
    public string UserId { get; set; } = string.Empty;

    public string CodeHash { get; set; } = string.Empty;

    public DateTime? ConsumedAtUtc { get; set; }
}
