using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Infrastructure.Strategies;

namespace CoinBot.UnitTests.Infrastructure.Strategies;

public sealed class StrategyRuleParserTests
{
    [Fact]
    public void Parse_ReturnsTypedDocument_WhenJsonMatchesStrategyBackendStandard()
    {
        var parser = new StrategyRuleParser();

        var document = parser.Parse(
            """
            {
              "schemaVersion": 1,
              "entry": {
                "operator": "all",
                "rules": [
                  {
                    "path": "context.mode",
                    "comparison": "equals",
                    "value": "Demo"
                  },
                  {
                    "path": "indicator.rsi.value",
                    "comparison": "lessThanOrEqual",
                    "value": 30
                  }
                ]
              },
              "risk": {
                "path": "indicator.rsi.isReady",
                "comparison": "equals",
                "value": true
              }
            }
            """);

        var entryGroup = Assert.IsType<StrategyRuleGroup>(document.Entry);
        var firstCondition = Assert.IsType<StrategyRuleCondition>(entryGroup.Rules[0]);
        var riskCondition = Assert.IsType<StrategyRuleCondition>(document.Risk);

        Assert.Equal(1, document.SchemaVersion);
        Assert.Equal(StrategyRuleGroupOperator.All, entryGroup.Operator);
        Assert.Equal("context.mode", firstCondition.Path);
        Assert.Equal(StrategyRuleComparisonOperator.Equals, firstCondition.Comparison);
        Assert.Equal(StrategyRuleOperandKind.String, firstCondition.Operand.Kind);
        Assert.Equal("Demo", firstCondition.Operand.Value);
        Assert.Equal("indicator.rsi.isReady", riskCondition.Path);
        Assert.Equal(StrategyRuleOperandKind.Boolean, riskCondition.Operand.Kind);
        Assert.Equal(bool.TrueString, riskCondition.Operand.Value);
    }

    [Fact]
    public void Parse_ParsesSchemaV2Metadata_AndNormalizesRuleMetadata()
    {
        var parser = new StrategyRuleParser();

        var document = parser.Parse(
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
                    "ruleId": "entry-rsi",
                    "ruleType": "rsi",
                    "path": "indicator.rsi.value",
                    "comparison": "lessThanOrEqual",
                    "value": 30,
                    "timeframe": "1m",
                    "weight": 25,
                    "enabled": true,
                    "group": "entry"
                  }
                ]
              }
            }
            """);

        var entryGroup = Assert.IsType<StrategyRuleGroup>(document.Entry);
        var condition = Assert.IsType<StrategyRuleCondition>(Assert.Single(entryGroup.Rules));

        Assert.Equal(2, document.SchemaVersion);
        Assert.Equal("rsi-reversal", document.Metadata?.TemplateKey);
        Assert.Equal("RSI Reversal", document.Metadata?.TemplateName);
        Assert.Equal("entry-root", entryGroup.Metadata?.RuleId);
        Assert.Equal("group", entryGroup.Metadata?.RuleType);
        Assert.Equal("1m", entryGroup.Metadata?.Timeframe);
        Assert.Equal("entry-rsi", condition.Metadata?.RuleId);
        Assert.Equal("rsi", condition.Metadata?.RuleType);
        Assert.Equal(25m, condition.Metadata?.Weight);
        Assert.True(condition.Metadata?.Enabled);
    }

    [Fact]
    public void Parse_RejectsUnsupportedSchemaVersion()
    {
        var parser = new StrategyRuleParser();

        var exception = Assert.Throws<StrategyRuleParseException>(() => parser.Parse(
            """
            {
              "schemaVersion": 3,
              "entry": {
                "path": "context.mode",
                "comparison": "equals",
                "value": "Demo"
              }
            }
            """));

        Assert.Contains("schemaVersion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsMalformedJson_WithSanitizedParseReason()
    {
        var parser = new StrategyRuleParser();

        var exception = Assert.Throws<StrategyRuleParseException>(() => parser.Parse("{ \"schemaVersion\": 2, \"entry\": [ }"));

        Assert.Contains("could not be parsed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsUnsupportedGroupOperator_FailClosed()
    {
        var parser = new StrategyRuleParser();

        var exception = Assert.Throws<StrategyRuleParseException>(() => parser.Parse(
            """
            {
              "schemaVersion": 2,
              "entry": {
                "operator": "xor",
                "rules": [
                  {
                    "path": "context.mode",
                    "comparison": "equals",
                    "value": "Live"
                  }
                ]
              }
            }
            """));

        Assert.Contains("operator", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
