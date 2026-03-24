namespace CoinBot.Domain.Entities;

public sealed class ApiCredential : BaseEntity
{
    public Guid ExchangeAccountId { get; set; }

    public string OwnerUserId { get; set; } = string.Empty;

    public string ApiKeyCiphertext { get; set; } = string.Empty;

    public string ApiSecretCiphertext { get; set; } = string.Empty;

    public string CredentialFingerprint { get; set; } = string.Empty;

    public string KeyVersion { get; set; } = string.Empty;

    public int EncryptedBlobVersion { get; set; }

    public string ValidationStatus { get; set; } = string.Empty;

    public string? PermissionSummary { get; set; }

    public DateTime StoredAtUtc { get; set; }

    public DateTime? LastValidatedAtUtc { get; set; }

    public string? LastFailureReason { get; set; }
}
