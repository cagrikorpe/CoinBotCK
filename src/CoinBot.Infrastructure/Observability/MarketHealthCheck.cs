using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Observability;

public sealed class MarketHealthCheck(
    ApplicationDbContext dbContext,
    IDataLatencyCircuitBreaker dataLatencyCircuitBreaker,
    IOptions<MarketHealthOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var activeExchangeAccounts = await dbContext.ExchangeAccounts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity => !entity.IsDeleted)
            .ToListAsync(cancellationToken);

        if (activeExchangeAccounts.Count == 0)
        {
            return HealthCheckResult.Unhealthy(
                "Market readiness is not ready because no active exchange account exists.",
                data: CreateData(exchangeAccounts: 0, latestObservedAtUtc: null, marketSnapshot: null, validationFreshnessMinutes: options.Value.ValidationFreshnessMinutes));
        }

        var marketSnapshot = await dataLatencyCircuitBreaker.GetSnapshotAsync(cancellationToken: cancellationToken);
        var latestObservedAtUtc = marketSnapshot.LatestHeartbeatReceivedAtUtc ?? marketSnapshot.LatestDataTimestampAtUtc;
        var freshnessThreshold = TimeSpan.FromMinutes(options.Value.ValidationFreshnessMinutes);
        var age = latestObservedAtUtc is null
            ? freshnessThreshold + TimeSpan.FromSeconds(1)
            : timeProvider.GetUtcNow().UtcDateTime - latestObservedAtUtc.Value;

        if (latestObservedAtUtc is null)
        {
            return HealthCheckResult.Unhealthy(
                "Market readiness state is missing.",
                data: CreateData(activeExchangeAccounts.Count, latestObservedAtUtc, marketSnapshot, options.Value.ValidationFreshnessMinutes));
        }

        if (age > freshnessThreshold)
        {
            return HealthCheckResult.Unhealthy(
                "Market readiness state is stale.",
                data: CreateData(activeExchangeAccounts.Count, latestObservedAtUtc, marketSnapshot, options.Value.ValidationFreshnessMinutes));
        }

        return marketSnapshot.StateCode == DegradedModeStateCode.Normal
            ? HealthCheckResult.Healthy(
                "Market readiness state is fresh and healthy.",
                CreateData(activeExchangeAccounts.Count, latestObservedAtUtc, marketSnapshot, options.Value.ValidationFreshnessMinutes))
            : HealthCheckResult.Unhealthy(
                "Market readiness state reports a non-healthy market state.",
                data: CreateData(activeExchangeAccounts.Count, latestObservedAtUtc, marketSnapshot, options.Value.ValidationFreshnessMinutes));
    }

    private static IReadOnlyDictionary<string, object> CreateData(
        int exchangeAccounts,
        DateTime? latestObservedAtUtc,
        DegradedModeSnapshot? marketSnapshot,
        int validationFreshnessMinutes)
    {
        return new Dictionary<string, object>
        {
            ["exchangeAccounts"] = exchangeAccounts,
            ["latestObservedAtUtc"] = latestObservedAtUtc?.ToString("O") ?? "missing",
            ["validationFreshnessMinutes"] = validationFreshnessMinutes,
            ["stateCode"] = marketSnapshot?.StateCode.ToString() ?? "missing",
            ["reasonCode"] = marketSnapshot?.ReasonCode.ToString() ?? "missing",
            ["signalFlowBlocked"] = marketSnapshot?.SignalFlowBlocked ?? true,
            ["executionFlowBlocked"] = marketSnapshot?.ExecutionFlowBlocked ?? true,
            ["latestDataTimestampAtUtc"] = marketSnapshot?.LatestDataTimestampAtUtc?.ToString("O") ?? "missing",
            ["latestHeartbeatReceivedAtUtc"] = marketSnapshot?.LatestHeartbeatReceivedAtUtc?.ToString("O") ?? "missing",
            ["latestDataAgeMilliseconds"] = marketSnapshot?.LatestDataAgeMilliseconds?.ToString() ?? "missing",
            ["latestClockDriftMilliseconds"] = marketSnapshot?.LatestClockDriftMilliseconds?.ToString() ?? "missing",
            ["isPersisted"] = marketSnapshot?.IsPersisted ?? false
        };
    }
}
