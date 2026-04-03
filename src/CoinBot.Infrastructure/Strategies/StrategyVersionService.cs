using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Strategies;

public sealed class StrategyVersionService(
    ApplicationDbContext dbContext,
    IStrategyRuleParser parser,
    TimeProvider timeProvider,
    IStrategyDefinitionValidator? validator = null,
    IStrategyTemplateCatalogService? templateCatalog = null) : IStrategyVersionService
{
    private readonly IStrategyDefinitionValidator validator = validator ?? new StrategyDefinitionValidator();
    private readonly IStrategyTemplateCatalogService templateCatalog = templateCatalog ?? new StrategyTemplateCatalogService(parser, validator ?? new StrategyDefinitionValidator());

    public async Task<StrategyVersionSnapshot> CreateDraftAsync(
        Guid strategyId,
        string definitionJson,
        CancellationToken cancellationToken = default)
    {
        var strategy = await dbContext.TradingStrategies
            .SingleOrDefaultAsync(entity => entity.Id == strategyId && !entity.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException($"Trading strategy '{strategyId}' was not found.");

        var parsedDefinition = ParseAndValidate(definitionJson);
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
            SchemaVersion = parsedDefinition.Document.SchemaVersion,
            VersionNumber = nextVersionNumber + 1,
            Status = StrategyVersionStatus.Draft,
            DefinitionJson = definitionJson.Trim()
        };

        dbContext.TradingStrategyVersions.Add(version);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToSnapshot(version, parsedDefinition.Document, parsedDefinition.Validation);
    }

    public async Task<StrategyVersionSnapshot> CreateDraftFromTemplateAsync(
        Guid strategyId,
        string templateKey,
        CancellationToken cancellationToken = default)
    {
        var template = await templateCatalog.GetAsync(templateKey, cancellationToken);
        return await CreateDraftAsync(strategyId, template.DefinitionJson, cancellationToken);
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

        _ = ParseAndValidate(version.DefinitionJson);

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

    private (StrategyRuleDocument Document, StrategyDefinitionValidationSnapshot Validation) ParseAndValidate(string definitionJson)
    {
        var document = parser.Parse(definitionJson);
        var validation = validator.Validate(document);
        if (!validation.IsValid)
        {
            throw new StrategyDefinitionValidationException(validation.StatusCode, validation.Summary, validation.FailureReasons);
        }

        return (document, validation);
    }

    private DateTime GetUtcNow()
    {
        return timeProvider.GetUtcNow().UtcDateTime;
    }

    private StrategyVersionSnapshot ToSnapshot(TradingStrategyVersion version)
    {
        try
        {
            var parsedDefinition = ParseAndValidate(version.DefinitionJson);
            return ToSnapshot(version, parsedDefinition.Document, parsedDefinition.Validation);
        }
        catch (StrategyDefinitionValidationException exception)
        {
            return new StrategyVersionSnapshot(
                version.Id,
                version.TradingStrategyId,
                version.SchemaVersion,
                version.VersionNumber,
                version.Status,
                version.PublishedAtUtc,
                version.ArchivedAtUtc,
                TemplateKey: null,
                TemplateName: null,
                ValidationStatusCode: exception.StatusCode,
                ValidationSummary: exception.Message);
        }
        catch (StrategyRuleParseException exception)
        {
            return new StrategyVersionSnapshot(
                version.Id,
                version.TradingStrategyId,
                version.SchemaVersion,
                version.VersionNumber,
                version.Status,
                version.PublishedAtUtc,
                version.ArchivedAtUtc,
                TemplateKey: null,
                TemplateName: null,
                ValidationStatusCode: "ParseFailed",
                ValidationSummary: exception.Message);
        }
    }

    private static StrategyVersionSnapshot ToSnapshot(
        TradingStrategyVersion version,
        StrategyRuleDocument document,
        StrategyDefinitionValidationSnapshot validation)
    {
        return new StrategyVersionSnapshot(
            version.Id,
            version.TradingStrategyId,
            document.SchemaVersion,
            version.VersionNumber,
            version.Status,
            version.PublishedAtUtc,
            version.ArchivedAtUtc,
            document.Metadata?.TemplateKey,
            document.Metadata?.TemplateName,
            validation.StatusCode,
            validation.Summary);
    }
}
