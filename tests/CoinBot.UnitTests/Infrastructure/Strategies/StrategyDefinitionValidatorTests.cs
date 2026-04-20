using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Strategies;

namespace CoinBot.UnitTests.Infrastructure.Strategies;

public sealed class StrategyDefinitionValidatorTests
{
    [Fact]
    public void Validate_ReturnsValidSnapshot_ForSupportedSchemaAndRuleMetadata()
    {
        var parser = new StrategyRuleParser();
        var validator = new StrategyDefinitionValidator();

        var snapshot = validator.Validate(parser.Parse(
            """
            {
              "schemaVersion": 2,
              "metadata": { "templateKey": "rsi-reversal", "templateName": "RSI Reversal" },
              "entry": {
                "operator": "all",
                "ruleId": "entry-root",
                "ruleType": "group",
                "timeframe": "1m",
                "weight": 1,
                "enabled": true,
                "rules": [
                  {
                    "ruleId": "entry-rsi",
                    "ruleType": "rsi",
                    "path": "indicator.rsi.value",
                    "comparison": "lessThanOrEqual",
                    "value": 30,
                    "timeframe": "1m",
                    "weight": 10,
                    "enabled": true
                  }
                ]
              }
            }
            """));

        Assert.True(snapshot.IsValid);
        Assert.Equal("Valid", snapshot.StatusCode);
        Assert.Equal(1, snapshot.EnabledRuleCount);
    }


    [Fact]
    public void Validate_FailsClosed_ForNeutralStrategyDirection()
    {
        var validator = new StrategyDefinitionValidator();
        var document = new StrategyRuleDocument(
            2,
            new StrategyRuleCondition(
                "context.mode",
                StrategyRuleComparisonOperator.Equals,
                new StrategyRuleOperand(StrategyRuleOperandKind.String, "Live")),
            null,
            null,
            Direction: StrategyTradeDirection.Neutral);

        var snapshot = validator.Validate(document);

        Assert.False(snapshot.IsValid);
        Assert.Equal("UnsupportedStrategyDirection:Neutral", snapshot.StatusCode);
    }

    [Fact]
    public void Validate_AllowsNeutralStrategyDirection_WhenDirectionalRootsAreUsed()
    {
        var parser = new StrategyRuleParser();
        var validator = new StrategyDefinitionValidator();

        var snapshot = validator.Validate(parser.Parse(
            """
            {
              "schemaVersion": 2,
              "longEntry": {
                "operator": "all",
                "rules": [
                  {
                    "path": "context.mode",
                    "comparison": "equals",
                    "value": "Live"
                  }
                ]
              },
              "shortEntry": {
                "operator": "all",
                "rules": [
                  {
                    "path": "context.mode",
                    "comparison": "equals",
                    "value": "Demo"
                  }
                ]
              }
            }
            """));

        Assert.True(snapshot.IsValid);
    }

    [Fact]
    public void ValidateForBotDirectionMode_FailsClosed_WhenLongOnlyBotReceivesShortRules()
    {
        var parser = new StrategyRuleParser();
        var validator = new StrategyDefinitionValidator();
        var document = parser.Parse(
            """
            {
              "schemaVersion": 2,
              "shortEntry": {
                "operator": "all",
                "rules": [
                  {
                    "path": "context.mode",
                    "comparison": "equals",
                    "value": "Live"
                  }
                ]
              }
            }
            """);

        var snapshot = validator.ValidateForBotDirectionMode(document, TradingBotDirectionMode.LongOnly);

        Assert.False(snapshot.IsValid);
        Assert.Equal("DirectionModeBlocked:Short", snapshot.StatusCode);
    }

