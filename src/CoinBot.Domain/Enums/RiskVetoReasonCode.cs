namespace CoinBot.Domain.Enums;

public enum RiskVetoReasonCode
{
    None = 0,
    RiskProfileMissing = 1,
    KillSwitchEnabled = 2,
    AccountEquityUnavailable = 3,
    DailyLossLimitBreached = 4,
    ExposureLimitBreached = 5,
    LeverageLimitBreached = 6,
    WeeklyLossLimitBreached = 7,
    SymbolExposureLimitBreached = 8,
    MaxConcurrentPositionsBreached = 9,
    CoinSpecificLimitBreached = 10,
    RiskProfileConfigurationInvalid = 11
}
