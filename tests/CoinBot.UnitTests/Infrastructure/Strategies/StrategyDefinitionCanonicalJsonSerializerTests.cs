using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Infrastructure.Strategies;

namespace CoinBot.UnitTests.Infrastructure.Strategies;

public sealed class StrategyDefinitionCanonicalJsonSerializerTests
{
    private static readonly StrategyRuleParser Parser = new();

    [Fact]
    public void Serialize_ProducesDeterministicCanonicalJson_ForBuilderPayload()
    {
        var canonicalJsonFirst = Serialize(
            """
            {
              "entry": {
                "weight": 1.0,
                "enabled": true,
                "timeframe": "1m",
                "ruleType": "group",
                "group": "entry",
                "ruleId": "entry-root",
                "rules": [
                  {
                    "comparison": "equals",
                    "value": "Live",
                    "path": "context.mode",
                    "ruleType": "context",
                    "ruleId": "entry-mode",
                    "enabled": true,
                    "weight": 10.0,
                    "group": "entry",
                    "timeframe": "1m"
                  },
                  {
                    "comparison": "lessThanOrEqual",
                    "value": 30.000,
                    "path": "indicator.rsi.value",
                    "ruleType": "rsi",
                    "ruleId": "entry-rsi",
                    "enabled": true,
                    "weight": 10.0,
                    "group": "entry",
                    "timeframe": "1m"
                  }
                ],
                "operator": "all"
              },
              "metadata": {
                "templateName": "  Builder Strategy  ",
                "templateSource": " Custom ",
                "templateKey": " builder-custom "
              },
              "schemaVersion": 2
            }
            """);
        var canonicalJsonSecond = Serialize(
            """
            {
              "entry": {
                "weight": 1.0,
                "enabled": true,
                "timeframe": "1m",
                "ruleType": "group",
                "group": "entry",
                "ruleId": "entry-root",
                "rules": [
                  {
                    "comparison": "equals",
                    "value": "Live",
                    "path": "context.mode",
                    "ruleType": "context",
                    "ruleId": "entry-mode",
                    "enabled": true,
                    "weight": 10.0,
                    "group": "entry",
                    "timeframe": "1m"
                  },
                  {
                    "comparison": "lessThanOrEqual",
                    "value": 30.000,
                    "path": "indicator.rsi.value",
                    "ruleType": "rsi",
                    "ruleId": "entry-rsi",
                    "enabled": true,
                    "weight": 10.0,
                    "group": "entry",
                    "timeframe": "1m"
                  }
                ],
                "operator": "all"
              },
              "metadata": {
                "templateName": "  Builder Strategy  ",
                "templateSource": " Custom ",
                "templateKey": " builder-custom "
              },
              "schemaVersion": 2
            }
            """);

        AssertCanonicalJson(
            """
            {
              "schemaVersion": 2,
              "metadata": {
                "templateKey": "builder-custom",
                "templateName": "Builder Strategy",
                "templateSource": "Custom"
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
                    "path": "context.mode",
                    "comparison": "equals",
                    "value": "Live",
                    "ruleId": "entry-mode",
                    "ruleType": "context",
                    "timeframe": "1m",
                    "weight": 10,
                    "enabled": true,
                    "group": "entry"
                  },
                  {
                    "path": "indicator.rsi.value",
                    "comparison": "lessThanOrEqual",
                    "value": 30,
                    "ruleId": "entry-rsi",
                    "ruleType": "rsi",
                    "timeframe": "1m",
                    "weight": 10,
                    "enabled": true,
                    "group": "entry"
                  }
                ]
              }
            }
            """,
            canonicalJsonFirst);

        Assert.Equal(canonicalJsonFirst, canonicalJsonSecond);
    }

