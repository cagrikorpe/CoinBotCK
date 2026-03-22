using CoinBot.Application.Abstractions.Execution;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CoinBot.Infrastructure.Observability;

public sealed class DemoEngineHealthCheck(
    IGlobalExecutionSwitchService globalExecutionSwitchService) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await globalExecutionSwitchService.GetSnapshotAsync(cancellationToken);
        var data = new Dictionary<string, object>
        {
            ["isPersisted"] = snapshot.IsPersisted,
            ["tradeMasterArmed"] = snapshot.IsTradeMasterArmed,
            ["demoModeEnabled"] = snapshot.DemoModeEnabled,
            ["effectiveEnvironment"] = snapshot.EffectiveEnvironment.ToString(),
            ["liveModeApprovedAtUtc"] = snapshot.LiveModeApprovedAtUtc?.ToString("O") ?? "missing"
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

        return HealthCheckResult.Healthy("Demo engine route is open.", data);
    }
}
