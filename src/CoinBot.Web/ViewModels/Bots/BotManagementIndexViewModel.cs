namespace CoinBot.Web.ViewModels.Bots;

public sealed class BotManagementIndexViewModel
{
    public BotManagementIndexViewModel(IReadOnlyCollection<BotManagementRowViewModel> bots)
    {
        Bots = bots;
    }

    public IReadOnlyCollection<BotManagementRowViewModel> Bots { get; }
}

public sealed record BotManagementRowViewModel(
    Guid BotId,
    string Name,
    string StrategyDisplayName,
    string StrategyKey,
    bool HasPublishedStrategyVersion,
    string Symbol,
    string QuantityDisplay,
    string ExchangeAccountDisplayName,
    bool ExchangeAccountIsActive,
    bool ExchangeAccountIsWritable,
    string LeverageDisplay,
    string MarginType,
    bool IsEnabled,
    int OpenOrderCount,
    int OpenPositionCount,
    string LastJobStatus,
    string? LastJobErrorCode,
    string LastExecutionState,
    string? LastExecutionFailureCode,
    string? LastExecutionBlockDetail,
    string? LastExecutionStageText,
    string? LastExecutionSubmitStatusText,
    string? LastExecutionRetryText,
    string? LastExecutionProtectionText,
    string? LastExecutionTransitionText,
    string? LastExecutionCorrelationText,
    string? LastExecutionClientOrderText,
    string? LastExecutionDuplicateText,
    bool IsCooldownActive,
    string? CooldownBlockedUntilLabel,
    string? CooldownRemainingText,
    string UpdatedAtLabel,
    string LastExecutionAtLabel,
    string? MarketDataBadgeText = null,
    string? LastCandleAtLabel = null,
    string? DataAgeText = null,
    string? ContinuityStateText = null,
    string? ContinuityGapText = null,
    string? AffectedMarketText = null,
    string? StaleReasonText = null,
    string? DecisionText = null,
    string? DecisionReasonCodeText = null,
    string? DecisionSummaryText = null,
    string? DecisionAtText = null,
    string? MarketStaleThresholdText = null,
    string? ContinuityGapStartedText = null,
    string? ContinuityGapLastSeenText = null,
    string? ContinuityRecoveryText = null);

