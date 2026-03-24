namespace CoinBot.Application.Abstractions.ExchangeCredentials;

public sealed record SetExchangeCredentialValidationStateRequest(
    Guid ExchangeAccountId,
    bool IsValid,
    string Actor,
    string? CorrelationId = null,
    bool IsKeyValid = true,
    bool CanTrade = true,
    bool CanWithdraw = false,
    bool SupportsSpot = true,
    bool SupportsFutures = true,
    string? EnvironmentScope = null,
    bool IsEnvironmentMatch = true,
    bool HasTimestampSkew = false,
    bool HasIpRestrictionIssue = false,
    string? FailureReason = null,
    string? PermissionSummary = null);
