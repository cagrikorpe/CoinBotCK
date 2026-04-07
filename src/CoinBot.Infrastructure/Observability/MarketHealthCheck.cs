using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Observability;

public sealed class MarketHealthCheck(
    ApplicationDbContext dbContext,
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
                data: CreateData(exchangeAccounts: 0, latestSnapshotAtUtc: null, marketSnapshot: null, validationFreshnessMinutes: options.Value.ValidationFreshnessMinutes));
        }

        var marketSnapshot = await dbContext.HealthSnapshots
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.SnapshotKey == "market-watchdog", cancellationToken);

        if (marketSnapshot is null)
        {
            return HealthCheckResult.Unhealthy(
                "Market readiness snapshot is missing.",
                data: CreateData(activeExchangeAccounts.Count, latestSnapshotAtUtc: null, marketSnapshot, options.Value.ValidationFreshnessMinutes));
        }

        var freshnessThreshold = TimeSpan.FromMinutes(options.Value.ValidationFreshnessMinutes);
        var latestSnapshotAtUtc = marketSnapshot.LastUpdatedAtUtc;
        var age = timeProvider.GetUtcNow().UtcDateTime - latestSnapshotAtUtc;

        if (age > freshnessThreshold)
        {
            return HealthCheckResult.Unhealthy(
                "Market readiness snapshot is stale.",
                data: CreateData(activeExchangeAccounts.Count, latestSnapshotAtUtc, marketSnapshot, options.Value.ValidationFreshnessMinutes));
        }

        return marketSnapshot.HealthState == MonitoringHealthState.Healthy
            ? HealthCheckResult.Healthy(
                "Market readiness snapshot is fresh and healthy.",
                CreateData(activeExchangeAccounts.Count, latestSnapshotAtUtc, marketSnapshot, options.Value.ValidationFreshnessMinutes))
            : HealthCheckResult.Unhealthy(
                "Market readiness snapshot reports a non-healthy market state.",
                data: CreateData(activeExchangeAccounts.Count, latestSnapshotAtUtc, marketSnapshot, options.Value.ValidationFreshnessMinutes));
    }

    private static IReadOnlyDictionary<string, object> CreateData(
        int exchangeAccounts,
        DateTime? latestSnapshotAtUtc,
        HealthSnapshot? marketSnapshot,
        int validationFreshnessMinutes)
    {
        return new Dictionary<string, object>
        {
            ["exchangeAccounts"] = exchangeAccounts,
            ["latestSnapshotAtUtc"] = latestSnapshotAtUtc?.ToString("O") ?? "missing",
            ["validationFreshnessMinutes"] = validationFreshnessMinutes,
            ["snapshotKey"] = marketSnapshot?.SnapshotKey ?? "missing",
            ["snapshotHealthState"] = marketSnapshot?.HealthState.ToString() ?? "missing",
            ["snapshotCircuitBreakerState"] = marketSnapshot?.CircuitBreakerState.ToString() ?? "missing",
            ["snapshotDetail"] = marketSnapshot?.Detail ?? "missing"
        };
    }
}
