using System.Linq;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Web.StrategyBuilderSupport;

namespace CoinBot.UnitTests.Web;

public sealed class StrategyBuilderRuntimeParityHelperTests
{
    [Fact]
    public void BuildSnapshot_Fallbacks_WhenEntryRootIsMissing()
    {
        var snapshot = BuildSnapshot(
            """
            {
              "schemaVersion": 2,
              "exit": {
                "operator": "all",
                "ruleId": "exit-root",
                "ruleType": "group",
                "rules": [
                  {
                    "ruleId": "exit-state",
                    "ruleType": "data-quality",
                    "path": "indicator.state",
                    "comparison": "equals",
                    "value": "Ready",
                    "enabled": true
                  }
                ]
              }
            }
            """);

        Assert.Equal(3, snapshot.LongRows.Count);
        Assert.Equal(3, snapshot.ShortRows.Count);
        Assert.All(snapshot.LongRows, row => Assert.Equal("Eksik", row.Status));
        Assert.All(snapshot.ShortRows, row => Assert.Equal("Eksik", row.Status));
        AssertHasMessage(snapshot, "warning", "Entry koku", "aktif entry kurali");
    }

    [Fact]
    public void BuildSnapshot_Fallbacks_WhenActiveEntryRuleIsMissing()
    {
        var snapshot = BuildSnapshot(
            """
            {
              "schemaVersion": 2,
              "entry": {
                "operator": "all",
                "ruleId": "entry-root",
                "ruleType": "group",
                "rules": [
                  {
                    "ruleId": "entry-rsi",
                    "ruleType": "rsi",
                    "path": "indicator.rsi.value",
                    "comparison": "lessThanOrEqual",
                    "value": 68,
                    "enabled": false
                  }
                ]
              }
            }
            """);

        Assert.Equal(3, snapshot.LongRows.Count);
        Assert.Equal(3, snapshot.ShortRows.Count);
        Assert.All(snapshot.LongRows, row => Assert.Equal("Eksik", row.Status));
        Assert.All(snapshot.ShortRows, row => Assert.Equal("Eksik", row.Status));
        AssertHasMessage(snapshot, "warning", "Entry koku", "aktif entry kurali");
    }

    [Fact]
    public void BuildSnapshot_Fallbacks_WhenActiveNumericRuleIsMissing()
    {
        var snapshot = BuildSnapshot(
            """
            {
              "schemaVersion": 2,
              "entry": {
                "operator": "all",
                "ruleId": "entry-root",
                "ruleType": "group",
                "rules": [
                  {
                    "ruleId": "entry-state",
                    "ruleType": "data-quality",
                    "path": "indicator.state",
                    "comparison": "equals",
                    "value": "Ready",
                    "enabled": true
                  }
                ]
              }
            }
            """);

        Assert.Equal(3, snapshot.LongRows.Count);
        Assert.Equal(3, snapshot.ShortRows.Count);
        Assert.All(snapshot.LongRows, row => Assert.Equal("Eksik", row.Status));
        Assert.All(snapshot.ShortRows, row => Assert.Equal("Eksik", row.Status));
        AssertHasMessage(snapshot, "warning", "aktif numeric kural bulunamadi");
    }

    [Fact]
    public void BuildSnapshot_ParityFlags_WhenRuntimeIsStricter()
    {
        var snapshot = BuildSnapshot(
            """
            {
              "schemaVersion": 2,
              "entry": {
                "operator": "all",
                "ruleId": "entry-root",
                "ruleType": "group",
                "rules": [
                  {
                    "ruleId": "entry-rsi",
                    "ruleType": "rsi",
                    "path": "indicator.rsi.value",
                    "comparison": "lessThanOrEqual",
                    "value": 75,
                    "enabled": true
                  }
                ]
              }
            }
            """);

        var row = GetRow(snapshot.LongRows, "RSI");
        Assert.Equal("Runtime stricter", row.Status);
        Assert.Contains("runtime gate", row.Summary, StringComparison.OrdinalIgnoreCase);
        AssertHasMessage(snapshot, "warning", "runtime gate", "blocked");
    }

