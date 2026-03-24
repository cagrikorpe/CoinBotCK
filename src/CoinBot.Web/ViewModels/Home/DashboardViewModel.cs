//namespace CoinBot.Web.ViewModels.Home;

//public sealed record DashboardViewModel(
//    IReadOnlyCollection<DashboardMarketTickerViewModel> MarketTickers,
//    string MarketDataHubPath);


using System.Collections.Generic;

namespace CoinBot.Web.ViewModels.Home;

public sealed record DashboardViewModel(
    IReadOnlyCollection<DashboardMarketTickerViewModel> MarketTickers,
    string MarketDataHubPath,
    List<KpiItemViewModel> Kpis,
    List<AiFeedItemViewModel> AiFeed,
    List<OpenPositionViewModel> OpenPositions
);

public record KpiItemViewModel(string Label, string Value, string Help, string Tone, string Tag);

public record AiFeedItemViewModel(string Time, string Symbol, string Direction, string Confidence, string Reason, string Tone, bool Veto);

// 11 parametreli tam uyumlu model
public record OpenPositionViewModel(
    string Symbol,
    string Direction,
    string DirectionTone,
    string Leverage,
    string Entry,
    string Current,
    string Pnl,
    string PnlTone,
    string Risk,
    string RiskTone,
    string Updated
);
