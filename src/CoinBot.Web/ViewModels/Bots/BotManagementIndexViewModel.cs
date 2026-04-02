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
    string UpdatedAtLabel,
    string LastExecutionAtLabel);
