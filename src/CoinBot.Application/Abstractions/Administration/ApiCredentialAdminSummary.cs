namespace CoinBot.Application.Abstractions.Administration;

public sealed record ApiCredentialAdminSummary(
    Guid ExchangeAccountId,
    string OwnerUserId,
    string ExchangeName,
    string DisplayName,
    bool IsReadOnly,
    string? MaskedFingerprint,
    string ValidationStatus,
    string? PermissionSummary,
    DateTime? LastValidationAtUtc,
    string? LastFailureReason);