    [Fact]
    public void ValidateForBotDirectionMode_FailsClosed_WhenShortOnlyBotReceivesLongRules()
    {
        var parser = new StrategyRuleParser();
        var validator = new StrategyDefinitionValidator();
        var document = parser.Parse(
            """
            {
              "schemaVersion": 2,
              "longEntry": {
                "operator": "all",
                "rules": [
                  {
                    "path": "context.mode",
                    "comparison": "equals",
                    "value": "Live"
                  }
                ]
              }
            }
            """);

        var snapshot = validator.ValidateForBotDirectionMode(document, TradingBotDirectionMode.ShortOnly);

        Assert.False(snapshot.IsValid);
        Assert.Equal("DirectionModeBlocked:Long", snapshot.StatusCode);
    }

    [Fact]
    public void ValidateForBotDirectionMode_AllowsLongShortBot_WhenBothDirectionsExist()
    {
        var parser = new StrategyRuleParser();
        var validator = new StrategyDefinitionValidator();
        var document = parser.Parse(
            """
            {
              "schemaVersion": 2,
              "longEntry": {
                "operator": "all",
                "rules": [
                  {
                    "path": "context.mode",
                    "comparison": "equals",
                    "value": "Live"
                  }
                ]
              },
              "shortEntry": {
                "operator": "all",
                "rules": [
                  {
                    "path": "context.mode",
                    "comparison": "equals",
                    "value": "Demo"
                  }
                ]
              }
            }
            """);

        var snapshot = validator.ValidateForBotDirectionMode(document, TradingBotDirectionMode.LongShort);

        Assert.True(snapshot.IsValid);
    }

