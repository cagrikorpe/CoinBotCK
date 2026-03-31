namespace CoinBot.Infrastructure.Exchange;

public interface IBinanceCredentialProbeClient
{
    Task<BinanceCredentialProbeSnapshot> ProbeAsync(
        string apiKey,
        string apiSecret,
        CancellationToken cancellationToken = default);
}

public sealed record BinanceCredentialProbeSnapshot(
    bool IsKeyValid,
    bool CanTrade,
    bool? CanWithdraw,
    bool SupportsSpot,
    bool SupportsFutures,
    bool HasTimestampSkew,
    bool HasIpRestrictionIssue,
    string SpotEnvironmentScope,
    string FuturesEnvironmentScope,
    string PermissionSummary,
    string? SafeFailureReason);
