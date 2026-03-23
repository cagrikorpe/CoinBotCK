using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;

namespace CoinBot.Web.Hubs;

public sealed record MarketChartSeedSnapshot(
    string Symbol,
    string Timeframe,
    IReadOnlyCollection<MarketCandleSnapshot> Candles,
    StrategyIndicatorSnapshot? IndicatorSnapshot);
