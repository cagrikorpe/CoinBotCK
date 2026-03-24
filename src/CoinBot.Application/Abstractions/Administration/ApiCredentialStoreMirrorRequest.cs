namespace CoinBot.Application.Abstractions.Administration;

public sealed record ApiCredentialStoreMirrorRequest(
    Guid ExchangeAccountId,
    string OwnerUserId,
    string ApiKeyCiphertext,
    string ApiSecretCiphertext,
    string CredentialFingerprint,
    string KeyVersion,
    int EncryptedBlobVersion,
    DateTime StoredAtUtc);
