using CoinBot.Application.Abstractions.Auditing;
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
    IStrategyTemplateCatalogService? templateCatalog = null,
    IAuditLogService? auditLogService = null) : IStrategyVersionService
{
    private readonly IStrategyDefinitionValidator validator = validator ?? new StrategyDefinitionValidator();
    private readonly IStrategyTemplateCatalogService templateCatalog = templateCatalog ?? new StrategyTemplateCatalogService(
        parser,
        validator ?? new StrategyDefinitionValidator(),
        dbContext,
        auditLogService);
    private readonly IAuditLogService? auditLogService = auditLogService;

    public async Task<StrategyVersionSnapshot> CreateDraftAsync(
        Guid strategyId,
        string definitionJson,
        CancellationToken cancellationToken = default)
    {
        var strategy = await GetStrategyAsync(strategyId, cancellationToken);
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
            DefinitionJson = NormalizeDefinitionJson(definitionJson)
        };

        dbContext.TradingStrategyVersions.Add(version);
        await dbContext.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            strategy.OwnerUserId,
            "Strategy.Version.DraftCreated",
            strategy.StrategyKey,
            $"StrategyKey={strategy.StrategyKey}; Version={version.VersionNumber}; Template={parsedDefinition.Document.Metadata?.TemplateKey ?? "custom"}; TemplateRevision={(parsedDefinition.Document.Metadata?.TemplateRevisionNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a")}; Validation={parsedDefinition.Validation.StatusCode}",
            cancellationToken);

        return ToSnapshot(strategy, version, parsedDefinition.Document, parsedDefinition.Validation);
    }

    public async Task<StrategyVersionSnapshot> CreateDraftFromTemplateAsync(
        Guid strategyId,
        string templateKey,
        CancellationToken cancellationToken = default)
    {
        var template = await templateCatalog.GetAsync(templateKey, cancellationToken);
        return await CreateDraftAsync(strategyId, template.DefinitionJson, cancellationToken);
    }

    public async Task<StrategyVersionSnapshot> CreateDraftFromVersionAsync(
        Guid strategyVersionId,
        CancellationToken cancellationToken = default)
    {
        var version = await GetVersionAsync(strategyVersionId, cancellationToken);
        return await CreateDraftAsync(version.TradingStrategyId, version.DefinitionJson, cancellationToken);
    }

    public async Task<StrategyVersionSnapshot> PublishAsync(
        Guid strategyVersionId,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteLifecycleMutationAsync(
            async ct =>
            {
                var version = await GetVersionAsync(strategyVersionId, ct);
                var strategy = await GetStrategyAsync(version.TradingStrategyId, ct);

                if (version.Status == StrategyVersionStatus.Published)
                {
                    return ToSnapshot(strategy, version);
                }

                if (version.Status != StrategyVersionStatus.Draft)
                {
                    throw new InvalidOperationException("Only draft strategy versions can be published.");
                }

                var parsedDefinition = ParseAndValidate(version.DefinitionJson);
                var utcNow = GetUtcNow();

                version.Status = StrategyVersionStatus.Published;
                version.PublishedAtUtc = utcNow;
                version.ArchivedAtUtc = null;
                strategy.UsesExplicitVersionLifecycle = true;
                strategy.ActiveTradingStrategyVersionId = version.Id;
                strategy.ActiveVersionActivatedAtUtc = utcNow;

                await dbContext.SaveChangesAsync(ct);
                await WriteAuditAsync(
                    strategy.OwnerUserId,
                    "Strategy.Version.Published",
                    strategy.StrategyKey,
                    $"StrategyKey={strategy.StrategyKey}; Version={version.VersionNumber}; Template={parsedDefinition.Document.Metadata?.TemplateKey ?? "custom"}; TemplateRevision={(parsedDefinition.Document.Metadata?.TemplateRevisionNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a")}; Validation={parsedDefinition.Validation.StatusCode}",
                    ct);
                await WriteAuditAsync(
                    strategy.OwnerUserId,
                    "Strategy.Version.Activated",
                    strategy.StrategyKey,
                    $"StrategyKey={strategy.StrategyKey}; Version={version.VersionNumber}; Source=Publish; ActivationToken={ResolveActivationToken(strategy) ?? "n/a"}",
                    ct);

                return ToSnapshot(strategy, version, parsedDefinition.Document, parsedDefinition.Validation);
            },
            cancellationToken);
    }

    public async Task<StrategyVersionSnapshot> ActivateAsync(
        Guid strategyVersionId,
        string? expectedActivationToken = null,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteLifecycleMutationAsync(
            async ct =>
            {
                var version = await GetVersionAsync(strategyVersionId, ct);
                var strategy = await GetStrategyAsync(version.TradingStrategyId, ct);

                if (version.Status != StrategyVersionStatus.Published)
                {
                    throw new InvalidOperationException("Only published strategy versions can be activated.");
                }

                var parsedDefinition = ParseAndValidate(version.DefinitionJson);
                EnsureActivationTokenMatches(strategy, expectedActivationToken);
                if (strategy.UsesExplicitVersionLifecycle && strategy.ActiveTradingStrategyVersionId == version.Id)
                {
                    return ToSnapshot(strategy, version, parsedDefinition.Document, parsedDefinition.Validation);
                }

                var previousActiveVersionId = strategy.ActiveTradingStrategyVersionId;
                strategy.UsesExplicitVersionLifecycle = true;
                strategy.ActiveTradingStrategyVersionId = version.Id;
                strategy.ActiveVersionActivatedAtUtc = GetUtcNow();

                await dbContext.SaveChangesAsync(ct);
                await WriteAuditAsync(
                    strategy.OwnerUserId,
                    "Strategy.Version.Activated",
                    strategy.StrategyKey,
                    $"StrategyKey={strategy.StrategyKey}; Version={version.VersionNumber}; PreviousActiveVersionId={(previousActiveVersionId?.ToString("N") ?? "none")}; ActivationToken={ResolveActivationToken(strategy) ?? "n/a"}",
                    ct);

                return ToSnapshot(strategy, version, parsedDefinition.Document, parsedDefinition.Validation);
            },
            cancellationToken);
    }

    public async Task<StrategyVersionSnapshot?> DeactivateAsync(
        Guid strategyId,
        string? expectedActivationToken = null,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteLifecycleMutationAsync(
            async ct =>
            {
                var strategy = await GetStrategyAsync(strategyId, ct);
                EnsureActivationTokenMatches(strategy, expectedActivationToken);
                var runtimeVersion = await StrategyRuntimeVersionSelection.ResolveAsync(dbContext, strategy.Id, ct);
                if (runtimeVersion is null)
                {
                    return null;
                }

                strategy.UsesExplicitVersionLifecycle = true;
                strategy.ActiveTradingStrategyVersionId = null;
                strategy.ActiveVersionActivatedAtUtc = null;

                await dbContext.SaveChangesAsync(ct);
                await WriteAuditAsync(
                    strategy.OwnerUserId,
                    "Strategy.Version.Deactivated",
                    strategy.StrategyKey,
                    $"StrategyKey={strategy.StrategyKey}; Version={runtimeVersion.VersionNumber}; PreviousActiveVersionId={runtimeVersion.Id:N}; ActivationToken={ResolveActivationToken(strategy) ?? "n/a"}",
                    ct);

                return ToSnapshot(strategy, runtimeVersion);
            },
            cancellationToken);
    }

    public async Task<StrategyVersionSnapshot> ArchiveAsync(
        Guid strategyVersionId,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteLifecycleMutationAsync(
            async ct =>
            {
                var version = await GetVersionAsync(strategyVersionId, ct);
                var strategy = await GetStrategyAsync(version.TradingStrategyId, ct);

                if (version.Status == StrategyVersionStatus.Archived)
                {
                    return ToSnapshot(strategy, version);
                }

                var wasActive = strategy.ActiveTradingStrategyVersionId == version.Id;
                version.Status = StrategyVersionStatus.Archived;
                version.ArchivedAtUtc = GetUtcNow();

                if (wasActive)
                {
                    strategy.UsesExplicitVersionLifecycle = true;
                    strategy.ActiveTradingStrategyVersionId = null;
                    strategy.ActiveVersionActivatedAtUtc = null;
                }

                await dbContext.SaveChangesAsync(ct);
                if (wasActive)
                {
                    await WriteAuditAsync(
                        strategy.OwnerUserId,
                        "Strategy.Version.Deactivated",
                        strategy.StrategyKey,
                        $"StrategyKey={strategy.StrategyKey}; Version={version.VersionNumber}; Source=Archive; ActivationToken={ResolveActivationToken(strategy) ?? "n/a"}",
                        ct);
                }

                await WriteAuditAsync(
                    strategy.OwnerUserId,
                    "Strategy.Version.Archived",
                    strategy.StrategyKey,
                    $"StrategyKey={strategy.StrategyKey}; Version={version.VersionNumber}; Status=Archived",
                    ct);

                return ToSnapshot(strategy, version);
            },
            cancellationToken);
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

    private async Task<T> ExecuteLifecycleMutationAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            try
            {
                return await action(cancellationToken);
            }
            catch (DbUpdateConcurrencyException exception)
            {
                throw CreateStaleLifecycleException(exception);
            }
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await action(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (DbUpdateConcurrencyException exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw CreateStaleLifecycleException(exception);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<TradingStrategy> GetStrategyAsync(Guid strategyId, CancellationToken cancellationToken)
    {
        return await dbContext.TradingStrategies
            .SingleOrDefaultAsync(entity => entity.Id == strategyId && !entity.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException($"Trading strategy '{strategyId}' was not found.");
    }

    private async Task<TradingStrategyVersion> GetVersionAsync(Guid strategyVersionId, CancellationToken cancellationToken)
    {
        return await dbContext.TradingStrategyVersions
            .SingleOrDefaultAsync(entity => entity.Id == strategyVersionId && !entity.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException($"Trading strategy version '{strategyVersionId}' was not found.");
    }

    private DateTime GetUtcNow()
    {
        return timeProvider.GetUtcNow().UtcDateTime;
    }

    private async Task WriteAuditAsync(
        string actorUserId,
        string action,
        string target,
        string? context,
        CancellationToken cancellationToken)
    {
        if (auditLogService is null)
        {
            return;
        }

        await auditLogService.WriteAsync(
            new AuditLogWriteRequest(
                actorUserId,
                action,
                target,
                Truncate(context, 2048),
                CorrelationId: null,
                Outcome: "Success",
                Environment: "Strategy"),
            cancellationToken);
    }

    private StrategyVersionSnapshot ToSnapshot(TradingStrategy strategy, TradingStrategyVersion version)
    {
        try
        {
            var parsedDefinition = ParseAndValidate(version.DefinitionJson);
            return ToSnapshot(strategy, version, parsedDefinition.Document, parsedDefinition.Validation);
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
                ValidationSummary: exception.Message,
                IsActive: IsActive(strategy, version.Id),
                IsImmutable: version.Status != StrategyVersionStatus.Draft,
                ActivatedAtUtc: ResolveActivatedAtUtc(strategy, version.Id),
                TemplateRevisionNumber: null,
                TemplateSource: null,
                ActivationToken: ResolveActivationToken(strategy));
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
                ValidationSummary: exception.Message,
                IsActive: IsActive(strategy, version.Id),
                IsImmutable: version.Status != StrategyVersionStatus.Draft,
                ActivatedAtUtc: ResolveActivatedAtUtc(strategy, version.Id),
                TemplateRevisionNumber: null,
                TemplateSource: null,
                ActivationToken: ResolveActivationToken(strategy));
        }
    }

    private StrategyVersionSnapshot ToSnapshot(
        TradingStrategy strategy,
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
            validation.Summary,
            IsActive: IsActive(strategy, version.Id),
            IsImmutable: version.Status != StrategyVersionStatus.Draft,
            ActivatedAtUtc: ResolveActivatedAtUtc(strategy, version.Id),
            TemplateRevisionNumber: document.Metadata?.TemplateRevisionNumber,
            TemplateSource: document.Metadata?.TemplateSource,
            ActivationToken: ResolveActivationToken(strategy));
    }

    private static bool IsActive(TradingStrategy strategy, Guid strategyVersionId)
    {
        return strategy.UsesExplicitVersionLifecycle &&
               strategy.ActiveTradingStrategyVersionId == strategyVersionId;
    }

    private static DateTime? ResolveActivatedAtUtc(TradingStrategy strategy, Guid strategyVersionId)
    {
        return IsActive(strategy, strategyVersionId)
            ? strategy.ActiveVersionActivatedAtUtc
            : null;
    }

    private static string? ResolveActivationToken(TradingStrategy strategy)
    {
        return strategy.ActivationConcurrencyToken.Length == 0
            ? null
            : Convert.ToBase64String(strategy.ActivationConcurrencyToken);
    }

    private static void EnsureActivationTokenMatches(TradingStrategy strategy, string? expectedActivationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedActivationToken))
        {
            return;
        }

        var actualToken = ResolveActivationToken(strategy);
        if (!string.Equals(actualToken, expectedActivationToken.Trim(), StringComparison.Ordinal))
        {
            throw CreateStaleLifecycleException();
        }
    }

    private static InvalidOperationException CreateStaleLifecycleException(Exception? innerException = null)
    {
        return new InvalidOperationException(
            "Strategy lifecycle request became stale. Refresh lifecycle state and retry with the latest activation token.",
            innerException);
    }

    private static string NormalizeDefinitionJson(string definitionJson)
    {
        return string.IsNullOrWhiteSpace(definitionJson)
            ? throw new ArgumentException("The value is required.", nameof(definitionJson))
            : definitionJson.Trim();
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }
}