    [Fact]
    public void Validate_FailsClosed_WithExactReason_ForUnsupportedRuleType()
    {
        var parser = new StrategyRuleParser();
        var validator = new StrategyDefinitionValidator();

        var snapshot = validator.Validate(parser.Parse(
            """
            {
              "schemaVersion": 2,
              "entry": {
                "path": "indicator.rsi.value",
                "comparison": "lessThanOrEqual",
                "value": 30,
                "ruleId": "entry-rsi",
                "ruleType": "unsupported",
                "weight": 10,
                "enabled": true
              }
            }
            """));

        Assert.False(snapshot.IsValid);
        Assert.Equal("UnsupportedRuleType:entry:unsupported", snapshot.StatusCode);
        Assert.Contains("UnsupportedRuleType:entry:unsupported", snapshot.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_FailsClosed_ForDuplicateAndConflictingRules()
    {
        var parser = new StrategyRuleParser();
        var validator = new StrategyDefinitionValidator();

        var snapshot = validator.Validate(parser.Parse(
            """
            {
              "schemaVersion": 2,
              "entry": {
                "operator": "all",
                "rules": [
                  {
                    "ruleId": "mode-live",
                    "path": "context.mode",
                    "comparison": "equals",
                    "value": "Live",
                    "weight": 10,
                    "enabled": true
                  },
                  {
                    "ruleId": "mode-live",
                    "path": "context.mode",
                    "comparison": "equals",
                    "value": "Demo",
                    "weight": 10,
                    "enabled": true
                  }
                ]
              }
            }
            """));

        Assert.False(snapshot.IsValid);
        Assert.Contains(snapshot.FailureReasons, reason => reason.StartsWith("DuplicateRuleId:", StringComparison.Ordinal));
        Assert.Contains(snapshot.FailureReasons, reason => reason.StartsWith("ConflictingRule:", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_FailsClosed_ForUnsupportedPath()
    {
        var parser = new StrategyRuleParser();
        var validator = new StrategyDefinitionValidator();

        var snapshot = validator.Validate(parser.Parse(
            """
            {
              "schemaVersion": 2,
              "entry": {
                "operator": "all",
                "ruleId": "entry-root",
                "ruleType": "group",
                "timeframe": "5m",
                "weight": 1,
                "enabled": true,
                "rules": [
                  {
                    "ruleId": "entry-invalid-path",
                    "ruleType": "context",
                    "path": "indicator.closePrice",
                    "comparison": "equals",
                    "value": 100,
                    "timeframe": "5m",
                    "weight": 10,
                    "enabled": true
                  }
                ]
              }
            }
            """));

        Assert.False(snapshot.IsValid);
        Assert.Contains(snapshot.FailureReasons, reason => reason.StartsWith("UnsupportedRulePath:entry.rules[0]:indicator.closePrice", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_FailsClosed_ForUnsupportedTimeframe()
    {
        var parser = new StrategyRuleParser();
        var validator = new StrategyDefinitionValidator();

        var snapshot = validator.Validate(parser.Parse(
            """
            {
              "schemaVersion": 2,
              "entry": {
                "path": "context.mode",
                "comparison": "equals",
                "value": "Live",
                "ruleId": "entry-mode",
                "ruleType": "context",
                "timeframe": "2m",
                "weight": 10,
                "enabled": true
              }
            }
            """));

        Assert.False(snapshot.IsValid);
        Assert.Contains(snapshot.FailureReasons, reason => reason.StartsWith("UnsupportedTimeframe:entry:2m", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_FailsClosed_WhenRuleGroupTimeframeDiffersFromIndicatorRuleTimeframe()
    {
        var parser = new StrategyRuleParser();
        var validator = new StrategyDefinitionValidator();

        var snapshot = validator.Validate(parser.Parse(
            """
            {
              "schemaVersion": 2,
              "longEntry": {
                "operator": "all",
                "ruleId": "longEntry-root",
                "ruleType": "group",
                "timeframe": "5m",
                "weight": 1,
                "enabled": true,
                "rules": [
                  {
                    "ruleId": "longEntry-rsi",
                    "ruleType": "rsi",
                    "path": "indicator.rsi.value",
                    "comparison": "lessThanOrEqual",
                    "value": 30,
                    "timeframe": "1m",
                    "weight": 10,
                    "enabled": true
                  }
                ]
              },
              "risk": {
                "operator": "all",
                "ruleId": "risk-root",
                "ruleType": "group",
                "timeframe": "5m",
                "weight": 1,
                "enabled": true,
                "rules": [
                  {
                    "ruleId": "risk-sample",
                    "ruleType": "data-quality",
                    "path": "indicator.sampleCount",
                    "comparison": "greaterThanOrEqual",
                    "value": 34,
                    "timeframe": "1m",
                    "weight": 10,
                    "enabled": true
                  }
                ]
              }
            }
            """));

        Assert.False(snapshot.IsValid);
        Assert.Equal("RuleGroupTimeframeMismatch:risk.rules[0]:group=5m:rule=1m", snapshot.StatusCode);
        Assert.Contains(snapshot.FailureReasons, reason => reason == "RuleGroupTimeframeMismatch:longEntry.rules[0]:group=5m:rule=1m");
        Assert.Contains(snapshot.FailureReasons, reason => reason == "RuleGroupTimeframeMismatch:risk.rules[0]:group=5m:rule=1m");
    }

    [Fact]
    public void Validate_FailsClosed_WhenRuleGroupTimeframeDiffersFromRuntimeTimeframeReference()
    {
        var parser = new StrategyRuleParser();
        var validator = new StrategyDefinitionValidator();

        var snapshot = validator.Validate(parser.Parse(
            """
            {
              "schemaVersion": 2,
              "entry": {
                "operator": "all",
                "ruleId": "entry-root",
                "ruleType": "group",
                "timeframe": "5m",
                "weight": 1,
                "enabled": true,
                "rules": [
                  {
                    "ruleId": "entry-timeframe",
                    "ruleType": "data-quality",
                    "path": "indicator.timeframe",
                    "comparison": "equals",
                    "value": "1m",
                    "timeframe": "5m",
                    "weight": 10,
                    "enabled": true
                  }
                ]
              }
            }
            """));

        Assert.False(snapshot.IsValid);
        Assert.Contains(snapshot.FailureReasons, reason => reason == "RuleTimeframeMismatch:entry.rules[0]:rule=5m:indicator=1m");
        Assert.Contains(snapshot.FailureReasons, reason => reason == "RuleGroupTimeframeMismatch:entry.rules[0]:group=5m:indicator=1m");
    }

    [Fact]
    public void Validate_Passes_ForExpandedBreadth_PathsOperators_AndTimeframes()
    {
        var parser = new StrategyRuleParser();
        var validator = new StrategyDefinitionValidator();

        var snapshot = validator.Validate(parser.Parse(
            """
            {
              "schemaVersion": 2,
              "entry": {
                "operator": "all",
                "ruleId": "entry-root",
                "ruleType": "group",
                "timeframe": "30m",
                "weight": 1,
                "enabled": true,
                "rules": [
                  {
                    "ruleId": "entry-latency",
                    "ruleType": "data-quality",
                    "path": "indicator.latencySeconds",
                    "comparison": "between",
                    "value": "0..5",
                    "timeframe": "30m",
                    "weight": 10,
                    "enabled": true
                  },
                  {
                    "ruleId": "entry-source",
                    "ruleType": "data-quality",
                    "path": "indicator.source",
                    "comparison": "contains",
                    "value": "stream",
                    "timeframe": "30m",
                    "weight": 10,
                    "enabled": true
                  }
                ]
              }
            }
            """));

        Assert.True(snapshot.IsValid);
        Assert.Equal("Valid", snapshot.StatusCode);
        Assert.Equal(2, snapshot.EnabledRuleCount);
    }

    [Fact]
    public void Validate_FailsClosed_ForInvalidRangeShape()
    {
        var parser = new StrategyRuleParser();
        var validator = new StrategyDefinitionValidator();

        var snapshot = validator.Validate(parser.Parse(
            """
            {
              "schemaVersion": 2,
              "entry": {
                "path": "indicator.latencySeconds",
                "comparison": "between",
                "value": "fast",
                "ruleId": "entry-latency",
                "ruleType": "data-quality",
                "timeframe": "30m",
                "weight": 10,
                "enabled": true
              }
            }
            """));

        Assert.False(snapshot.IsValid);
        Assert.Contains(snapshot.FailureReasons, reason => reason.StartsWith("InvalidRangeOperand:entry:fast", StringComparison.Ordinal));
    }
    [Fact]
    public void Validate_ReturnsValidSnapshot_ForReferenceContractJson()
    {
        var parser = new StrategyRuleParser();
        var validator = new StrategyDefinitionValidator();

        var snapshot = validator.Validate(parser.Parse(StrategyContractJson.Reference));

        Assert.True(snapshot.IsValid);
        Assert.Equal("Valid", snapshot.StatusCode);
        Assert.Equal(12, snapshot.EnabledRuleCount);
    }

    [Fact]
    public void Validate_FailsClosed_WhenNoRuleGroupExists()
    {
        var parser = new StrategyRuleParser();
        var validator = new StrategyDefinitionValidator();

        var snapshot = validator.Validate(parser.Parse(
            """
            {
              "schemaVersion": 2,
              "entry": {
                "path": "context.mode",
                "comparison": "equals",
                "value": "Live",
                "ruleId": "entry-mode",
                "ruleType": "context",
                "timeframe": "1m",
                "weight": 10,
                "enabled": true
              }
            }
            """));

        Assert.False(snapshot.IsValid);
        Assert.Equal("MissingRuleGroup:entry-exit-risk-directional", snapshot.StatusCode);
    }

    [Fact]
    public void Validate_FailsClosed_ForUnsupportedMarketClosePath()
    {
        var parser = new StrategyRuleParser();
        var validator = new StrategyDefinitionValidator();

        var snapshot = validator.Validate(parser.Parse(
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
                "rules": [
                  {
                    "ruleId": "entry-market-close",
                    "ruleType": "context",
                    "path": "market.close",
                    "comparison": "greaterThan",
                    "value": 100,
                    "timeframe": "1m",
                    "weight": 10,
                    "enabled": true
                  }
                ]
              }
            }
            """));

        Assert.False(snapshot.IsValid);
        Assert.Contains(snapshot.FailureReasons, reason => reason.StartsWith("UnsupportedRulePath:entry.rules[0]:market.close", StringComparison.Ordinal));
    }
}
