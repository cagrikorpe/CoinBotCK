using CoinBot.Domain.Entities;

namespace CoinBot.Infrastructure.Identity;

public sealed class MfaEmailOtpChallenge : BaseEntity
{
    public string UserId { get; set; } = string.Empty;

    public string Purpose { get; set; } = string.Empty;

    public string TokenCiphertext { get; set; } = string.Empty;

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? ConsumedAtUtc { get; set; }

    public int FailedAttemptCount { get; set; }

    public DateTime? LastAttemptedAtUtc { get; set; }
}
