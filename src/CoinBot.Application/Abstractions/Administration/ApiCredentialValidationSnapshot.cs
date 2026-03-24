namespace CoinBot.Application.Abstractions.Administration;

public sealed record ApiCredentialValidationSnapshot(
    Guid ApiCredentialId,
    Guid ExchangeAccountId,
    string OwnerUserId,
    string ValidationStatus,
    string PermissionSummary,
    string? FailureReason,
    bool IsKeyValid,
    bool CanTrade,
    bool CanWithdraw,
    bool SupportsSpot,
    bool SupportsFutures,
    bool IsEnvironmentMatch,
    bool HasTimestampSkew,
    bool HasIpRestrictionIssue,
    string? EnvironmentScope,
    DateTime ValidatedAtUtc);
