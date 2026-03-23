using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CoinBot.Infrastructure.Observability;

public sealed class DemoEngineHealthCheck(
    IGlobalExecutionSwitchService globalExecutionSwitchService,
    ApplicationDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await globalExecutionSwitchService.GetSnapshotAsync(cancellationToken);
        var activeDriftedSessionCount = await dbContext.DemoSessions
            .IgnoreQueryFilters()
            .CountAsync(
                entity => !entity.IsDeleted &&
                          entity.State == DemoSessionState.Active &&
                          entity.ConsistencyStatus == DemoConsistencyStatus.DriftDetected,
                cancellationToken);
        var data = new Dictionary<string, object>
        {
            ["isPersisted"] = snapshot.IsPersisted,
            ["tradeMasterArmed"] = snapshot.IsTradeMasterArmed,
            ["demoModeEnabled"] = snapshot.DemoModeEnabled,
            ["effectiveEnvironment"] = snapshot.EffectiveEnvironment.ToString(),
            ["liveModeApprovedAtUtc"] = snapshot.LiveModeApprovedAtUtc?.ToString("O") ?? "missing",
            ["activeDriftedSessionCount"] = activeDriftedSessionCount
        };

        if (!snapshot.IsPersisted)
        {
            return HealthCheckResult.Unhealthy(
                "Demo engine is fail-closed because the global execution switch configuration is missing.",
                data: data);
        }

        if (!snapshot.IsTradeMasterArmed)
        {
            return HealthCheckResult.Unhealthy(
                "Demo engine is fail-closed because TradeMaster is disarmed.",
                data: data);
        }

        if (!snapshot.DemoModeEnabled)
        {
            return HealthCheckResult.Unhealthy(
                "Demo engine is closed because the global default mode is Live.",
                data: data);
        }

        if (activeDriftedSessionCount > 0)
        {
            return HealthCheckResult.Unhealthy(
                "Demo engine is closed because one or more active demo sessions drifted.",
                data: data);
        }

        return HealthCheckResult.Healthy("Demo engine route is open.", data);
    }
}
