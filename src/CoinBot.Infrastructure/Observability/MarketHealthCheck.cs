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
                "Market connectivity is not ready because no exchange validation state exists.",
                data: CreateData(exchangeAccounts: 0, latestValidatedAtUtc: null, validationFreshnessMinutes: options.Value.ValidationFreshnessMinutes));
        }

        var latestValidatedAtUtc = activeExchangeAccounts
            .Where(entity => entity.LastValidatedAt.HasValue)
            .Select(entity => entity.LastValidatedAt)
            .Max();

        if (latestValidatedAtUtc is null)
        {
            return HealthCheckResult.Unhealthy(
                "Market connectivity is not ready because exchange validation timestamps are missing.",
                data: CreateData(activeExchangeAccounts.Count, latestValidatedAtUtc, options.Value.ValidationFreshnessMinutes));
        }

        var freshnessThreshold = TimeSpan.FromMinutes(options.Value.ValidationFreshnessMinutes);
        var age = timeProvider.GetUtcNow().UtcDateTime - latestValidatedAtUtc.Value;

        return age <= freshnessThreshold
            ? HealthCheckResult.Healthy(
                "Market validation state is fresh enough for readiness.",
                CreateData(activeExchangeAccounts.Count, latestValidatedAtUtc, options.Value.ValidationFreshnessMinutes))
            : HealthCheckResult.Unhealthy(
                "Market validation state is stale.",
                data: CreateData(activeExchangeAccounts.Count, latestValidatedAtUtc, options.Value.ValidationFreshnessMinutes));
    }

    private static IReadOnlyDictionary<string, object> CreateData(
        int exchangeAccounts,
        DateTime? latestValidatedAtUtc,
        int validationFreshnessMinutes)
    {
        return new Dictionary<string, object>
        {
            ["exchangeAccounts"] = exchangeAccounts,
            ["latestValidatedAtUtc"] = latestValidatedAtUtc?.ToString("O") ?? "missing",
            ["validationFreshnessMinutes"] = validationFreshnessMinutes
        };
    }
}
