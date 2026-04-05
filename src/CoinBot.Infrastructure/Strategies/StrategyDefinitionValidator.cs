using System.Globalization;
using CoinBot.Application.Abstractions.Strategies;

namespace CoinBot.Infrastructure.Strategies;

public sealed class StrategyDefinitionValidator : IStrategyDefinitionValidator
{
    private static readonly HashSet<int> SupportedSchemaVersions = [1, 2];

    private static readonly Dictionary<string, StrategyRuleOperandKind> SupportedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["context.mode"] = StrategyRuleOperandKind.String,
        ["indicator.symbol"] = StrategyRuleOperandKind.String,
        ["indicator.timeframe"] = StrategyRuleOperandKind.String,
        ["indicator.sampleCount"] = StrategyRuleOperandKind.Number,
        ["indicator.requiredSampleCount"] = StrategyRuleOperandKind.Number,
        ["indicator.sampleCoveragePercent"] = StrategyRuleOperandKind.Number,
        ["indicator.latencySeconds"] = StrategyRuleOperandKind.Number,
        ["indicator.state"] = StrategyRuleOperandKind.String,
        ["indicator.dataQualityReasonCode"] = StrategyRuleOperandKind.String,
        ["indicator.source"] = StrategyRuleOperandKind.String,
        ["indicator.rsi.period"] = StrategyRuleOperandKind.Number,
        ["indicator.rsi.isReady"] = StrategyRuleOperandKind.Boolean,
        ["indicator.rsi.value"] = StrategyRuleOperandKind.Number,
        ["indicator.macd.fastPeriod"] = StrategyRuleOperandKind.Number,
        ["indicator.macd.slowPeriod"] = StrategyRuleOperandKind.Number,
        ["indicator.macd.signalPeriod"] = StrategyRuleOperandKind.Number,
        ["indicator.macd.isReady"] = StrategyRuleOperandKind.Boolean,
        ["indicator.macd.macdLine"] = StrategyRuleOperandKind.Number,
        ["indicator.macd.signalLine"] = StrategyRuleOperandKind.Number,
        ["indicator.macd.histogram"] = StrategyRuleOperandKind.Number,
        ["indicator.macd.spread"] = StrategyRuleOperandKind.Number,
        ["indicator.bollinger.period"] = StrategyRuleOperandKind.Number,
        ["indicator.bollinger.standardDeviationMultiplier"] = StrategyRuleOperandKind.Number,
        ["indicator.bollinger.isReady"] = StrategyRuleOperandKind.Boolean,
        ["indicator.bollinger.middleBand"] = StrategyRuleOperandKind.Number,
        ["indicator.bollinger.upperBand"] = StrategyRuleOperandKind.Number,
        ["indicator.bollinger.lowerBand"] = StrategyRuleOperandKind.Number,
        ["indicator.bollinger.standardDeviation"] = StrategyRuleOperandKind.Number,
        ["indicator.bollinger.bandWidth"] = StrategyRuleOperandKind.Number
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
        "30m",
        "1h",
        "2h",
        "4h",
        "6h",
        "12h",
        "1d",
        "1w"
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

        ValidateDefinitionMetadata(document.Metadata, "metadata", failures);

        if (document.Entry is null && document.Exit is null && document.Risk is null)
        {
            failures.Add("MissingRuleRoot:entry-exit-risk");
        }

        enabledRuleCount += ValidateNode(document.Entry, "entry", null, ruleIds, failures);
        enabledRuleCount += ValidateNode(document.Exit, "exit", null, ruleIds, failures);
        enabledRuleCount += ValidateNode(document.Risk, "risk", null, ruleIds, failures);

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
        var metadata = ValidateRuleMetadata(group.Metadata, location, failures, expectedType: "group");

        if (group.Rules.Count == 0)
        {
            failures.Add($"EmptyRuleGroup:{location}");
            return 0;
        }

