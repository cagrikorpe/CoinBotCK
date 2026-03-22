using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class ExchangeAccount : UserOwnedEntity
{
    public string ExchangeName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool IsReadOnly { get; set; }

    public DateTime? LastValidatedAt { get; set; }

    public string? ApiKeyCiphertext { get; set; }

    public string? ApiSecretCiphertext { get; set; }

    public string? CredentialFingerprint { get; set; }

    public string? CredentialKeyVersion { get; set; }

    public ExchangeCredentialStatus CredentialStatus { get; set; } = ExchangeCredentialStatus.Missing;

    public DateTime? CredentialStoredAtUtc { get; set; }

    public DateTime? CredentialLastAccessedAtUtc { get; set; }

    public DateTime? CredentialLastRotatedAtUtc { get; set; }

    public DateTime? CredentialRevalidateAfterUtc { get; set; }

    public DateTime? CredentialRotateAfterUtc { get; set; }
}
