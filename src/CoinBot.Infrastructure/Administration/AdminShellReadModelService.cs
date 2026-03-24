using CoinBot.Application.Abstractions.Administration;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CoinBot.Infrastructure.Administration;

public sealed class AdminShellReadModelService(
    ApplicationDbContext dbContext,
    IMemoryCache memoryCache) : IAdminShellReadModelService
{
    private static readonly object CacheKey = new();

    public Task<AdminShellHealthSnapshot> GetHealthSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return memoryCache.GetOrCreateAsync(
                CacheKey,
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(3);

                    var systemState = await dbContext.GlobalSystemStates
                        .AsNoTracking()
                        .SingleOrDefaultAsync(entity => entity.Id == GlobalSystemStateDefaults.SingletonId, cancellationToken);
                    var executionSwitch = await dbContext.GlobalExecutionSwitches
                        .AsNoTracking()
                        .SingleOrDefaultAsync(
                            entity => entity.Id == GlobalExecutionSwitchDefaults.SingletonId &&
                                      !entity.IsDeleted,
                            cancellationToken);

                    return BuildSnapshot(systemState, executionSwitch);
                })!;
    }

    private static AdminShellHealthSnapshot BuildSnapshot(
        GlobalSystemState? systemState,
        GlobalExecutionSwitch? executionSwitch)
    {
        var effectiveSystemState = systemState?.State ?? GlobalSystemStateKind.Active;
        var environmentBadge = effectiveSystemState == GlobalSystemStateKind.Degraded
            ? "DEGRADED"
            : executionSwitch?.DemoModeEnabled != false
                ? "DEMO"
                : "LIVE";
        var lastUpdatedAtUtc = Max(executionSwitch?.UpdatedDate, systemState?.UpdatedAtUtc);

        return new AdminShellHealthSnapshot(
            environmentBadge,
            effectiveSystemState,
            systemState?.ReasonCode ?? GlobalSystemStateDefaults.DefaultReasonCode,
            systemState?.Message,
            lastUpdatedAtUtc,
            systemState?.IsManualOverride ?? false);
    }

    private static DateTime? Max(DateTime? left, DateTime? right)
    {
        if (!left.HasValue)
        {
            return right;
        }

        if (!right.HasValue)
        {
            return left;
        }

        return left.Value >= right.Value ? left : right;
    }
}
