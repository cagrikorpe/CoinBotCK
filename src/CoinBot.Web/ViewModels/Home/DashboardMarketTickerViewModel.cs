namespace CoinBot.Web.ViewModels.Home;

public sealed record DashboardMarketTickerViewModel(
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    decimal? Price,
    DateTime? ObservedAtUtc,
    DateTime? ReceivedAtUtc,
    string Source,
    decimal? TickSize,
    decimal? StepSize,
    string TradingStatus,
    bool IsTradingEnabled);
