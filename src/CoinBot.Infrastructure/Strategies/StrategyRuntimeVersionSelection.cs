using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Strategies;

internal static class StrategyRuntimeVersionSelection
{
    public static async Task<TradingStrategyVersion?> ResolveAsync(
        ApplicationDbContext dbContext,
        Guid tradingStrategyId,
        CancellationToken cancellationToken = default)
    {
        var strategy = await dbContext.TradingStrategies
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => entity.Id == tradingStrategyId && !entity.IsDeleted)
            .Select(entity => new
            {
                entity.ActiveTradingStrategyVersionId,
                entity.UsesExplicitVersionLifecycle
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (strategy is null)
        {
            return null;
        }

        var publishedVersions = dbContext.TradingStrategyVersions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.TradingStrategyId == tradingStrategyId &&
                entity.Status == StrategyVersionStatus.Published &&
                !entity.IsDeleted);

        if (strategy.UsesExplicitVersionLifecycle)
        {
            return strategy.ActiveTradingStrategyVersionId.HasValue
                ? await publishedVersions.SingleOrDefaultAsync(
                    entity => entity.Id == strategy.ActiveTradingStrategyVersionId.Value,
                    cancellationToken)
                : null;
        }

        return await publishedVersions
            .OrderByDescending(entity => entity.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
