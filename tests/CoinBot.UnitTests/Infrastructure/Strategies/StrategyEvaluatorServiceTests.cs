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
                "path": "indicator.rsi.value",
                "comparison": "lessThan",
                "value": 30
              }
            }
            """,
            context));

        Assert.Contains("resolved to null", exception.Message, StringComparison.Ordinal);
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
