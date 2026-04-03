using CoinBot.Application.Abstractions.Strategies;

namespace CoinBot.Infrastructure.Strategies;

public sealed class StrategyTemplateCatalogService(
    IStrategyRuleParser parser,
    IStrategyDefinitionValidator validator) : IStrategyTemplateCatalogService
{
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

    public Task<IReadOnlyCollection<StrategyTemplateSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyCollection<StrategyTemplateSnapshot>>(BuiltInTemplates
            .Select(BuildSnapshot)
            .OrderBy(template => template.TemplateName, StringComparer.Ordinal)
            .ToArray());
    }

    public Task<StrategyTemplateSnapshot> GetAsync(string templateKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedTemplateKey = templateKey?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTemplateKey))
        {
            throw new ArgumentException("The template key is required.", nameof(templateKey));
        }

        var template = BuiltInTemplates.SingleOrDefault(item => string.Equals(item.Key, normalizedTemplateKey, StringComparison.OrdinalIgnoreCase));
        if (template.Key is null)
        {
            throw new InvalidOperationException($"Strategy template '{normalizedTemplateKey}' was not found.");
        }

        return Task.FromResult(BuildSnapshot(template));
    }

    private StrategyTemplateSnapshot BuildSnapshot((string Key, string Name, string Description, string Category, string DefinitionJson) template)
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
            validation);
    }
}
