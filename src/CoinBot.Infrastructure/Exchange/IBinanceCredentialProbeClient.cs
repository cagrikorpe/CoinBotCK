using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Domain.Enums;

namespace CoinBot.Infrastructure.Exchange;

public interface IBinanceCredentialProbeClient
{
    Task<BinanceCredentialProbeSnapshot> ProbeAsync(
        string apiKey,
        string apiSecret,
        CancellationToken cancellationToken = default);

    Task<BinanceCredentialProbeSnapshot> ProbeAsync(
        string apiKey,
        string apiSecret,
        ExecutionEnvironment requestedEnvironment,
        ExchangeTradeModeSelection requestedTradeMode,
        CancellationToken cancellationToken = default)
    {
        return ProbeAsync(apiKey, apiSecret, cancellationToken);
    }
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
