namespace CoinBot.Application.Abstractions.Execution;

public enum ExecutionGateBlockedReason
{
    SwitchConfigurationMissing = 0,
    TradeMasterDisarmed = 1,
    LiveExecutionBlockedByDemoMode = 2,
    RequestedEnvironmentDoesNotMatchResolvedMode = 3,
    MarketDataUnavailable = 4,
    StaleMarketData = 5,
    ClockDriftExceeded = 6,
    DataLatencyGuardUnavailable = 7,
    DemoSessionDriftDetected = 8,
    ContinuityGap = 9
}
