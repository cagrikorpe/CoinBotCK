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
    public void Parse_RejectsUnsupportedSchemaVersion()
    {
        var parser = new StrategyRuleParser();

        var exception = Assert.Throws<StrategyRuleParseException>(() => parser.Parse(
            """
            {
              "schemaVersion": 2,
              "entry": {
                "path": "context.mode",
                "comparison": "equals",
                "value": "Demo"
              }
            }
            """));

        Assert.Contains("schemaVersion", exception.Message, StringComparison.Ordinal);
    }
}
