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
    ContinuityGap = 9,
    PilotConfigurationMissing = 10,
    PilotRequiresDevelopment = 11,
    PilotTestnetEndpointMismatch = 12,
    PilotCredentialValidationUnavailable = 13,
    PilotCredentialEnvironmentMismatch = 14,
    PrivatePlaneUnavailable = 15,
    PrivatePlaneStale = 16
}
