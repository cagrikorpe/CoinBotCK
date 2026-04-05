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

        return builtInSnapshots
            .Concat(customTemplates.Select(BuildCustomSnapshot))
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
                          entity.IsActive &&
                          !entity.IsDeleted,
                cancellationToken);

        return template is not null
            ? BuildCustomSnapshot(template)
            : throw new InvalidOperationException($"Strategy template '{normalizedTemplateKey}' was not found.");
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
            cancellationToken);
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

        if (!template.IsActive)
        {
            return BuildCustomSnapshot(template);
        }

        template.IsActive = false;
        template.ArchivedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            template.OwnerUserId,
            "Strategy.Template.Archived",
            normalizedTemplateKey,
            $"TemplateKey={normalizedTemplateKey}; Source={template.SourceTemplateKey ?? "custom"}; State=Archived",
            cancellationToken);

        return BuildCustomSnapshot(template);
    }

    private async Task<StrategyTemplateSnapshot> CreateCustomInternalAsync(
        string ownerUserId,
        string templateKey,
        string templateName,
        string description,
        string category,
        string definitionJson,
        string? sourceTemplateKey,
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

        var normalizedDefinition = NormalizeDefinitionJson(definitionJson, normalizedTemplateKey, normalizedTemplateName);
        var document = parser.Parse(normalizedDefinition);
        var validation = validator.Validate(document);
        if (!validation.IsValid)
        {
            throw new StrategyDefinitionValidationException(validation.StatusCode, validation.Summary, validation.FailureReasons);
        }

        var template = new TradingStrategyTemplate
        {
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

        dbContext!.TradingStrategyTemplates.Add(template);
        await dbContext.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            normalizedOwnerUserId,
            "Strategy.Template.Created",
            normalizedTemplateKey,
            $"TemplateKey={normalizedTemplateKey}; Source={template.SourceTemplateKey ?? "custom"}; SchemaVersion={template.SchemaVersion}; Validation={validation.StatusCode}",
            cancellationToken);

        return BuildCustomSnapshot(template);
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
        var document = parser.Parse(template.DefinitionJson);
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
            template.DefinitionJson.Trim(),
            validation,
            IsBuiltIn: true,
            IsActive: true,
            TemplateSource: "BuiltIn",
            SourceTemplateKey: null);
    }

    private StrategyTemplateSnapshot BuildCustomSnapshot(TradingStrategyTemplate template)
    {
        var document = parser.Parse(template.DefinitionJson);
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
            template.DefinitionJson.Trim(),
            validation,
            IsBuiltIn: false,
            IsActive: template.IsActive,
            TemplateSource: "Custom",
            SourceTemplateKey: template.SourceTemplateKey);
    }

    private static (string Key, string Name, string Description, string Category, string DefinitionJson) TryGetBuiltInTemplate(string templateKey)
    {
        return BuiltInTemplates.SingleOrDefault(item => string.Equals(item.Key, templateKey, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeDefinitionJson(string definitionJson, string templateKey, string templateName)
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
            ["templateName"] = templateName
        };

        return rootObject.ToJsonString(JsonSerializerOptions);
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
