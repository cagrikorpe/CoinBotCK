using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoinBot.Application.Abstractions.Strategies;

namespace CoinBot.Infrastructure.Strategies;

internal static class StrategyDefinitionCanonicalJsonSerializer
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string Serialize(StrategyRuleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var root = new JsonObject
        {
            ["schemaVersion"] = document.SchemaVersion
        };

        if (ShouldWriteDirection(document))
        {
            root["direction"] = ToDirectionValue(document.Direction);
        }

        var metadata = BuildMetadata(document.Metadata);
        if (metadata is not null)
        {
            root["metadata"] = metadata;
        }

        AddNode(root, "entry", document.Entry);
        AddNode(root, "exit", document.Exit);
        AddNode(root, "risk", document.Risk);
        AddNode(root, "longEntry", document.LongEntry);
        AddNode(root, "longExit", document.LongExit);
        AddNode(root, "shortEntry", document.ShortEntry);
        AddNode(root, "shortExit", document.ShortExit);

        return root.ToJsonString(JsonSerializerOptions);
    }

    private static bool ShouldWriteDirection(StrategyRuleDocument document)
    {
        return document.Direction != StrategyTradeDirection.Long || document.HasDirectionalRoots;
    }

    private static JsonObject? BuildMetadata(StrategyDefinitionMetadata? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        var node = new JsonObject();
        AddString(node, "templateKey", metadata.TemplateKey);
        AddString(node, "templateName", metadata.TemplateName);

        if (metadata.TemplateRevisionNumber.HasValue)
        {
            node["templateRevisionNumber"] = metadata.TemplateRevisionNumber.Value;
        }

        AddString(node, "templateSource", metadata.TemplateSource);
        return node.Count == 0
            ? null
            : node;
    }

    private static void AddNode(JsonObject root, string propertyName, StrategyRuleNode? node)
    {
        if (node is null)
        {
            return;
        }

        root[propertyName] = SerializeNode(node);
    }

    private static JsonNode SerializeNode(StrategyRuleNode node)
    {
        return node switch
        {
            StrategyRuleGroup group => SerializeGroup(group),
            StrategyRuleCondition condition => SerializeCondition(condition),
            _ => throw new InvalidOperationException($"Unsupported strategy rule node type '{node.GetType().Name}'.")
        };
    }

    private static JsonObject SerializeGroup(StrategyRuleGroup group)
    {
        var metadata = group.Metadata ?? StrategyRuleMetadata.Default;
        var node = new JsonObject
        {
            ["operator"] = ToGroupOperatorValue(group.Operator)
        };

        AddRuleMetadata(node, metadata, includeRuleType: true, defaultGroup: metadata.Group);
        var rules = new JsonArray();
        foreach (var child in group.Rules)
        {
            rules.Add(SerializeNode(child));
        }

        node["rules"] = rules;
        return node;
    }

    private static JsonObject SerializeCondition(StrategyRuleCondition condition)
    {
        var metadata = condition.Metadata ?? StrategyRuleMetadata.Default;
        var node = new JsonObject
        {
            ["path"] = condition.Path,
            ["comparison"] = ToComparisonValue(condition.Comparison)
        };

        if (condition.Operand.Kind == StrategyRuleOperandKind.Path)
        {
            node["valuePath"] = condition.Operand.Value;
        }
        else
        {
            node["value"] = SerializeLiteralOperand(condition.Operand);
        }

        AddRuleMetadata(node, metadata, includeRuleType: true, defaultGroup: metadata.Group);
        return node;
    }

    private static JsonNode SerializeLiteralOperand(StrategyRuleOperand operand)
    {
        return operand.Kind switch
        {
            StrategyRuleOperandKind.Number when decimal.TryParse(operand.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) => JsonValue.Create(NormalizeNumber(number))!,
            StrategyRuleOperandKind.Boolean when bool.TryParse(operand.Value, out var flag) => JsonValue.Create(flag)!,
            StrategyRuleOperandKind.String => JsonValue.Create(operand.Value)!,
            StrategyRuleOperandKind.Number => JsonValue.Create(operand.Value)!,
            StrategyRuleOperandKind.Boolean => JsonValue.Create(operand.Value)!,
            _ => JsonValue.Create(operand.Value)!
        };
    }

    private static void AddRuleMetadata(
        JsonObject node,
        StrategyRuleMetadata metadata,
        bool includeRuleType,
        string? defaultGroup)
    {
        AddString(node, "ruleId", metadata.RuleId);

        if (includeRuleType)
        {
            AddString(node, "ruleType", metadata.RuleType);
        }

        AddString(node, "timeframe", metadata.Timeframe);
        node["weight"] = NormalizeNumber(metadata.Weight);
        node["enabled"] = metadata.Enabled;
        AddString(node, "group", metadata.Group ?? defaultGroup);
    }

    private static void AddString(JsonObject node, string propertyName, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            node[propertyName] = value.Trim();
        }
    }

    private static decimal NormalizeNumber(decimal number)
    {
        return number / 1.0000000000000000000000000000m;
    }

    private static string ToDirectionValue(StrategyTradeDirection direction)
    {
        return direction switch
        {
            StrategyTradeDirection.Long => "long",
            StrategyTradeDirection.Short => "short",
            StrategyTradeDirection.Neutral => "neutral",
            _ => throw new InvalidOperationException($"Unsupported strategy direction '{direction}'.")
        };
    }

    private static string ToGroupOperatorValue(StrategyRuleGroupOperator groupOperator)
    {
        return groupOperator switch
        {
            StrategyRuleGroupOperator.All => "all",
            StrategyRuleGroupOperator.Any => "any",
            _ => throw new InvalidOperationException($"Unsupported strategy rule group operator '{groupOperator}'.")
        };
    }

    private static string ToComparisonValue(StrategyRuleComparisonOperator comparison)
    {
        return comparison switch
        {
            StrategyRuleComparisonOperator.Equals => "equals",
            StrategyRuleComparisonOperator.NotEquals => "notEquals",
            StrategyRuleComparisonOperator.GreaterThan => "greaterThan",
            StrategyRuleComparisonOperator.GreaterThanOrEqual => "greaterThanOrEqual",
            StrategyRuleComparisonOperator.LessThan => "lessThan",
            StrategyRuleComparisonOperator.LessThanOrEqual => "lessThanOrEqual",
            StrategyRuleComparisonOperator.Between => "between",
            StrategyRuleComparisonOperator.NotBetween => "notBetween",
            StrategyRuleComparisonOperator.Contains => "contains",
            StrategyRuleComparisonOperator.StartsWith => "startsWith",
            StrategyRuleComparisonOperator.EndsWith => "endsWith",
            _ => throw new InvalidOperationException($"Unsupported strategy rule comparison '{comparison}'.")
        };
    }
}
