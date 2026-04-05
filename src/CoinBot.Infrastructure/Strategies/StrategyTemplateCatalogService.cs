using System.Text.Json;
using System.Text.Json.Nodes;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Strategies;

public sealed class StrategyTemplateCatalogService(
    IStrategyRuleParser parser,
    IStrategyDefinitionValidator validator,
    ApplicationDbContext? dbContext = null,
    IAuditLogService? auditLogService = null) : IStrategyTemplateCatalogService
{
    private readonly ApplicationDbContext? dbContext = dbContext;
    private readonly IAuditLogService? auditLogService = auditLogService;

    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly IReadOnlyCollection<(string Key, string Name, string Description, string Category, string DefinitionJson)> BuiltInTemplates =
    [
        (
            "rsi-reversal",
            "RSI Reversal",
            "RSI oversold entry with deterministic sample-count risk guard.",
            "Reversal",
            """
            {
              "schemaVersion": 2,
              "metadata": {
                "templateKey": "rsi-reversal",
                "templateName": "RSI Reversal"
              },
              "entry": {
                "operator": "all",
                "ruleId": "entry-root",
                "ruleType": "group",
                "timeframe": "1m",
                "weight": 1,
                "enabled": true,
                "group": "entry",
                "rules": [
                  {
                    "ruleId": "entry-mode-live",
                    "ruleType": "context",
                    "path": "context.mode",
                    "comparison": "equals",
                    "value": "Live",
                    "timeframe": "1m",
                    "weight": 5,
                    "enabled": true,
                    "group": "entry"
                  },
                  {
                    "ruleId": "entry-rsi-oversold",
                    "ruleType": "rsi",
                    "path": "indicator.rsi.value",
                    "comparison": "lessThanOrEqual",
                    "value": 35,
                    "timeframe": "1m",
                    "weight": 70,
                    "enabled": true,
                    "group": "entry"
                  }
                ]
              },
              "risk": {
                "operator": "all",
                "ruleId": "risk-root",
                "ruleType": "group",
                "timeframe": "1m",
                "weight": 1,
                "enabled": true,
                "group": "risk",
                "rules": [
                  {
                    "ruleId": "risk-rsi-ready",
                    "ruleType": "rsi",
                    "path": "indicator.rsi.isReady",
                    "comparison": "equals",
                    "value": true,
                    "timeframe": "1m",
                    "weight": 15,
                    "enabled": true,
                    "group": "risk"
                  },
                  {
                    "ruleId": "risk-sample-count",
                    "ruleType": "data-quality",
                    "path": "indicator.sampleCount",
                    "comparison": "greaterThanOrEqual",
                    "value": 34,
                    "timeframe": "1m",
                    "weight": 10,
                    "enabled": true,
                    "group": "risk"
                  }
                ]
              }
            }
            """),
        (
            "macd-trend",
            "MACD Trend",
            "Trend-following entry when MACD line is above signal line and indicator is ready.",
            "Momentum",
            """
            {
              "schemaVersion": 2,
              "metadata": {
                "templateKey": "macd-trend",
                "templateName": "MACD Trend"
              },
              "entry": {
                "operator": "all",
                "ruleId": "entry-root",
                "ruleType": "group",
                "timeframe": "1m",
                "weight": 1,
                "enabled": true,
                "group": "entry",
                "rules": [
                  {
                    "ruleId": "entry-macd-ready",
                    "ruleType": "macd",
                    "path": "indicator.macd.isReady",
                    "comparison": "equals",
                    "value": true,
                    "timeframe": "1m",
                    "weight": 20,
                    "enabled": true,
                    "group": "entry"
                  },
                  {
                    "ruleId": "entry-macd-cross",
                    "ruleType": "macd",
                    "path": "indicator.macd.macdLine",
                    "comparison": "greaterThan",
                    "valuePath": "indicator.macd.signalLine",
                    "timeframe": "1m",
                    "weight": 80,
                    "enabled": true,
                    "group": "entry"
                  }
                ]
              },
              "risk": {
                "path": "indicator.sampleCount",
                "comparison": "greaterThanOrEqual",
                "value": 34,
                "ruleId": "risk-sample-count",
                "ruleType": "data-quality",
                "timeframe": "1m",
                "weight": 20,
                "enabled": true,
                "group": "risk"
              }
            }
            """),
        (
            "bollinger-mean-reversion",
            "Bollinger Mean Reversion",
            "Lower-band touch entry with ready-state and sample-count risk checks.",
            "MeanReversion",
            """
            {
              "schemaVersion": 2,
              "metadata": {
                "templateKey": "bollinger-mean-reversion",
                "templateName": "Bollinger Mean Reversion"
              },
              "entry": {
                "operator": "all",
                "ruleId": "entry-root",
                "ruleType": "group",
                "timeframe": "1m",
                "weight": 1,
                "enabled": true,
                "group": "entry",
                "rules": [
                  {
                    "ruleId": "entry-bollinger-ready",
                    "ruleType": "bollinger",
                    "path": "indicator.bollinger.isReady",
                    "comparison": "equals",
                    "value": true,
                    "timeframe": "1m",
                    "weight": 20,
                    "enabled": true,
                    "group": "entry"
                  },
                  {
                    "ruleId": "entry-close-below-middle",
                    "ruleType": "bollinger",
                    "path": "indicator.bollinger.lowerBand",
                    "comparison": "lessThanOrEqual",
                    "valuePath": "indicator.bollinger.middleBand",
                    "timeframe": "1m",
                    "weight": 80,
                    "enabled": true,
                    "group": "entry"
                  }
                ]
              },
              "risk": {
                "operator": "all",
                "ruleId": "risk-root",
                "ruleType": "group",
                "timeframe": "1m",
                "weight": 1,
                "enabled": true,
                "group": "risk",
                "rules": [
                  {
                    "ruleId": "risk-state-ready",
                    "ruleType": "data-quality",
                    "path": "indicator.state",
                    "comparison": "equals",
                    "value": "Ready",
                    "timeframe": "1m",
                    "weight": 10,
                    "enabled": true,
                    "group": "risk"
                  },
                  {
                    "ruleId": "risk-sample-count",
                    "ruleType": "data-quality",
                    "path": "indicator.sampleCount",
                    "comparison": "greaterThanOrEqual",
                    "value": 34,
                    "timeframe": "1m",
                    "weight": 10,
                    "enabled": true,
                    "group": "risk"
                  }
                ]
              }
            }
            """)
    ];

    public async Task<IReadOnlyCollection<StrategyTemplateSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var builtInSnapshots = BuiltInTemplates
            .Select(BuildBuiltInSnapshot)
            .ToArray();

        if (dbContext is null)
        {
            return builtInSnapshots;
        }

        var customTemplates = await dbContext.TradingStrategyTemplates
            .AsNoTracking()
            .Where(entity => entity.IsActive && !entity.IsDeleted)
            .OrderBy(entity => entity.TemplateName)
            .ThenBy(entity => entity.TemplateKey)
            .ToListAsync(cancellationToken);
        var revisions = await LoadRevisionsAsync(customTemplates.Select(entity => entity.Id).ToArray(), cancellationToken);

        return builtInSnapshots
            .Concat(customTemplates.Select(template => BuildCustomSnapshot(template, revisions)))
            .OrderBy(template => template.TemplateName, StringComparer.Ordinal)
            .ThenBy(template => template.TemplateKey, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<StrategyTemplateSnapshot> GetAsync(string templateKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedTemplateKey = NormalizeTemplateKey(templateKey, nameof(templateKey));
        var builtIn = TryGetBuiltInTemplate(normalizedTemplateKey);
        if (builtIn.Key is not null)
        {
            return BuildBuiltInSnapshot(builtIn);
        }

        if (dbContext is null)
        {
            throw new InvalidOperationException(
                $"Strategy template '{normalizedTemplateKey}' was not found in the built-in catalog, and template persistence is not available in the current service scope.");
        }

        var template = await dbContext!.TradingStrategyTemplates
            .AsNoTracking()
            .SingleOrDefaultAsync(
                entity => entity.TemplateKey == normalizedTemplateKey &&
                          !entity.IsDeleted,
                cancellationToken);

        if (template is null || !template.IsActive)
        {
            throw new InvalidOperationException($"Strategy template '{normalizedTemplateKey}' was not found.");
        }

        var revisions = await LoadRevisionsAsync([template.Id], cancellationToken);
        return BuildCustomSnapshot(template, revisions);
    }

    public async Task<StrategyTemplateSnapshot> CreateCustomAsync(
        string ownerUserId,
        string templateKey,
        string templateName,
        string description,
        string category,
        string definitionJson,
        CancellationToken cancellationToken = default)
    {
        return await CreateCustomInternalAsync(
            ownerUserId,
            templateKey,
            templateName,
            description,
            category,
            definitionJson,
            sourceTemplateKey: null,
            sourceRevisionNumber: null,
            cancellationToken);
    }

    public async Task<StrategyTemplateSnapshot> CloneAsync(
        string ownerUserId,
        string sourceTemplateKey,
        string templateKey,
        string templateName,
        string description,
        string category,
        CancellationToken cancellationToken = default)
    {
        var source = await GetAsync(sourceTemplateKey, cancellationToken);

        return await CreateCustomInternalAsync(
            ownerUserId,
            templateKey,
            templateName,
            description,
            category,
            source.DefinitionJson,
            source.TemplateKey,
            source.ActiveRevisionNumber,
            cancellationToken);
    }

    public async Task<StrategyTemplateSnapshot> ReviseAsync(
        string templateKey,
        string templateName,
        string description,
        string category,
        string definitionJson,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePersistenceAvailable();

        var normalizedTemplateKey = NormalizeTemplateKey(templateKey, nameof(templateKey));
        if (TryGetBuiltInTemplate(normalizedTemplateKey).Key is not null)
        {
            throw new InvalidOperationException("Built-in strategy templates cannot be revised.");
        }

        var template = await dbContext!.TradingStrategyTemplates
            .SingleOrDefaultAsync(
                entity => entity.TemplateKey == normalizedTemplateKey &&
                          !entity.IsDeleted,
                cancellationToken)
            ?? throw new InvalidOperationException($"Strategy template '{normalizedTemplateKey}' was not found.");

        if (!template.IsActive)
        {
            throw new InvalidOperationException("Archived strategy templates cannot be revised.");
        }

        var revisions = await dbContext.TradingStrategyTemplateRevisions
            .Where(entity => entity.TradingStrategyTemplateId == template.Id && !entity.IsDeleted)
            .OrderBy(entity => entity.RevisionNumber)
            .ToListAsync(cancellationToken);
        var previousActiveRevision = revisions.SingleOrDefault(entity => entity.Id == template.ActiveTradingStrategyTemplateRevisionId)
            ?? revisions.OrderByDescending(entity => entity.RevisionNumber).FirstOrDefault();
        var nextRevisionNumber = revisions.Select(entity => entity.RevisionNumber).DefaultIfEmpty(0).Max() + 1;
        var normalizedTemplateName = NormalizeRequired(templateName, 128, nameof(templateName));
        var normalizedDescription = NormalizeRequired(description, 512, nameof(description));
        var normalizedCategory = NormalizeRequired(category, 64, nameof(category));
        var normalizedDefinition = NormalizeDefinitionJson(definitionJson, normalizedTemplateKey, normalizedTemplateName, nextRevisionNumber, "Custom");
        var document = parser.Parse(normalizedDefinition);
        var validation = validator.Validate(document);
        if (!validation.IsValid)
        {
            throw new StrategyDefinitionValidationException(validation.StatusCode, validation.Summary, validation.FailureReasons);
        }

        var revision = new TradingStrategyTemplateRevision
        {
            Id = Guid.NewGuid(),
            OwnerUserId = template.OwnerUserId,
            TradingStrategyTemplateId = template.Id,
            RevisionNumber = nextRevisionNumber,
            SchemaVersion = document.SchemaVersion,
            DefinitionJson = normalizedDefinition,
            ValidationStatusCode = validation.StatusCode,
            ValidationSummary = validation.Summary,
            SourceTemplateKey = template.TemplateKey,
            SourceRevisionNumber = previousActiveRevision?.RevisionNumber
        };

        template.TemplateName = normalizedTemplateName;
        template.Description = normalizedDescription;
        template.Category = normalizedCategory;
        template.SchemaVersion = document.SchemaVersion;
        template.DefinitionJson = normalizedDefinition;
        template.ActiveTradingStrategyTemplateRevisionId = revision.Id;
        template.LatestTradingStrategyTemplateRevisionId = revision.Id;
        template.ArchivedAtUtc = null;

        dbContext.TradingStrategyTemplateRevisions.Add(revision);
        await dbContext.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            template.OwnerUserId,
            "Strategy.Template.Revised",
            normalizedTemplateKey,
            $"TemplateKey={normalizedTemplateKey}; Revision={nextRevisionNumber}; PreviousRevision={(previousActiveRevision?.RevisionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none")}; Validation={validation.StatusCode}",
            cancellationToken);

        revisions.Add(revision);
        return BuildCustomSnapshot(template, revisions);
    }

    public async Task<IReadOnlyCollection<StrategyTemplateRevisionSnapshot>> ListRevisionsAsync(
        string templateKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedTemplateKey = NormalizeTemplateKey(templateKey, nameof(templateKey));
        var builtIn = TryGetBuiltInTemplate(normalizedTemplateKey);
        if (builtIn.Key is not null)
        {
            var builtInSnapshot = BuildBuiltInSnapshot(builtIn);
            return
            [
                new StrategyTemplateRevisionSnapshot(
                    Guid.Empty,
                    null,
                    builtInSnapshot.TemplateKey,
                    builtInSnapshot.ActiveRevisionNumber,
                    builtInSnapshot.SchemaVersion,
                    builtInSnapshot.Validation.StatusCode,
                    builtInSnapshot.Validation.Summary,
                    IsActive: true,
                    IsLatest: true,
                    IsArchived: false,
                    SourceTemplateKey: null,
                    SourceRevisionNumber: null,
                    ArchivedAtUtc: null)
            ];
        }

        EnsurePersistenceAvailable();
        var template = await dbContext!.TradingStrategyTemplates
            .AsNoTracking()
            .SingleOrDefaultAsync(
                entity => entity.TemplateKey == normalizedTemplateKey &&
                          !entity.IsDeleted,
                cancellationToken)
            ?? throw new InvalidOperationException($"Strategy template '{normalizedTemplateKey}' was not found.");
        var revisions = await dbContext.TradingStrategyTemplateRevisions
            .AsNoTracking()
            .Where(entity => entity.TradingStrategyTemplateId == template.Id && !entity.IsDeleted)
            .OrderByDescending(entity => entity.RevisionNumber)
            .ToListAsync(cancellationToken);

        return revisions
            .Select(revision => new StrategyTemplateRevisionSnapshot(
                revision.Id,
                template.Id,
                template.TemplateKey,
                revision.RevisionNumber,
                revision.SchemaVersion,
                revision.ValidationStatusCode,
                revision.ValidationSummary,
                IsActive: template.ActiveTradingStrategyTemplateRevisionId == revision.Id,
                IsLatest: template.LatestTradingStrategyTemplateRevisionId == revision.Id,
                IsArchived: revision.ArchivedAtUtc.HasValue || !template.IsActive,
                revision.SourceTemplateKey,
                revision.SourceRevisionNumber,
                revision.ArchivedAtUtc))
            .ToArray();
    }

    public async Task<StrategyTemplateSnapshot> ArchiveAsync(
        string templateKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedTemplateKey = NormalizeTemplateKey(templateKey, nameof(templateKey));
        if (TryGetBuiltInTemplate(normalizedTemplateKey).Key is not null)
        {
            throw new InvalidOperationException("Built-in strategy templates cannot be archived.");
        }

        EnsurePersistenceAvailable();

        var template = await dbContext!.TradingStrategyTemplates
            .SingleOrDefaultAsync(
                entity => entity.TemplateKey == normalizedTemplateKey &&
                          !entity.IsDeleted,
                cancellationToken)
            ?? throw new InvalidOperationException($"Strategy template '{normalizedTemplateKey}' was not found.");
        var revisions = await dbContext.TradingStrategyTemplateRevisions
            .Where(entity => entity.TradingStrategyTemplateId == template.Id && !entity.IsDeleted)
            .ToListAsync(cancellationToken);

        if (!template.IsActive)
        {
            return BuildCustomSnapshot(template, revisions);
        }

        var archivedAtUtc = DateTime.UtcNow;
        template.IsActive = false;
        template.ArchivedAtUtc = archivedAtUtc;
        template.ActiveTradingStrategyTemplateRevisionId = null;
        foreach (var revision in revisions.Where(entity => !entity.ArchivedAtUtc.HasValue))
        {
            revision.ArchivedAtUtc = archivedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            template.OwnerUserId,
            "Strategy.Template.Archived",
            normalizedTemplateKey,
            $"TemplateKey={normalizedTemplateKey}; Source={template.SourceTemplateKey ?? "custom"}; State=Archived",
            cancellationToken);

        return BuildCustomSnapshot(template, revisions);
    }

    private async Task<StrategyTemplateSnapshot> CreateCustomInternalAsync(
        string ownerUserId,
        string templateKey,
        string templateName,
        string description,
        string category,
        string definitionJson,
        string? sourceTemplateKey,
        int? sourceRevisionNumber,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePersistenceAvailable();

        var normalizedOwnerUserId = NormalizeRequired(ownerUserId, 450, nameof(ownerUserId));
        var normalizedTemplateKey = NormalizeTemplateKey(templateKey, nameof(templateKey));
        var normalizedTemplateName = NormalizeRequired(templateName, 128, nameof(templateName));
        var normalizedDescription = NormalizeRequired(description, 512, nameof(description));
        var normalizedCategory = NormalizeRequired(category, 64, nameof(category));

        await EnsureTemplateKeyAvailableAsync(normalizedTemplateKey, cancellationToken);

        var normalizedDefinition = NormalizeDefinitionJson(definitionJson, normalizedTemplateKey, normalizedTemplateName, 1, "Custom");
        var document = parser.Parse(normalizedDefinition);
        var validation = validator.Validate(document);
        if (!validation.IsValid)
        {
            throw new StrategyDefinitionValidationException(validation.StatusCode, validation.Summary, validation.FailureReasons);
        }

        var template = new TradingStrategyTemplate
        {
            Id = Guid.NewGuid(),
            OwnerUserId = normalizedOwnerUserId,
            TemplateKey = normalizedTemplateKey,
            TemplateName = normalizedTemplateName,
            Description = normalizedDescription,
            Category = normalizedCategory,
            SchemaVersion = document.SchemaVersion,
            DefinitionJson = normalizedDefinition,
            IsActive = true,
            SourceTemplateKey = NormalizeOptional(sourceTemplateKey, 128),
            ArchivedAtUtc = null
        };
        var revision = new TradingStrategyTemplateRevision
        {
            Id = Guid.NewGuid(),
            OwnerUserId = normalizedOwnerUserId,
            TradingStrategyTemplateId = template.Id,
            RevisionNumber = 1,
            SchemaVersion = document.SchemaVersion,
            DefinitionJson = normalizedDefinition,
            ValidationStatusCode = validation.StatusCode,
            ValidationSummary = validation.Summary,
            SourceTemplateKey = template.SourceTemplateKey,
            SourceRevisionNumber = sourceRevisionNumber
        };

        dbContext!.TradingStrategyTemplates.Add(template);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.Entry(template).State = EntityState.Detached;

        var persistedTemplate = await dbContext.TradingStrategyTemplates
            .SingleAsync(entity => entity.Id == template.Id, cancellationToken);

        dbContext.TradingStrategyTemplateRevisions.Add(revision);
        await dbContext.SaveChangesAsync(cancellationToken);

        persistedTemplate.ActiveTradingStrategyTemplateRevisionId = revision.Id;
        persistedTemplate.LatestTradingStrategyTemplateRevisionId = revision.Id;
        await dbContext.SaveChangesAsync(cancellationToken);

        await WriteAuditAsync(
            normalizedOwnerUserId,
            "Strategy.Template.Created",
            normalizedTemplateKey,
            $"TemplateKey={normalizedTemplateKey}; Source={persistedTemplate.SourceTemplateKey ?? "custom"}; SchemaVersion={persistedTemplate.SchemaVersion}; Revision=1; Validation={validation.StatusCode}",
            cancellationToken);

        return BuildCustomSnapshot(persistedTemplate, [revision]);
    }

    private async Task EnsureTemplateKeyAvailableAsync(string templateKey, CancellationToken cancellationToken)
    {
        if (TryGetBuiltInTemplate(templateKey).Key is not null)
        {
            throw new InvalidOperationException($"Strategy template key '{templateKey}' is reserved by a built-in template.");
        }

        var exists = await dbContext!.TradingStrategyTemplates
            .AsNoTracking()
            .AnyAsync(entity => entity.TemplateKey == templateKey && !entity.IsDeleted, cancellationToken);

        if (exists)
        {
            throw new InvalidOperationException($"Strategy template '{templateKey}' already exists.");
        }
    }

    private StrategyTemplateSnapshot BuildBuiltInSnapshot((string Key, string Name, string Description, string Category, string DefinitionJson) template)
    {
        var normalizedDefinition = NormalizeDefinitionJson(template.DefinitionJson, template.Key, template.Name, 1, "BuiltIn");
        var document = parser.Parse(normalizedDefinition);
        var validation = validator.Validate(document);
        if (!validation.IsValid)
        {
            throw new StrategyDefinitionValidationException(validation.StatusCode, validation.Summary, validation.FailureReasons);
        }

        return new StrategyTemplateSnapshot(
            template.Key,
            template.Name,
            template.Description,
            template.Category,
            document.SchemaVersion,
            normalizedDefinition,
            validation,
            IsBuiltIn: true,
            IsActive: true,
            TemplateSource: "BuiltIn",
            SourceTemplateKey: null,
            ActiveRevisionNumber: 1,
            LatestRevisionNumber: 1);
    }

    private StrategyTemplateSnapshot BuildCustomSnapshot(
        TradingStrategyTemplate template,
        IReadOnlyCollection<TradingStrategyTemplateRevision> revisions)
    {
        var activeRevision = template.ActiveTradingStrategyTemplateRevisionId.HasValue
            ? revisions.SingleOrDefault(entity => entity.Id == template.ActiveTradingStrategyTemplateRevisionId.Value)
            : revisions.OrderByDescending(entity => entity.RevisionNumber).FirstOrDefault();
        var latestRevision = template.LatestTradingStrategyTemplateRevisionId.HasValue
            ? revisions.SingleOrDefault(entity => entity.Id == template.LatestTradingStrategyTemplateRevisionId.Value)
            : revisions.OrderByDescending(entity => entity.RevisionNumber).FirstOrDefault();
        var definitionJson = activeRevision?.DefinitionJson ?? latestRevision?.DefinitionJson ?? template.DefinitionJson;
        var document = parser.Parse(definitionJson);
        var validation = validator.Validate(document);
        if (!validation.IsValid)
        {
            throw new StrategyDefinitionValidationException(validation.StatusCode, validation.Summary, validation.FailureReasons);
        }

        return new StrategyTemplateSnapshot(
            template.TemplateKey,
            template.TemplateName,
            template.Description,
            template.Category,
            document.SchemaVersion,
            definitionJson.Trim(),
            validation,
            IsBuiltIn: false,
            IsActive: template.IsActive,
            TemplateSource: "Custom",
            SourceTemplateKey: template.SourceTemplateKey,
            ActiveRevisionNumber: activeRevision?.RevisionNumber ?? 0,
            LatestRevisionNumber: latestRevision?.RevisionNumber ?? 0,
            SourceRevisionNumber: activeRevision?.SourceRevisionNumber,
            TemplateId: template.Id,
            ActiveRevisionId: activeRevision?.Id,
            LatestRevisionId: latestRevision?.Id,
            ArchivedAtUtc: template.ArchivedAtUtc);
    }

    private static (string Key, string Name, string Description, string Category, string DefinitionJson) TryGetBuiltInTemplate(string templateKey)
    {
        return BuiltInTemplates.SingleOrDefault(item => string.Equals(item.Key, templateKey, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeDefinitionJson(
        string definitionJson,
        string templateKey,
        string templateName,
        int templateRevisionNumber,
        string templateSource)
    {
        if (string.IsNullOrWhiteSpace(definitionJson))
        {
            throw new ArgumentException("The strategy template definition is required.", nameof(definitionJson));
        }

        JsonNode rootNode;
        try
        {
            rootNode = JsonNode.Parse(definitionJson)
                ?? throw new StrategyRuleParseException("Strategy definition payload could not be parsed.");
        }
        catch (JsonException exception)
        {
            throw new StrategyRuleParseException($"Strategy definition payload could not be parsed: {exception.Message}");
        }

        if (rootNode is not JsonObject rootObject)
        {
            throw new StrategyRuleParseException("Strategy definition root must be a JSON object.");
        }

        rootObject["metadata"] = new JsonObject
        {
            ["templateKey"] = templateKey,
            ["templateName"] = templateName,
            ["templateRevisionNumber"] = templateRevisionNumber,
            ["templateSource"] = templateSource
        };

        return rootObject.ToJsonString(JsonSerializerOptions);
    }

    private async Task<IReadOnlyCollection<TradingStrategyTemplateRevision>> LoadRevisionsAsync(
        IReadOnlyCollection<Guid> templateIds,
        CancellationToken cancellationToken)
    {
        if (dbContext is null || templateIds.Count == 0)
        {
            return Array.Empty<TradingStrategyTemplateRevision>();
        }

        return await dbContext.TradingStrategyTemplateRevisions
            .AsNoTracking()
            .Where(entity => templateIds.Contains(entity.TradingStrategyTemplateId) && !entity.IsDeleted)
            .ToListAsync(cancellationToken);
    }

    private void EnsurePersistenceAvailable()
    {
        if (dbContext is null)
        {
            throw new InvalidOperationException("Strategy template persistence is not available in the current service scope.");
        }
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

    private static string NormalizeTemplateKey(string? value, string parameterName)
    {
        var normalized = NormalizeRequired(value, 128, parameterName)
            .Replace(' ', '-')
            .ToLowerInvariant();

        return normalized;
    }

    private static string NormalizeRequired(string? value, int maxLength, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        if (normalized.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"The value cannot exceed {maxLength} characters.");
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (normalized.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"The value cannot exceed {maxLength} characters.");
        }

        return normalized;
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
