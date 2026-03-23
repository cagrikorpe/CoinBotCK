namespace CoinBot.Web.ViewModels.Home;

public sealed record DashboardViewModel(
    IReadOnlyCollection<DashboardMarketTickerViewModel> MarketTickers,
    string MarketDataHubPath);
