using CoinBot.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CoinBot.Infrastructure.Observability;

public sealed class DatabaseHealthCheck(ApplicationDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);

            return canConnect
                ? HealthCheckResult.Healthy("Database connection check succeeded.")
                : HealthCheckResult.Unhealthy("Database connection check failed.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Database connection check threw an exception.", exception);
        }
    }
}
