using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Strategies;

namespace CoinBot.UnitTests.Infrastructure.Strategies;

public sealed class StrategyEvaluatorServiceTests
{
    [Fact]
    public void Evaluate_UsesDemoLiveContext_AndIndicatorPathComparisons()
    {
        var parser = new StrategyRuleParser();
        var service = new StrategyEvaluatorService(parser);
        var context = CreateContext(ExecutionEnvironment.Demo);

        var result = service.Evaluate(
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
                  },
                  {
                    "path": "indicator.macd.macdLine",
                    "comparison": "greaterThan",
                    "valuePath": "indicator.macd.signalLine"
                  }
                ]
              },
              "risk": {
                "operator": "all",
                "rules": [
                  {
                    "path": "indicator.sampleCount",
                    "comparison": "greaterThanOrEqual",
                    "value": 100
                  },
                  {
                    "path": "indicator.state",
                    "comparison": "equals",
                    "value": "Ready"
                  }
                ]
              }
            }
            """,
            context);

        Assert.True(result.HasEntryRules);
        Assert.True(result.EntryMatched);
        Assert.True(result.HasRiskRules);
        Assert.True(result.RiskPassed);
        Assert.False(result.HasExitRules);
        Assert.False(result.ExitMatched);
        Assert.NotNull(result.EntryRuleResult);
        Assert.NotNull(result.RiskRuleResult);
        var entryChildren = result.EntryRuleResult!.Children.ToArray();
        var riskChildren = result.RiskRuleResult!.Children.ToArray();

        Assert.Equal(StrategyRuleGroupOperator.All, result.EntryRuleResult.GroupOperator);
        Assert.Equal(3, entryChildren.Length);
        Assert.Equal("context.mode", entryChildren[0].Path);
        Assert.Equal("Demo", entryChildren[0].RightValue);
        Assert.Equal("indicator.sampleCount", riskChildren[0].Path);
    }

    [Fact]
    public void Evaluate_FailsClosed_WhenRuleReferencesUnavailableIndicatorValue()
    {
        var parser = new StrategyRuleParser();
        var service = new StrategyEvaluatorService(parser);
        var context = CreateContext(ExecutionEnvironment.Live) with
        {
            IndicatorSnapshot = CreateIndicatorSnapshot(rsiValue: null)
        };

        var exception = Assert.Throws<StrategyRuleEvaluationException>(() => service.Evaluate(
            """
            {
              "schemaVersion": 1,
              "entry": {
                "operator": "all",
                "ruleType": "group",
                "rules": [
                  {
                    "path": "indicator.rsi.value",
                    "comparison": "lessThan",
                    "value": 30
                  }
                ]
              }
            }
            """,
            context));

        Assert.Contains("resolved to null", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateReport_ReturnsDeterministicScore_AndRuleLevelExplainability()
    {
        var parser = new StrategyRuleParser();
        var service = new StrategyEvaluatorService(parser, new StrategyDefinitionValidator());
        var context = CreateContext(ExecutionEnvironment.Live);
        var request = new StrategyEvaluationReportRequest(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            3,
            "scanner-template",
            "Scanner Template",
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
                "rules": [
                  {
                    "ruleId": "entry-mode",
                    "ruleType": "context",
                    "path": "context.mode",
                    "comparison": "equals",
                    "value": "Live",
                    "timeframe": "1m",
                    "weight": 20,
                    "enabled": true
                  },
                  {
                    "ruleId": "entry-rsi",
                    "ruleType": "rsi",
                    "path": "indicator.rsi.value",
                    "comparison": "lessThanOrEqual",
                    "value": 20,
                    "timeframe": "1m",
                    "weight": 80,
                    "enabled": true
                  }
                ]
              },
              "risk": {
                "path": "indicator.sampleCount",
                "comparison": "greaterThanOrEqual",
                "value": 100,
                "ruleId": "risk-sample",
                "ruleType": "data-quality",
                "timeframe": "1m",
                "weight": 20,
                "enabled": true
              }
            }
            """,
            context,
            new DateTime(2026, 4, 3, 10, 0, 0, DateTimeKind.Utc));

        var firstReport = service.EvaluateReport(request);
        var secondReport = service.EvaluateReport(request);

        Assert.Equal("rsi-reversal", firstReport.TemplateKey);
        Assert.Equal("RSI Reversal", firstReport.TemplateName);
        Assert.Equal("BTCUSDT", firstReport.Symbol);
        Assert.Equal("1m", firstReport.Timeframe);
        Assert.Equal("NoSignalCandidate", firstReport.Outcome);
        Assert.Equal(33, firstReport.AggregateScore);
        Assert.Equal(2, firstReport.PassedRuleCount);
        Assert.Equal(1, firstReport.FailedRuleCount);
        Assert.Contains(firstReport.PassedRules, rule => rule.Contains("entry-mode", StringComparison.Ordinal));
        Assert.Contains("entry-rsi", firstReport.FailedRules.Single(), StringComparison.Ordinal);
        Assert.Contains("Outcome=NoSignalCandidate", firstReport.ExplainabilitySummary, StringComparison.Ordinal);
        Assert.Equal(firstReport.Outcome, secondReport.Outcome);
        Assert.Equal(firstReport.AggregateScore, secondReport.AggregateScore);
        Assert.Equal(firstReport.PassedRules, secondReport.PassedRules);
        Assert.Equal(firstReport.FailedRules, secondReport.FailedRules);
        Assert.Equal(firstReport.ExplainabilitySummary, secondReport.ExplainabilitySummary);
        Assert.Equal(firstReport.RuleEvaluation.HasEntryRules, secondReport.RuleEvaluation.HasEntryRules);
        Assert.Equal(firstReport.RuleEvaluation.EntryMatched, secondReport.RuleEvaluation.EntryMatched);
        Assert.Equal(firstReport.RuleEvaluation.HasRiskRules, secondReport.RuleEvaluation.HasRiskRules);
        Assert.Equal(firstReport.RuleEvaluation.RiskPassed, secondReport.RuleEvaluation.RiskPassed);
        Assert.Equal(firstReport.RuleEvaluation.EntryRuleResult?.Reason, secondReport.RuleEvaluation.EntryRuleResult?.Reason);
        Assert.Equal(firstReport.RuleEvaluation.RiskRuleResult?.Reason, secondReport.RuleEvaluation.RiskRuleResult?.Reason);
    }

    [Fact]
    public void Evaluate_RejectsInvalidStrategyDefinition_BeforeRuleEvaluation()
    {
        var service = new StrategyEvaluatorService(new StrategyRuleParser(), new StrategyDefinitionValidator());

        var exception = Assert.Throws<StrategyDefinitionValidationException>(() => service.Evaluate(
            """
            {
              "schemaVersion": 2,
              "entry": {
                "operator": "all",
                "rules": [
                  {
                    "ruleId": "dup",
                    "path": "indicator.rsi.value",
                    "comparison": "lessThanOrEqual",
                    "value": 20,
                    "weight": 20,
                    "enabled": true
                  },
                  {
                    "ruleId": "dup",
                    "path": "indicator.sampleCount",
                    "comparison": "greaterThanOrEqual",
                    "value": 100,
                    "weight": 20,
                    "enabled": true
                  }
                ]
              }
            }
            """,
            CreateContext(ExecutionEnvironment.Live)));

        Assert.Equal("DuplicateRuleId:entry.rules[1]:dup", exception.StatusCode);
    }

    [Fact]
    public void Evaluate_NestedAnyAllGroups_RemainsDeterministic_AcrossRuns()
    {
        var parser = new StrategyRuleParser();
        var service = new StrategyEvaluatorService(parser, new StrategyDefinitionValidator());
        var context = CreateContext(ExecutionEnvironment.Live);
        const string definitionJson =
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
                    "operator": "any",
                    "ruleId": "entry-any",
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
                        "weight": 30,
                        "enabled": true
                      },
                      {
                        "ruleId": "entry-macd",
                        "ruleType": "macd",
                        "path": "indicator.macd.macdLine",
                        "comparison": "greaterThan",
                        "valuePath": "indicator.macd.signalLine",
                        "timeframe": "1m",
                        "weight": 70,
                        "enabled": true
                      }
                    ]
                  },
                  {
                    "ruleId": "entry-mode",
                    "ruleType": "context",
                    "path": "context.mode",
                    "comparison": "equals",
                    "value": "Live",
                    "timeframe": "1m",
                    "weight": 10,
                    "enabled": true
                  }
                ]
              }
            }
            """;

        var firstResult = service.Evaluate(definitionJson, context);
        var secondResult = service.Evaluate(definitionJson, context);

        Assert.True(firstResult.EntryMatched);
        Assert.True(secondResult.EntryMatched);
        Assert.Equal(firstResult.EntryRuleResult?.Reason, secondResult.EntryRuleResult?.Reason);
        Assert.Equal(firstResult.EntryRuleResult?.Children.Count, secondResult.EntryRuleResult?.Children.Count);
        Assert.Equal(
            firstResult.EntryRuleResult?.Children.Select(child => child.Reason).ToArray(),
            secondResult.EntryRuleResult?.Children.Select(child => child.Reason).ToArray());
    }

    [Fact]
    public void Evaluate_SupportsExpandedBreadth_ForLatencyRange_StringContains_AndDerivedIndicatorPaths()
    {
        var parser = new StrategyRuleParser();
        var service = new StrategyEvaluatorService(parser, new StrategyDefinitionValidator());
        var context = CreateContext(ExecutionEnvironment.Live) with
        {
            IndicatorSnapshot = CreateIndicatorSnapshot(rsiValue: 28m) with
            {
                Timeframe = "30m",
                ReceivedAtUtc = new DateTime(2026, 3, 22, 12, 1, 3, DateTimeKind.Utc),
                Source = "scanner-stream"
            }
        };

        var result = service.Evaluate(
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
                    "ruleId": "latency-window",
                    "ruleType": "data-quality",
                    "path": "indicator.latencySeconds",
                    "comparison": "between",
                    "value": "0..5",
                    "timeframe": "30m",
                    "weight": 10,
                    "enabled": true
                  },
                  {
                    "ruleId": "source-contains-stream",
                    "ruleType": "data-quality",
                    "path": "indicator.source",
                    "comparison": "contains",
                    "value": "stream",
                    "timeframe": "30m",
                    "weight": 10,
                    "enabled": true
                  },
                  {
                    "ruleId": "macd-spread-positive",
                    "ruleType": "macd",
                    "path": "indicator.macd.spread",
                    "comparison": "greaterThan",
                    "value": 0,
                    "timeframe": "30m",
                    "weight": 10,
                    "enabled": true
                  },
                  {
                    "ruleId": "bandwidth-max-five-percent",
                    "ruleType": "bollinger",
                    "path": "indicator.bollinger.bandWidth",
                    "comparison": "lessThanOrEqual",
                    "value": 5,
                    "timeframe": "30m",
                    "weight": 10,
                    "enabled": true
                  }
                ]
              }
            }
            """,
            context);

        Assert.True(result.EntryMatched);
        Assert.All(result.EntryRuleResult!.Children, child => Assert.True(child.Matched));
        Assert.Contains(result.EntryRuleResult.Children, child => child.Path == "indicator.latencySeconds" && child.LeftValue == "3");
        Assert.Contains(result.EntryRuleResult.Children, child => child.Path == "indicator.macd.spread" && child.LeftValue == "0.3");
        Assert.Contains(result.EntryRuleResult.Children, child => child.Path == "indicator.bollinger.bandWidth" && child.LeftValue == "1.612903");
    }

    private static StrategyEvaluationContext CreateContext(ExecutionEnvironment mode)
    {
        return new StrategyEvaluationContext(mode, CreateIndicatorSnapshot(rsiValue: 28m));
    }

    private static StrategyIndicatorSnapshot CreateIndicatorSnapshot(decimal? rsiValue)
    {
        return new StrategyIndicatorSnapshot(
            Symbol: "BTCUSDT",
            Timeframe: "1m",
            OpenTimeUtc: new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
            CloseTimeUtc: new DateTime(2026, 3, 22, 12, 1, 0, DateTimeKind.Utc),
            ReceivedAtUtc: new DateTime(2026, 3, 22, 12, 1, 1, DateTimeKind.Utc),
            SampleCount: 120,
            RequiredSampleCount: 120,
            State: IndicatorDataState.Ready,
            DataQualityReasonCode: DegradedModeReasonCode.None,
            Rsi: new RelativeStrengthIndexSnapshot(14, IsReady: true, Value: rsiValue),
            Macd: new MovingAverageConvergenceDivergenceSnapshot(
                12,
                26,
                9,
                IsReady: true,
                MacdLine: 1.4m,
                SignalLine: 1.1m,
                Histogram: 0.3m),
            Bollinger: new BollingerBandsSnapshot(
                20,
                2m,
                IsReady: true,
                MiddleBand: 62000m,
                UpperBand: 62500m,
                LowerBand: 61500m,
                StandardDeviation: 250m),
            Source: "UnitTest");
    }
}



