namespace CoinBot.Application.Abstractions.Indicators;

public sealed record MovingAverageConvergenceDivergenceSnapshot(
    int FastPeriod,
    int SlowPeriod,
    int SignalPeriod,
    bool IsReady,
    decimal? MacdLine,
    decimal? SignalLine,
    decimal? Histogram);
