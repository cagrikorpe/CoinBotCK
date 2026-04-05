using System.Globalization;
using System.Text.Json;
using CoinBot.Application.Abstractions.Strategies;

namespace CoinBot.Infrastructure.Strategies;

public sealed class StrategyRuleParser : IStrategyRuleParser
{
    private static readonly HashSet<int> SupportedSchemaVersions = [1, 2];

    public StrategyRuleDocument Parse(string definitionJson)
    {
        if (string.IsNullOrWhiteSpace(definitionJson))
        {
            throw new StrategyRuleParseException("Strategy definition JSON is required.");
        }

        try
        {
            using var document = JsonDocument.Parse(definitionJson);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new StrategyRuleParseException("Strategy definition root must be a JSON object.");
            }

            ValidateAllowedProperties(root, ["schemaVersion", "metadata", "entry", "exit", "risk"], "strategy definition");

            var schemaVersion = ParseSchemaVersion(root);
            var metadata = ParseDefinitionMetadata(root);
            var entry = TryGetProperty(root, "entry", out var entryElement)
                ? ParseNode(entryElement, "entry")
                : null;
            var exit = TryGetProperty(root, "exit", out var exitElement)
                ? ParseNode(exitElement, "exit")
                : null;
            var risk = TryGetProperty(root, "risk", out var riskElement)
                ? ParseNode(riskElement, "risk")
                : null;

            if (entry is null && exit is null && risk is null)
            {
                throw new StrategyRuleParseException("Strategy definition must contain at least one of 'entry', 'exit' or 'risk'.");
            }

            return new StrategyRuleDocument(schemaVersion, entry, exit, risk, metadata);
        }
        catch (JsonException exception)
        {
            throw new StrategyRuleParseException($"Strategy definition JSON could not be parsed: {exception.Message}");
        }
    }

    private static int ParseSchemaVersion(JsonElement root)
    {
        if (!TryGetProperty(root, "schemaVersion", out var schemaVersionElement) ||
            schemaVersionElement.ValueKind != JsonValueKind.Number ||
            !schemaVersionElement.TryGetInt32(out var schemaVersion))
        {
            throw new StrategyRuleParseException("Strategy definition must include a numeric 'schemaVersion'.");
        }

        if (!SupportedSchemaVersions.Contains(schemaVersion))
        {
            throw new StrategyRuleParseException(
                $"Strategy definition schemaVersion '{schemaVersion}' is not supported. Expected one of '{string.Join(", ", SupportedSchemaVersions.Order())}'.");
        }

        return schemaVersion;
    }

    private static StrategyDefinitionMetadata? ParseDefinitionMetadata(JsonElement root)
    {
        if (!TryGetProperty(root, "metadata", out var metadataElement))
        {
            return null;
        }

        if (metadataElement.ValueKind != JsonValueKind.Object)
        {
            throw new StrategyRuleParseException("Strategy definition property 'metadata' must be a JSON object.");
        }

        ValidateAllowedProperties(metadataElement, ["templateKey", "templateName", "templateRevisionNumber", "templateSource"], "strategy definition.metadata");

        return new StrategyDefinitionMetadata(
            TryGetOptionalString(metadataElement, "templateKey", "strategy definition.metadata.templateKey"),
            TryGetOptionalString(metadataElement, "templateName", "strategy definition.metadata.templateName"),
            TryGetOptionalInt32(metadataElement, "templateRevisionNumber", "strategy definition.metadata.templateRevisionNumber"),
            TryGetOptionalString(metadataElement, "templateSource", "strategy definition.metadata.templateSource"));
    }

    private static StrategyRuleNode ParseNode(JsonElement element, string location)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new StrategyRuleParseException($"Strategy rule node '{location}' must be a JSON object.");
        }

        var isGroup = TryGetProperty(element, "operator", out _) || TryGetProperty(element, "rules", out _);
        var isCondition = TryGetProperty(element, "path", out _) || TryGetProperty(element, "comparison", out _);

        if (isGroup && isCondition)
        {
            throw new StrategyRuleParseException($"Strategy rule node '{location}' cannot mix group and condition properties.");
        }

        if (isGroup)
        {
            return ParseGroup(element, location);
        }

        if (isCondition)
        {
            return ParseCondition(element, location);
        }

        throw new StrategyRuleParseException($"Strategy rule node '{location}' is invalid.");
    }

    private static StrategyRuleGroup ParseGroup(JsonElement element, string location)
    {
        ValidateAllowedProperties(element, ["operator", "rules", "ruleId", "ruleType", "timeframe", "weight", "enabled", "group"], location);

        var @operator = ParseGroupOperator(GetRequiredString(element, "operator", location));
        var metadata = ParseRuleMetadata(element, location);

        if (!TryGetProperty(element, "rules", out var rulesElement) || rulesElement.ValueKind != JsonValueKind.Array)
        {
            throw new StrategyRuleParseException($"Strategy rule group '{location}' must contain a 'rules' array.");
        }

        var rules = new List<StrategyRuleNode>();

        foreach (var ruleElement in rulesElement.EnumerateArray())
        {
            rules.Add(ParseNode(ruleElement, $"{location}.rules[{rules.Count}]"));
        }

        if (rules.Count == 0)
        {
            throw new StrategyRuleParseException($"Strategy rule group '{location}' must contain at least one rule.");
        }

        return new StrategyRuleGroup(@operator, rules, metadata);
    }

    private static StrategyRuleCondition ParseCondition(JsonElement element, string location)
    {
        ValidateAllowedProperties(element, ["path", "comparison", "value", "valuePath", "ruleId", "ruleType", "timeframe", "weight", "enabled", "group"], location);

        var path = GetRequiredString(element, "path", location);
        var comparison = ParseComparisonOperator(GetRequiredString(element, "comparison", location));
        var metadata = ParseRuleMetadata(element, location);
        var hasValue = TryGetProperty(element, "value", out var valueElement);
        var hasValuePath = TryGetProperty(element, "valuePath", out var valuePathElement);

        if (hasValue == hasValuePath)
        {
            throw new StrategyRuleParseException(
                $"Strategy rule condition '{location}' must contain exactly one of 'value' or 'valuePath'.");
        }

        var operand = hasValue
            ? ParseLiteralOperand(valueElement, location)
            : new StrategyRuleOperand(StrategyRuleOperandKind.Path, NormalizeRequiredString(valuePathElement.GetString(), $"{location}.valuePath"));

        return new StrategyRuleCondition(path, comparison, operand, metadata);
    }

    private static StrategyRuleMetadata ParseRuleMetadata(JsonElement element, string location)
    {
        return new StrategyRuleMetadata(
            TryGetOptionalString(element, "ruleId", $"{location}.ruleId"),
            TryGetOptionalString(element, "ruleType", $"{location}.ruleType"),
            TryGetOptionalString(element, "timeframe", $"{location}.timeframe"),
            TryGetOptionalDecimal(element, "weight", $"{location}.weight") ?? 1m,
            TryGetOptionalBoolean(element, "enabled", $"{location}.enabled") ?? true,
            TryGetOptionalString(element, "group", $"{location}.group"));
    }

    private static StrategyRuleOperand ParseLiteralOperand(JsonElement valueElement, string location)
    {
        return valueElement.ValueKind switch
        {
            JsonValueKind.Number => new StrategyRuleOperand(
                StrategyRuleOperandKind.Number,
                decimal.Parse(valueElement.GetRawText(), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)),
            JsonValueKind.String => new StrategyRuleOperand(
                StrategyRuleOperandKind.String,
                NormalizeRequiredString(valueElement.GetString(), $"{location}.value")),
            JsonValueKind.True => new StrategyRuleOperand(StrategyRuleOperandKind.Boolean, bool.TrueString),
            JsonValueKind.False => new StrategyRuleOperand(StrategyRuleOperandKind.Boolean, bool.FalseString),
            _ => throw new StrategyRuleParseException(
                $"Strategy rule literal '{location}.value' must be a string, number or boolean.")
        };
    }

    private static StrategyRuleGroupOperator ParseGroupOperator(string value)
    {
        return NormalizeRequiredString(value, nameof(value)).ToLowerInvariant() switch
        {
            "all" => StrategyRuleGroupOperator.All,
            "any" => StrategyRuleGroupOperator.Any,
            _ => throw new StrategyRuleParseException($"Strategy rule group operator '{value}' is not supported.")
        };
    }

    private static StrategyRuleComparisonOperator ParseComparisonOperator(string value)
    {
        return NormalizeRequiredString(value, nameof(value)).ToLowerInvariant() switch
        {
            "equals" => StrategyRuleComparisonOperator.Equals,
            "notequals" => StrategyRuleComparisonOperator.NotEquals,
            "greaterthan" => StrategyRuleComparisonOperator.GreaterThan,
            "greaterthanorequal" => StrategyRuleComparisonOperator.GreaterThanOrEqual,
            "lessthan" => StrategyRuleComparisonOperator.LessThan,
            "lessthanorequal" => StrategyRuleComparisonOperator.LessThanOrEqual,
            "between" => StrategyRuleComparisonOperator.Between,
            "notbetween" => StrategyRuleComparisonOperator.NotBetween,
            "contains" => StrategyRuleComparisonOperator.Contains,
            "startswith" => StrategyRuleComparisonOperator.StartsWith,
            "endswith" => StrategyRuleComparisonOperator.EndsWith,
            _ => throw new StrategyRuleParseException($"Strategy rule comparison '{value}' is not supported.")
        };
    }

    private static string GetRequiredString(JsonElement element, string propertyName, string location)
    {
        if (!TryGetProperty(element, propertyName, out var propertyValue) || propertyValue.ValueKind != JsonValueKind.String)
        {
            throw new StrategyRuleParseException($"Strategy rule property '{location}.{propertyName}' is required.");
        }

        return NormalizeRequiredString(propertyValue.GetString(), $"{location}.{propertyName}");
    }

    private static string? TryGetOptionalString(JsonElement element, string propertyName, string location)
    {
        if (!TryGetProperty(element, propertyName, out var propertyValue))
        {
            return null;
        }

        if (propertyValue.ValueKind != JsonValueKind.String)
        {
            throw new StrategyRuleParseException($"Strategy rule property '{location}' must be a string.");
        }

        return NormalizeRequiredString(propertyValue.GetString(), location);
    }

    private static decimal? TryGetOptionalDecimal(JsonElement element, string propertyName, string location)
    {
        if (!TryGetProperty(element, propertyName, out var propertyValue))
        {
            return null;
        }

        if (propertyValue.ValueKind != JsonValueKind.Number || !propertyValue.TryGetDecimal(out var value))
        {
            throw new StrategyRuleParseException($"Strategy rule property '{location}' must be numeric.");
        }

        return value;
    }

    private static int? TryGetOptionalInt32(JsonElement element, string propertyName, string location)
    {
        if (!TryGetProperty(element, propertyName, out var propertyValue))
        {
            return null;
        }

        if (propertyValue.ValueKind != JsonValueKind.Number || !propertyValue.TryGetInt32(out var value))
        {
            throw new StrategyRuleParseException($"Strategy rule property '{location}' must be numeric.");
        }

        return value;
    }

    private static bool? TryGetOptionalBoolean(JsonElement element, string propertyName, string location)
    {
        if (!TryGetProperty(element, propertyName, out var propertyValue))
        {
            return null;
        }

        return propertyValue.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new StrategyRuleParseException($"Strategy rule property '{location}' must be boolean.")
        };
    }

    private static string NormalizeRequiredString(string? value, string propertyName)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new StrategyRuleParseException($"Strategy rule property '{propertyName}' is required.");
        }

        return normalizedValue;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement propertyValue)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                propertyValue = property.Value;
                return true;
            }
        }

        propertyValue = default;
        return false;
    }

    private static void ValidateAllowedProperties(JsonElement element, IReadOnlyCollection<string> allowedProperties, string location)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (allowedProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            throw new StrategyRuleParseException($"Strategy rule property '{location}.{property.Name}' is not supported.");
        }
    }
}
