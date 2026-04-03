using CoinBot.Application.Abstractions.Strategies;

namespace CoinBot.Infrastructure.Strategies;

public sealed class StrategyDefinitionValidator : IStrategyDefinitionValidator
{
    private static readonly HashSet<int> SupportedSchemaVersions = [1, 2];

    private static readonly HashSet<string> SupportedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "context.mode",
        "indicator.symbol",
        "indicator.timeframe",
        "indicator.sampleCount",
        "indicator.requiredSampleCount",
        "indicator.state",
        "indicator.dataQualityReasonCode",
        "indicator.source",
        "indicator.rsi.period",
        "indicator.rsi.isReady",
        "indicator.rsi.value",
        "indicator.macd.fastPeriod",
        "indicator.macd.slowPeriod",
        "indicator.macd.signalPeriod",
        "indicator.macd.isReady",
        "indicator.macd.macdLine",
        "indicator.macd.signalLine",
        "indicator.macd.histogram",
        "indicator.bollinger.period",
        "indicator.bollinger.standardDeviationMultiplier",
        "indicator.bollinger.isReady",
        "indicator.bollinger.middleBand",
        "indicator.bollinger.upperBand",
        "indicator.bollinger.lowerBand",
        "indicator.bollinger.standardDeviation"
    };

    private static readonly HashSet<string> SupportedRuleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "context",
        "data-quality",
        "rsi",
        "macd",
        "bollinger",
        "group"
    };

    private static readonly HashSet<string> SupportedTimeframes = new(StringComparer.OrdinalIgnoreCase)
    {
        "1m",
        "3m",
        "5m",
        "15m",
        "1h",
        "4h",
        "1d"
    };

    public StrategyDefinitionValidationSnapshot Validate(StrategyRuleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var failures = new List<string>();
        var ruleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enabledRuleCount = 0;

        if (!SupportedSchemaVersions.Contains(document.SchemaVersion))
        {
            failures.Add($"UnsupportedSchemaVersion:{document.SchemaVersion}");
        }

        ValidateMetadata(document.Metadata, "metadata", failures);

        if (document.Entry is null && document.Exit is null && document.Risk is null)
        {
            failures.Add("MissingRuleRoot:entry-exit-risk");
        }

        enabledRuleCount += ValidateNode(document.Entry, "entry", parentOperator: null, ruleIds, failures);
        enabledRuleCount += ValidateNode(document.Exit, "exit", parentOperator: null, ruleIds, failures);
        enabledRuleCount += ValidateNode(document.Risk, "risk", parentOperator: null, ruleIds, failures);

        if (failures.Count > 0)
        {
            return new StrategyDefinitionValidationSnapshot(
                false,
                failures[0],
                $"Strategy validation failed: {string.Join(" | ", failures)}",
                failures,
                enabledRuleCount);
        }

        return new StrategyDefinitionValidationSnapshot(
            true,
            "Valid",
            $"Strategy definition is valid. EnabledRules={enabledRuleCount}.",
            Array.Empty<string>(),
            enabledRuleCount);
    }

    private static int ValidateNode(
        StrategyRuleNode? node,
        string location,
        StrategyRuleGroupOperator? parentOperator,
        ISet<string> ruleIds,
        ICollection<string> failures)
    {
        if (node is null)
        {
            return 0;
        }

        return node switch
        {
            StrategyRuleGroup group => ValidateGroup(group, location, ruleIds, failures),
            StrategyRuleCondition condition => ValidateCondition(condition, location, parentOperator, ruleIds, failures),
            _ => AddUnsupportedNodeFailure(node, location, failures)
        };
    }

    private static int ValidateGroup(
        StrategyRuleGroup group,
        string location,
        ISet<string> ruleIds,
        ICollection<string> failures)
    {
        var metadata = ValidateMetadata(group.Metadata, location, failures, expectedType: "group");

        if (group.Rules.Count == 0)
        {
            failures.Add($"EmptyRuleGroup:{location}");
            return 0;
        }

        var enabledRuleCount = metadata.Enabled ? 0 : 0;
        var seenConditionSignatures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var equalityConditions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < group.Rules.Count; index++)
        {
            var childLocation = $"{location}.rules[{index}]";
            var childNode = group.Rules[index];
            enabledRuleCount += ValidateNode(childNode, childLocation, group.Operator, ruleIds, failures);

            if (childNode is not StrategyRuleCondition childCondition)
            {
                continue;
            }

            var childMetadata = childCondition.Metadata ?? StrategyRuleMetadata.Default;
            if (!childMetadata.Enabled)
            {
                continue;
            }

            var signature = BuildConditionSignature(childCondition);
            if (seenConditionSignatures.ContainsKey(signature))
            {
                failures.Add($"DuplicateCondition:{childLocation}:{signature}");
            }
            else
            {
                seenConditionSignatures[signature] = childLocation;
            }

            if (group.Operator != StrategyRuleGroupOperator.All ||
                childCondition.Comparison != StrategyRuleComparisonOperator.Equals ||
                childCondition.Operand.Kind == StrategyRuleOperandKind.Path)
            {
                continue;
            }

            var normalizedPath = childCondition.Path.Trim().ToLowerInvariant();
            if (equalityConditions.TryGetValue(normalizedPath, out var expectedValue) &&
                !string.Equals(expectedValue, childCondition.Operand.Value, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"ConflictingRule:{childLocation}:{normalizedPath}");
            }
            else
            {
                equalityConditions[normalizedPath] = childCondition.Operand.Value;
            }
        }

        return metadata.Enabled ? enabledRuleCount : 0;
    }

    private static int ValidateCondition(
        StrategyRuleCondition condition,
        string location,
        StrategyRuleGroupOperator? parentOperator,
        ISet<string> ruleIds,
        ICollection<string> failures)
    {
        _ = parentOperator;
        var metadata = ValidateMetadata(condition.Metadata, location, failures);
        var normalizedPath = condition.Path?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            failures.Add($"MissingRulePath:{location}");
        }
        else if (!SupportedPaths.Contains(normalizedPath))
        {
            failures.Add($"UnsupportedRulePath:{location}:{normalizedPath}");
        }

        if (!string.IsNullOrWhiteSpace(metadata.RuleId) && !ruleIds.Add(metadata.RuleId.Trim()))
        {
            failures.Add($"DuplicateRuleId:{location}:{metadata.RuleId.Trim()}");
        }

        if (condition.Operand.Kind == StrategyRuleOperandKind.Path)
        {
            var normalizedOperandPath = condition.Operand.Value?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedOperandPath) || !SupportedPaths.Contains(normalizedOperandPath))
            {
                failures.Add($"UnsupportedOperandPath:{location}:{condition.Operand.Value}");
            }
        }

        if (normalizedPath?.Equals("indicator.rsi.value", StringComparison.OrdinalIgnoreCase) == true &&
            condition.Operand.Kind == StrategyRuleOperandKind.Number &&
            decimal.TryParse(condition.Operand.Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var rsiThreshold) &&
            (rsiThreshold < 0m || rsiThreshold > 100m))
        {
            failures.Add($"InvalidRsiThreshold:{location}:{condition.Operand.Value}");
        }

        return metadata.Enabled ? 1 : 0;
    }

    private static StrategyRuleMetadata ValidateMetadata(
        StrategyRuleMetadata? metadata,
        string location,
        ICollection<string> failures,
        string? expectedType = null)
    {
        var normalizedMetadata = metadata ?? StrategyRuleMetadata.Default;

        if (!string.IsNullOrWhiteSpace(normalizedMetadata.RuleType) &&
            !SupportedRuleTypes.Contains(normalizedMetadata.RuleType.Trim()))
        {
            failures.Add($"UnsupportedRuleType:{location}:{normalizedMetadata.RuleType.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(expectedType) &&
            !string.IsNullOrWhiteSpace(normalizedMetadata.RuleType) &&
            !string.Equals(normalizedMetadata.RuleType.Trim(), expectedType, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"MismatchedRuleType:{location}:{normalizedMetadata.RuleType.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(normalizedMetadata.Timeframe) &&
            !SupportedTimeframes.Contains(normalizedMetadata.Timeframe.Trim()))
        {
            failures.Add($"UnsupportedTimeframe:{location}:{normalizedMetadata.Timeframe.Trim()}");
        }

        if (normalizedMetadata.Weight <= 0m || normalizedMetadata.Weight > 100m)
        {
            failures.Add($"InvalidRuleWeight:{location}:{normalizedMetadata.Weight}");
        }

        return normalizedMetadata;
    }

    private static void ValidateMetadata(
        StrategyDefinitionMetadata? metadata,
        string location,
        ICollection<string> failures)
    {
        if (metadata is null)
        {
            return;
        }

        if (metadata.TemplateKey is not null && string.IsNullOrWhiteSpace(metadata.TemplateKey))
        {
            failures.Add($"InvalidTemplateKey:{location}");
        }

        if (metadata.TemplateName is not null && string.IsNullOrWhiteSpace(metadata.TemplateName))
        {
            failures.Add($"InvalidTemplateName:{location}");
        }
    }

    private static string BuildConditionSignature(StrategyRuleCondition condition)
    {
        return $"{condition.Path.Trim().ToLowerInvariant()}|{condition.Comparison}|{condition.Operand.Kind}|{condition.Operand.Value.Trim().ToLowerInvariant()}";
    }

    private static int AddUnsupportedNodeFailure(StrategyRuleNode node, string location, ICollection<string> failures)
    {
        failures.Add($"UnsupportedRuleNode:{location}:{node.GetType().Name}");
        return 0;
    }
}
