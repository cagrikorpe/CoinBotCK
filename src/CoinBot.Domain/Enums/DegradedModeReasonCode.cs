namespace CoinBot.Domain.Enums;

public enum DegradedModeReasonCode
{
    None = 0,
    MarketDataUnavailable = 1,
    MarketDataLatencyBreached = 2,
    MarketDataLatencyCritical = 3,
    ClockDriftExceeded = 4,
    CandleDataGapDetected = 5,
    CandleDataDuplicateDetected = 6,
    CandleDataOutOfOrderDetected = 7
}
