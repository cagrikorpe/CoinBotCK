using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.ExchangeCredentials;

public sealed record UserExchangeEnvironmentSummary(
    ExecutionEnvironment EffectiveEnvironment,
    string EffectiveEnvironmentLabel,
    string EffectiveEnvironmentTone,
    string ResolutionSourceLabel,
    string ResolutionReason,
    bool HasExplicitLiveApproval);

public sealed record UserExchangeRiskOverrideSummary(
    string RiskProfileName,
    decimal? MaxDailyLossPercentage,
    decimal? MaxPositionSizePercentage,
    decimal? MaxLeverage,
    bool KillSwitchEnabled,
    bool SessionDisabled,
    bool ReduceOnly,
    decimal? OverrideLeverageCap,
    decimal? OverrideMaxOrderSize,
    int? OverrideMaxDailyTrades,
    string SummaryLabel,
    string SummaryTone,
    string SummaryText);

public sealed record UserExchangeAccountSummary(
    Guid ExchangeAccountId,
    string ExchangeName,
    string DisplayName,
    string CredentialStatusLabel,
    string CredentialStatusTone,
    string ModeLabel,
    string SyncStatusLabel,
    string SyncStatusTone,
    string? MaskedFingerprint,
    string PermissionSummary,
    string EnvironmentLabel,
    string EnvironmentTone,
    DateTime? LastValidatedAtUtc,
    string LastValidatedLabel,
    string? LastFailureReason);

public sealed record UserExchangeValidationHistoryEntry(
    Guid ExchangeAccountId,
    string ExchangeDisplayName,
    DateTime ValidatedAtUtc,
    string TimeLabel,
    string ValidationStatus,
    string ValidationTone,
    bool IsKeyValid,
    bool CanTrade,
    bool CanWithdraw,
    bool SupportsSpot,
    bool SupportsFutures,
    string EnvironmentScope,
    bool IsEnvironmentMatch,
    string PermissionSummary,
    string? FailureReason,
    string? MaskedFingerprint);

public sealed record UserExchangeCommandCenterSnapshot(
    string UserId,
    string DisplayName,
    UserExchangeEnvironmentSummary Environment,
    UserExchangeRiskOverrideSummary RiskOverride,
    IReadOnlyCollection<UserExchangeAccountSummary> Accounts,
    IReadOnlyCollection<UserExchangeValidationHistoryEntry> ValidationHistory,
    DateTime LastRefreshedAtUtc)
{
    public static UserExchangeCommandCenterSnapshot Empty(
        string userId,
        string displayName,
        UserExchangeEnvironmentSummary environment,
        UserExchangeRiskOverrideSummary riskOverride,
        DateTime lastRefreshedAtUtc) =>
        new(
            userId,
            displayName,
            environment,
            riskOverride,
            Array.Empty<UserExchangeAccountSummary>(),
            Array.Empty<UserExchangeValidationHistoryEntry>(),
            lastRefreshedAtUtc);
}

public sealed record ConnectUserBinanceCredentialRequest(
    string UserId,
    Guid? ExchangeAccountId,
    string ApiKey,
    string ApiSecret,
    ExecutionEnvironment RequestedEnvironment,
    ExchangeTradeModeSelection RequestedTradeMode,
    string Actor,
    string? CorrelationId = null);

public sealed record ConnectUserBinanceCredentialResult(
    Guid ExchangeAccountId,
    bool IsValid,
    string StatusLabel,
    string StatusTone,
    string UserMessage,
    string? SafeFailureReason,
    string PermissionSummary,
    string EnvironmentLabel);
