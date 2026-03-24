namespace CoinBot.Application.Abstractions.Administration;

public sealed record ApiCredentialValidationRequest(
    Guid ExchangeAccountId,
    string OwnerUserId,
    bool IsKeyValid,
    bool CanTrade,
    bool CanWithdraw,
    bool SupportsSpot,
    bool SupportsFutures,
    bool IsEnvironmentMatch,
    bool HasTimestampSkew,
    bool HasIpRestrictionIssue,
    string? EnvironmentScope,
    string Actor,
    string? CorrelationId = null,
    string? FailureReason = null,
    string? PermissionSummary = null,
    DateTime? ValidatedAtUtc = null);
