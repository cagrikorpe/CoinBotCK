namespace CoinBot.Application.Abstractions.Risk;

public sealed record PreTradeRiskSnapshot(
    bool IsVirtualCheck,
    Guid? RiskProfileId,
    string? RiskProfileName,
    bool KillSwitchEnabled,
    decimal CurrentEquity,
    decimal CurrentGrossExposure,
    decimal CurrentLeverage,
    decimal CurrentExposurePercentage,
    decimal CurrentDailyLossAmount,
    decimal CurrentDailyLossPercentage,
    decimal? MaxDailyLossPercentage,
    decimal? MaxExposurePercentage,
    decimal? MaxLeverage,
    int OpenPositionCount,
    DateTime EvaluatedAtUtc);