    [Fact]
    public void BuildSnapshot_ParityFlags_WhenStrategyIsStricter()
    {
        var snapshot = BuildSnapshot(
            """
            {
              "schemaVersion": 2,
              "entry": {
                "operator": "all",
                "ruleId": "entry-root",
                "ruleType": "group",
                "rules": [
                  {
                    "ruleId": "entry-rsi",
                    "ruleType": "rsi",
                    "path": "indicator.rsi.value",
                    "comparison": "lessThanOrEqual",
                    "value": 60,
                    "enabled": true
                  }
                ]
              }
            }
            """);

        var row = GetRow(snapshot.LongRows, "RSI");
        Assert.Equal("Strategy stricter", row.Status);
        Assert.Contains("daha siki", row.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            snapshot.Messages,
            message => string.Equals(message.Tone, "warning", StringComparison.OrdinalIgnoreCase) &&
                       message.Message.Contains("runtime gate ile cakisiyor", StringComparison.OrdinalIgnoreCase));
        AssertHasMessage(snapshot, "warning", "MACD histogram", "tanimli degil");
        AssertHasMessage(snapshot, "warning", "Bollinger width", "tanimli degil");
    }

    [Fact]
    public void BuildSnapshot_ParityFlags_WhenStrategyAndRuntimeAreAligned()
    {
        var snapshot = BuildSnapshot(
            """
            {
              "schemaVersion": 2,
              "entry": {
                "operator": "all",
                "ruleId": "entry-root",
                "ruleType": "group",
                "rules": [
                  {
                    "ruleId": "entry-rsi",
                    "ruleType": "rsi",
                    "path": "indicator.rsi.value",
                    "comparison": "lessThanOrEqual",
                    "value": 68,
                    "enabled": true
                  }
                ]
              }
            }
            """);

        var row = GetRow(snapshot.LongRows, "RSI");
        Assert.Equal("Aligned", row.Status);
        Assert.Contains("ayni yone", row.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            snapshot.Messages,
            message => string.Equals(message.Tone, "warning", StringComparison.OrdinalIgnoreCase) &&
                       message.Message.Contains("runtime gate ile cakisiyor", StringComparison.OrdinalIgnoreCase));
        AssertHasMessage(snapshot, "warning", "MACD histogram", "tanimli degil");
        AssertHasMessage(snapshot, "warning", "Bollinger width", "tanimli degil");
    }

    [Fact]
    public void BuildSnapshot_UsesRuntimeDisabledState_WhenRuntimeGateIsOff()
    {
        var snapshot = BuildSnapshot(
            """
            {
              "schemaVersion": 2,
              "entry": {
                "operator": "all",
                "ruleId": "entry-root",
                "ruleType": "group",
                "rules": [
                  {
                    "ruleId": "entry-rsi",
                    "ruleType": "rsi",
                    "path": "indicator.rsi.value",
                    "comparison": "lessThanOrEqual",
                    "value": 68,
                    "enabled": true
                  }
                ]
              }
            }
            """,
            options =>
            {
                options.LongRegimeFilterEnabled = false;
                options.ShortRegimeFilterEnabled = false;
            });

        Assert.Equal(3, snapshot.LongRows.Count);
        Assert.Equal(3, snapshot.ShortRows.Count);
        Assert.All(snapshot.LongRows, row => Assert.Equal("Runtime disabled", row.Status));
        Assert.All(snapshot.ShortRows, row => Assert.Equal("Runtime disabled", row.Status));
        AssertHasMessage(snapshot, "success", "hizalandi");
    }

