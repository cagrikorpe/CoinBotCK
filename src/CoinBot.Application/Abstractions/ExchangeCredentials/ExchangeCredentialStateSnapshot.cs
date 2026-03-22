using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.ExchangeCredentials;

public sealed record ExchangeCredentialStateSnapshot(
    Guid ExchangeAccountId,
    ExchangeCredentialStatus Status,
    string? Fingerprint,
    string? KeyVersion,
    DateTime? StoredAtUtc,
    DateTime? LastValidatedAtUtc,
    DateTime? LastAccessedAtUtc,
    DateTime? LastRotatedAtUtc,
    DateTime? RevalidateAfterUtc,
    DateTime? RotateAfterUtc);
