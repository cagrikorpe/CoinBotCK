namespace CoinBot.UnitTests.Infrastructure.Strategies;

internal static class StrategyContractJson
{
    public const string Reference =
        """
        {
          "schemaVersion": 2,
          "metadata": {
            "templateKey": "bollinger-rsi-reversal",
            "templateName": "Bollinger RSI Reversal",
            "templateRevisionNumber": 1,
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
                "ruleId": "entry-state-ready",
                "ruleType": "data-quality",
                "path": "indicator.state",
                "comparison": "equals",
                "value": "Ready",
                "timeframe": "1m",
                "weight": 10,
                "enabled": true,
                "group": "entry"
              },
              {
                "ruleId": "entry-sample-count",
                "ruleType": "data-quality",
                "path": "indicator.sampleCount",
                "comparison": "greaterThanOrEqual",
                "value": 34,
                "timeframe": "1m",
                "weight": 10,
                "enabled": true,
                "group": "entry"
              },
              {
                "ruleId": "entry-rsi-ready",
                "ruleType": "rsi",
                "path": "indicator.rsi.isReady",
                "comparison": "equals",
                "value": true,
                "timeframe": "1m",
                "weight": 10,
                "enabled": true,
                "group": "entry"
              },
              {
                "ruleId": "entry-rsi-oversold",
                "ruleType": "rsi",
                "path": "indicator.rsi.value",
                "comparison": "lessThanOrEqual",
                "value": 30,
                "timeframe": "1m",
                "weight": 30,
                "enabled": true,
                "group": "entry"
              },
              {
                "ruleId": "entry-bollinger-ready",
                "ruleType": "bollinger",
                "path": "indicator.bollinger.isReady",
                "comparison": "equals",
                "value": true,
                "timeframe": "1m",
                "weight": 10,
                "enabled": true,
                "group": "entry"
              },
              {
                "ruleId": "entry-bandwidth-low",
                "ruleType": "bollinger",
                "path": "indicator.bollinger.bandWidth",
                "comparison": "lessThanOrEqual",
                "value": 5,
                "timeframe": "1m",
                "weight": 30,
                "enabled": true,
                "group": "entry"
              }
            ]
          },
          "exit": {
            "operator": "any",
            "ruleId": "exit-root",
            "ruleType": "group",
            "timeframe": "1m",
            "weight": 1,
            "enabled": true,
            "group": "exit",
            "rules": [
              {
                "ruleId": "exit-rsi-reset",
                "ruleType": "rsi",
                "path": "indicator.rsi.value",
                "comparison": "greaterThanOrEqual",
                "value": 55,
                "timeframe": "1m",
                "weight": 60,
                "enabled": true,
                "group": "exit"
              },
              {
                "ruleId": "exit-macd-spread-negative",
                "ruleType": "macd",
                "path": "indicator.macd.spread",
                "comparison": "lessThanOrEqual",
                "value": 0,
                "timeframe": "1m",
                "weight": 40,
                "enabled": true,
                "group": "exit"
              }
            ]
          },
          "risk": {
            "operator": "all",
            "ruleId": "risk-root",
            "ruleType": "group",
            "timeframe": "1m",
            "weight": 1,
            "enabled": true,
            "group": "risk",
            "rules": [
              {
                "ruleId": "risk-state-ready",
                "ruleType": "data-quality",
                "path": "indicator.state",
                "comparison": "equals",
                "value": "Ready",
                "timeframe": "1m",
                "weight": 20,
                "enabled": true,
                "group": "risk"
              },
              {
                "ruleId": "risk-sample-count",
                "ruleType": "data-quality",
                "path": "indicator.sampleCount",
                "comparison": "greaterThanOrEqual",
                "value": 34,
                "timeframe": "1m",
                "weight": 20,
                "enabled": true,
                "group": "risk"
              },
              {
                "ruleId": "risk-coverage",
                "ruleType": "data-quality",
                "path": "indicator.sampleCoveragePercent",
                "comparison": "greaterThanOrEqual",
                "value": 95,
                "timeframe": "1m",
                "weight": 30,
                "enabled": true,
                "group": "risk"
              },
              {
                "ruleId": "risk-latency",
                "ruleType": "data-quality",
                "path": "indicator.latencySeconds",
                "comparison": "lessThanOrEqual",
                "value": 2,
                "timeframe": "1m",
                "weight": 30,
                "enabled": true,
                "group": "risk"
              }
            ]
          }
        }
        """;
}