        var enabledRuleCount = 0;
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
        var metadata = ValidateRuleMetadata(condition.Metadata, location, failures);
        var normalizedPath = condition.Path?.Trim();
        StrategyRuleOperandKind? pathKind = null;

        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            failures.Add($"MissingRulePath:{location}");
        }
        else if (!SupportedPaths.TryGetValue(normalizedPath, out var resolvedPathKind))
        {
            failures.Add($"UnsupportedRulePath:{location}:{normalizedPath}");
        }
        else
        {
            pathKind = resolvedPathKind;
        }

        if (!string.IsNullOrWhiteSpace(metadata.RuleId) && !ruleIds.Add(metadata.RuleId.Trim()))
        {
            failures.Add($"DuplicateRuleId:{location}:{metadata.RuleId.Trim()}");
        }

        if (condition.Operand.Kind == StrategyRuleOperandKind.Path)
        {
            var normalizedOperandPath = condition.Operand.Value?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedOperandPath) ||
                !SupportedPaths.TryGetValue(normalizedOperandPath, out var operandPathKind))
            {
                failures.Add($"UnsupportedOperandPath:{location}:{condition.Operand.Value}");
            }
            else if (condition.Comparison is StrategyRuleComparisonOperator.Between or StrategyRuleComparisonOperator.NotBetween)
            {
                failures.Add($"InvalidRangeOperand:{location}:{condition.Operand.Value}");
            }
            else if (pathKind.HasValue && operandPathKind != pathKind.Value)
            {
                failures.Add($"OperandTypeMismatch:{location}:{pathKind.Value}:{operandPathKind}");
            }
        }
        else if (pathKind.HasValue && !IsLiteralOperandCompatible(pathKind.Value, condition.Comparison, condition.Operand))
        {
            failures.Add($"OperandTypeMismatch:{location}:{pathKind.Value}:{condition.Operand.Kind}");
        }

        if (pathKind.HasValue && !IsComparisonSupported(pathKind.Value, condition.Comparison))
        {
            failures.Add($"UnsupportedComparisonForPath:{location}:{normalizedPath}:{condition.Comparison}");
        }

        if (pathKind == StrategyRuleOperandKind.Number &&
            condition.Comparison is StrategyRuleComparisonOperator.Between or StrategyRuleComparisonOperator.NotBetween &&
            !TryParseRange(condition.Operand.Value, out _, out _))
        {
            failures.Add($"InvalidRangeOperand:{location}:{condition.Operand.Value}");
        }

        if (normalizedPath?.Equals("indicator.rsi.value", StringComparison.OrdinalIgnoreCase) == true &&
            condition.Operand.Kind == StrategyRuleOperandKind.Number &&
            decimal.TryParse(condition.Operand.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var rsiThreshold) &&
            (rsiThreshold < 0m || rsiThreshold > 100m))
        {
            failures.Add($"InvalidRsiThreshold:{location}:{condition.Operand.Value}");
        }

        return metadata.Enabled ? 1 : 0;
    }

    private static StrategyRuleMetadata ValidateRuleMetadata(
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

    private static void ValidateDefinitionMetadata(
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

        if (metadata.TemplateRevisionNumber.HasValue && metadata.TemplateRevisionNumber.Value <= 0)
        {
            failures.Add($"InvalidTemplateRevision:{location}:{metadata.TemplateRevisionNumber.Value}");
        }

        if (metadata.TemplateSource is not null &&
            !string.Equals(metadata.TemplateSource.Trim(), "BuiltIn", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(metadata.TemplateSource.Trim(), "Custom", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"InvalidTemplateSource:{location}:{metadata.TemplateSource.Trim()}");
        }
    }

    private static string BuildConditionSignature(StrategyRuleCondition condition)
    {
        return $"{condition.Path.Trim().ToLowerInvariant()}|{condition.Comparison}|{condition.Operand.Kind}|{condition.Operand.Value.Trim().ToLowerInvariant()}";
    }

    private static bool IsLiteralOperandCompatible(
        StrategyRuleOperandKind pathKind,
        StrategyRuleComparisonOperator comparison,
        StrategyRuleOperand operand)
    {
        if (comparison is StrategyRuleComparisonOperator.Between or StrategyRuleComparisonOperator.NotBetween)
        {
            return pathKind == StrategyRuleOperandKind.Number &&
                   operand.Kind is StrategyRuleOperandKind.Number or StrategyRuleOperandKind.String;
        }

        return pathKind == operand.Kind;
    }

    private static bool IsComparisonSupported(StrategyRuleOperandKind pathKind, StrategyRuleComparisonOperator comparison)
    {
        return pathKind switch
        {
            StrategyRuleOperandKind.Number => comparison is
                StrategyRuleComparisonOperator.Equals or
                StrategyRuleComparisonOperator.NotEquals or
                StrategyRuleComparisonOperator.GreaterThan or
                StrategyRuleComparisonOperator.GreaterThanOrEqual or
                StrategyRuleComparisonOperator.LessThan or
                StrategyRuleComparisonOperator.LessThanOrEqual or
                StrategyRuleComparisonOperator.Between or
                StrategyRuleComparisonOperator.NotBetween,
            StrategyRuleOperandKind.String => comparison is
                StrategyRuleComparisonOperator.Equals or
                StrategyRuleComparisonOperator.NotEquals or
                StrategyRuleComparisonOperator.Contains or
                StrategyRuleComparisonOperator.StartsWith or
                StrategyRuleComparisonOperator.EndsWith,
            StrategyRuleOperandKind.Boolean => comparison is
                StrategyRuleComparisonOperator.Equals or
                StrategyRuleComparisonOperator.NotEquals,
            _ => false
        };
    }

    private static bool TryParseRange(string? value, out decimal lowerBound, out decimal upperBound)
    {
        lowerBound = default;
        upperBound = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split("..", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !decimal.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out lowerBound) ||
            !decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out upperBound))
        {
            return false;
        }

        return lowerBound <= upperBound;
    }

    private static int AddUnsupportedNodeFailure(StrategyRuleNode node, string location, ICollection<string> failures)
    {
        failures.Add($"UnsupportedRuleNode:{location}:{node.GetType().Name}");
        return 0;
    }
}