    [Fact]
    public void BuildSnapshot_KeepsMissingMetricOutputStable_WhenUnsupportedMetricExists()
    {
        var snapshot = BuildSnapshot(
            """
            {
              "schemaVersion": 2,
              "longEntry": {
                "operator": "all",
                "ruleId": "long-entry-root",
                "ruleType": "group",
                "rules": [
                  {
                    "ruleId": "entry-rsi",
                    "ruleType": "rsi",
                    "path": "indicator.rsi.value",
                    "comparison": "lessThanOrEqual",
                    "value": 68,
                    "enabled": true
                  },
                  {
                    "ruleId": "entry-volume",
                    "ruleType": "custom",
                    "path": "indicator.volume.spike",
                    "comparison": "greaterThan",
                    "value": 10,
                    "enabled": true
                  }
                ]
              }
            }
            """);

        Assert.Equal(3, snapshot.LongRows.Count);
        Assert.DoesNotContain(snapshot.LongRows, row => row.MetricLabel.Contains("volume", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Aligned", GetRow(snapshot.LongRows, "RSI").Status);
        Assert.Equal("Eksik", GetRow(snapshot.LongRows, "MACD histogram").Status);
        Assert.Equal("Eksik", GetRow(snapshot.LongRows, "Bollinger width %").Status);
        AssertHasMessage(snapshot, "warning", "MACD histogram", "tanimli degil");
        AssertHasMessage(snapshot, "warning", "Bollinger width", "tanimli degil");
    }

    [Fact]
    public void BuildSnapshot_UsesMappedMetricLabels_ForSupportedPaths()
    {
        var snapshot = BuildSnapshot(
            """
            {
              "schemaVersion": 2,
              "longEntry": {
                "operator": "all",
                "ruleId": "long-entry-root",
                "ruleType": "group",
                "rules": [
                  {
                    "ruleId": "entry-macd",
                    "ruleType": "macd",
                    "path": "indicator.macd.histogram",
                    "comparison": "greaterThanOrEqual",
                    "value": -0.005,
                    "enabled": true
                  },
                  {
                    "ruleId": "entry-bandwidth",
                    "ruleType": "bollinger",
                    "path": "indicator.bollinger.bandwidth",
                    "comparison": "greaterThanOrEqual",
                    "value": 0.07,
                    "enabled": true
                  }
                ]
              }
            }
            """);

        var macdRow = GetRow(snapshot.LongRows, "MACD histogram");
        var bollingerRow = GetRow(snapshot.LongRows, "Bollinger width %");

        Assert.Equal("MACD histogram", macdRow.MetricLabel);
        Assert.Equal("Bollinger width %", bollingerRow.MetricLabel);
        Assert.Contains("Value (MACD histogram)", macdRow.StrategyInputLabel, StringComparison.Ordinal);
        Assert.Contains("Value (Bollinger width %)", bollingerRow.StrategyInputLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("indicator.macd.histogram", macdRow.StrategyInputLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("indicator.bollinger.bandwidth", bollingerRow.StrategyInputLabel, StringComparison.OrdinalIgnoreCase);
    }

    private static StrategyBuilderExplainabilitySnapshot BuildSnapshot(
        string definitionJson,
        Action<BotExecutionPilotOptions>? configure = null)
    {
        var options = CreateRuntimeOptions();
        configure?.Invoke(options);
        return StrategyBuilderRuntimeParityHelper.BuildSnapshot(definitionJson, options);
    }

    private static BotExecutionPilotOptions CreateRuntimeOptions()
    {
        return new BotExecutionPilotOptions
        {
            LongRegimeFilterEnabled = true,
            ShortRegimeFilterEnabled = true,
            LongRegimeMaxEntryRsi = 68m,
            LongRegimeMinMacdHistogram = -0.005m,
            LongRegimeMinBollingerWidthPercentage = 0.07m,
            ShortRegimeMinEntryRsi = 32m,
            ShortRegimeMaxMacdHistogram = 0.005m,
            ShortRegimeMinBollingerWidthPercentage = 0.09m
        };
    }

    private static StrategyBuilderParityRow GetRow(
        IEnumerable<StrategyBuilderParityRow> rows,
        string metricLabel)
    {
        return Assert.Single(rows, row => string.Equals(row.MetricLabel, metricLabel, StringComparison.Ordinal));
    }

    private static void AssertHasMessage(
        StrategyBuilderExplainabilitySnapshot snapshot,
        string tone,
        params string[] requiredPhrases)
    {
        Assert.Contains(
            snapshot.Messages,
            message => string.Equals(message.Tone, tone, StringComparison.OrdinalIgnoreCase) &&
                       requiredPhrases.All(phrase =>
                           message.Message.Contains(phrase, StringComparison.OrdinalIgnoreCase)));
    }
}
