using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Strategies;

public sealed class StrategyVersionService(
    ApplicationDbContext dbContext,
    IStrategyRuleParser parser,
    TimeProvider timeProvider) : IStrategyVersionService
{
    public async Task<StrategyVersionSnapshot> CreateDraftAsync(
        Guid strategyId,
        string definitionJson,
        CancellationToken cancellationToken = default)
    {
        var strategy = await dbContext.TradingStrategies
            .SingleOrDefaultAsync(entity => entity.Id == strategyId && !entity.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException($"Trading strategy '{strategyId}' was not found.");

        var parsedDocument = parser.Parse(definitionJson);
        var utcNow = GetUtcNow();
        var existingDrafts = await dbContext.TradingStrategyVersions
            .Where(entity => entity.TradingStrategyId == strategy.Id &&
                             entity.Status == StrategyVersionStatus.Draft &&
                             !entity.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var existingDraft in existingDrafts)
        {
            existingDraft.Status = StrategyVersionStatus.Archived;
            existingDraft.ArchivedAtUtc = utcNow;
        }

        var nextVersionNumber = await dbContext.TradingStrategyVersions
            .Where(entity => entity.TradingStrategyId == strategy.Id && !entity.IsDeleted)
            .Select(entity => (int?)entity.VersionNumber)
            .MaxAsync(cancellationToken) ?? 0;

        var version = new TradingStrategyVersion
        {
            OwnerUserId = strategy.OwnerUserId,
            TradingStrategyId = strategy.Id,
            SchemaVersion = parsedDocument.SchemaVersion,
            VersionNumber = nextVersionNumber + 1,
            Status = StrategyVersionStatus.Draft,
            DefinitionJson = definitionJson.Trim()
        };

        dbContext.TradingStrategyVersions.Add(version);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToSnapshot(version);
    }

    public async Task<StrategyVersionSnapshot> PublishAsync(
        Guid strategyVersionId,
        CancellationToken cancellationToken = default)
    {
        var version = await dbContext.TradingStrategyVersions
            .SingleOrDefaultAsync(entity => entity.Id == strategyVersionId && !entity.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException($"Trading strategy version '{strategyVersionId}' was not found.");

        if (version.Status == StrategyVersionStatus.Published)
        {
            return ToSnapshot(version);
        }

        if (version.Status != StrategyVersionStatus.Draft)
        {
            throw new InvalidOperationException("Only draft strategy versions can be published.");
        }

        parser.Parse(version.DefinitionJson);

        var utcNow = GetUtcNow();
        var publishedVersions = await dbContext.TradingStrategyVersions
            .Where(entity => entity.TradingStrategyId == version.TradingStrategyId &&
                             entity.Id != version.Id &&
                             entity.Status == StrategyVersionStatus.Published &&
                             !entity.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var publishedVersion in publishedVersions)
        {
            publishedVersion.Status = StrategyVersionStatus.Archived;
            publishedVersion.ArchivedAtUtc = utcNow;
        }

        version.Status = StrategyVersionStatus.Published;
        version.PublishedAtUtc = utcNow;
        version.ArchivedAtUtc = null;

        await dbContext.SaveChangesAsync(cancellationToken);

        return ToSnapshot(version);
    }

    public async Task<StrategyVersionSnapshot> ArchiveAsync(
        Guid strategyVersionId,
        CancellationToken cancellationToken = default)
    {
        var version = await dbContext.TradingStrategyVersions
            .SingleOrDefaultAsync(entity => entity.Id == strategyVersionId && !entity.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException($"Trading strategy version '{strategyVersionId}' was not found.");

        if (version.Status == StrategyVersionStatus.Archived)
        {
            return ToSnapshot(version);
        }

        version.Status = StrategyVersionStatus.Archived;
        version.ArchivedAtUtc = GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return ToSnapshot(version);
    }

    private DateTime GetUtcNow()
    {
        return timeProvider.GetUtcNow().UtcDateTime;
    }

    private static StrategyVersionSnapshot ToSnapshot(TradingStrategyVersion version)
    {
        return new StrategyVersionSnapshot(
            version.Id,
            version.TradingStrategyId,
            version.SchemaVersion,
            version.VersionNumber,
            version.Status,
            version.PublishedAtUtc,
            version.ArchivedAtUtc);
    }
}
