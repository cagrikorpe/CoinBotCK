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
}