    [Fact]
    public void Serialize_NormalizesEquivalentNumericForms_ToSameCanonicalJson()
    {
        var formPayloadCanonicalJson = Serialize(
            """
            {
              "schemaVersion": 2,
              "entry": {
                "operator": "all",
                "ruleId": "entry-root",
                "ruleType": "group",
                "timeframe": "1m",
                "weight": 1.000,
                "enabled": true,
                "group": "entry",
                "rules": [
                  {
                    "path": "indicator.rsi.value",
                    "comparison": "lessThanOrEqual",
                    "value": 30.000,
                    "ruleId": "entry-rsi",
                    "ruleType": "rsi",
                    "timeframe": "1m",
                    "weight": 10.000,
                    "enabled": true,
                    "group": "entry"
                  }
                ]
              }
            }
            """);
        var reloadedPayloadCanonicalJson = Serialize(
            """
            {
              "schemaVersion": 2,
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
                    "path": "indicator.rsi.value",
                    "comparison": "lessThanOrEqual",
                    "value": 30,
                    "ruleId": "entry-rsi",
                    "ruleType": "rsi",
                    "timeframe": "1m",
                    "weight": 10,
                    "enabled": true,
                    "group": "entry"
                  }
                ]
              }
            }
            """);

        Assert.Equal(formPayloadCanonicalJson, reloadedPayloadCanonicalJson);
        Assert.DoesNotContain("30.000", formPayloadCanonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("10.000", formPayloadCanonicalJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_RoundTripsFormSourceJson_WithoutPropertyOrderDrift()
    {
        var canonicalJson = Serialize(
            """
            {
              "shortEntry": {
                "weight": 1.0,
                "enabled": true,
                "group": "shortEntry",
                "ruleId": "short-entry-root",
                "rules": [
                  {
                    "comparison": "lessThan",
                    "path": "indicator.macd.histogram",
                    "valuePath": "indicator.macd.signal",
                    "ruleType": "macd",
                    "ruleId": "short-entry-macd",
                    "enabled": false,
                    "weight": 2.500,
                    "group": "shortEntry",
                    "timeframe": "5m"
                  }
                ],
                "operator": "all",
                "ruleType": "group"
              },
              "direction": "neutral",
              "schemaVersion": 2,
              "longEntry": {
                "weight": 1.0,
                "enabled": true,
                "group": "longEntry",
                "ruleId": "long-entry-root",
                "rules": [
                  {
                    "comparison": "greaterThan",
                    "path": "indicator.ema.fast",
                    "valuePath": "indicator.ema.slow",
                    "ruleType": "ema",
                    "ruleId": "long-entry-ema",
                    "enabled": true,
                    "weight": 2.500,
                    "group": "longEntry",
                    "timeframe": "5m"
                  }
                ],
                "operator": "all",
                "ruleType": "group"
              }
            }
            """);
        var reloadedCanonicalJson = Serialize(canonicalJson);

        AssertCanonicalJson(
            """
            {
              "schemaVersion": 2,
              "direction": "neutral",
              "longEntry": {
                "operator": "all",
                "ruleId": "long-entry-root",
                "ruleType": "group",
                "weight": 1,
                "enabled": true,
                "group": "longEntry",
                "rules": [
                  {
                    "path": "indicator.ema.fast",
                    "comparison": "greaterThan",
                    "valuePath": "indicator.ema.slow",
                    "ruleId": "long-entry-ema",
                    "ruleType": "ema",
                    "timeframe": "5m",
                    "weight": 2.5,
                    "enabled": true,
                    "group": "longEntry"
                  }
                ]
              },
              "shortEntry": {
                "operator": "all",
                "ruleId": "short-entry-root",
                "ruleType": "group",
                "weight": 1,
                "enabled": true,
                "group": "shortEntry",
                "rules": [
                  {
                    "path": "indicator.macd.histogram",
                    "comparison": "lessThan",
                    "valuePath": "indicator.macd.signal",
                    "ruleId": "short-entry-macd",
                    "ruleType": "macd",
                    "timeframe": "5m",
                    "weight": 2.5,
                    "enabled": false,
                    "group": "shortEntry"
                  }
                ]
              }
            }
            """,
            canonicalJson);

        Assert.Equal(canonicalJson, reloadedCanonicalJson);
    }

    [Fact]
    public void Serialize_OmitsEmptyMetadata_AndPreservesDisabledRuleBehavior()
    {
        var document = new StrategyRuleDocument(
            2,
            new StrategyRuleGroup(
                StrategyRuleGroupOperator.All,
                [
                    new StrategyRuleCondition(
                        "indicator.rsi.isReady",
                        StrategyRuleComparisonOperator.Equals,
                        new StrategyRuleOperand(StrategyRuleOperandKind.Boolean, bool.TrueString),
                        new StrategyRuleMetadata(
                            RuleType: "rsi",
                            Weight: 1.000m,
                            Enabled: false))
                ],
                new StrategyRuleMetadata(
                    RuleId: "entry-root",
                    RuleType: "group",
                    Weight: 1.000m,
                    Enabled: false)),
            Exit: null,
            Risk: null,
            Metadata: StrategyDefinitionMetadata.Empty);

        var canonicalJson = StrategyDefinitionCanonicalJsonSerializer.Serialize(document);

        AssertCanonicalJson(
            """
            {
              "schemaVersion": 2,
              "entry": {
                "operator": "all",
                "ruleId": "entry-root",
                "ruleType": "group",
                "weight": 1,
                "enabled": false,
                "rules": [
                  {
                    "path": "indicator.rsi.isReady",
                    "comparison": "equals",
                    "value": true,
                    "ruleType": "rsi",
                    "weight": 1,
                    "enabled": false
                  }
                ]
              }
            }
            """,
            canonicalJson);

        Assert.DoesNotContain("metadata", canonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("null", canonicalJson, StringComparison.OrdinalIgnoreCase);
    }

    private static string Serialize(string definitionJson)
    {
        return StrategyDefinitionCanonicalJsonSerializer.Serialize(Parser.Parse(definitionJson));
    }

    private static void AssertCanonicalJson(string expected, string actual)
    {
        Assert.Equal(NormalizeLineEndings(expected).Trim(), NormalizeLineEndings(actual).Trim());
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
