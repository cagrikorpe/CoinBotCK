using System.Globalization;
using CoinBot.Application.Abstractions.Strategies;

namespace CoinBot.Infrastructure.Strategies;

public sealed class StrategyEvaluatorService(IStrategyRuleParser parser) : IStrategyEvaluatorService
{
    public StrategyEvaluationResult Evaluate(string definitionJson, StrategyEvaluationContext context)
    {
        return Evaluate(parser.Parse(definitionJson), context);
    }

    public StrategyEvaluationResult Evaluate(StrategyRuleDocument document, StrategyEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        var entryResult = document.Entry is not null
            ? EvaluateNode(document.Entry, context)
            : null;
        var exitResult = document.Exit is not null
            ? EvaluateNode(document.Exit, context)
            : null;
        var riskResult = document.Risk is not null
            ? EvaluateNode(document.Risk, context)
            : null;

        return new StrategyEvaluationResult(
            HasEntryRules: document.Entry is not null,
            EntryMatched: entryResult?.Matched ?? false,
            HasExitRules: document.Exit is not null,
            ExitMatched: exitResult?.Matched ?? false,
            HasRiskRules: document.Risk is not null,
            RiskPassed: riskResult?.Matched ?? true,
            EntryRuleResult: entryResult?.Snapshot,
            ExitRuleResult: exitResult?.Snapshot,
            RiskRuleResult: riskResult?.Snapshot);
    }

    private static NodeEvaluation EvaluateNode(StrategyRuleNode node, StrategyEvaluationContext context)
    {
        return node switch
        {
            StrategyRuleGroup group => EvaluateGroup(group, context),
            StrategyRuleCondition condition => EvaluateCondition(condition, context),
            _ => throw new StrategyRuleEvaluationException($"Unsupported strategy rule node '{node.GetType().Name}'.")
        };
    }

    private static NodeEvaluation EvaluateGroup(StrategyRuleGroup group, StrategyEvaluationContext context)
    {
        var childEvaluations = group.Rules
            .Select(rule => EvaluateNode(rule, context))
            .ToArray();

        var matched = group.Operator switch
        {
            StrategyRuleGroupOperator.All => childEvaluations.All(rule => rule.Matched),
            StrategyRuleGroupOperator.Any => childEvaluations.Any(rule => rule.Matched),
            _ => throw new StrategyRuleEvaluationException($"Unsupported strategy rule group operator '{group.Operator}'.")
        };

        return new NodeEvaluation(
            matched,
            new StrategyRuleResultSnapshot(
                Matched: matched,
                GroupOperator: group.Operator,
                Path: null,
                Comparison: null,
                Operand: null,
                OperandKind: null,
                LeftValue: null,
                RightValue: null,
                Children: childEvaluations.Select(rule => rule.Snapshot).ToArray()));
    }

    private static NodeEvaluation EvaluateCondition(StrategyRuleCondition condition, StrategyEvaluationContext context)
    {
        var left = ResolvePathValue(condition.Path, context);
        var right = condition.Operand.Kind == StrategyRuleOperandKind.Path
            ? ResolvePathValue(condition.Operand.Value, context)
            : ResolveLiteralValue(condition.Operand);

        if (left.Kind != right.Kind)
        {
            throw new StrategyRuleEvaluationException(
                $"Strategy rule path '{condition.Path}' resolved to '{left.Kind}' but operand resolved to '{right.Kind}'.");
        }

        var matched = left.Kind switch
        {
            StrategyRuleOperandKind.Number => CompareNumbers(left.Value, right.Value, condition.Comparison),
            StrategyRuleOperandKind.String => CompareStrings(left.Value, right.Value, condition.Comparison),
            StrategyRuleOperandKind.Boolean => CompareBooleans(left.Value, right.Value, condition.Comparison),
            _ => throw new StrategyRuleEvaluationException($"Unsupported strategy rule operand kind '{left.Kind}'.")
        };

        return new NodeEvaluation(
            matched,
            new StrategyRuleResultSnapshot(
                Matched: matched,
                GroupOperator: null,
                Path: condition.Path,
                Comparison: condition.Comparison,
                Operand: condition.Operand.Value,
                OperandKind: condition.Operand.Kind,
                LeftValue: left.Value,
                RightValue: right.Value,
                Children: Array.Empty<StrategyRuleResultSnapshot>()));
    }

    private static ResolvedValue ResolvePathValue(string path, StrategyEvaluationContext context)
    {
        return NormalizePath(path) switch
        {
            "context.mode" => ResolvedValue.String(context.Mode.ToString()),
            "indicator.symbol" => ResolvedValue.String(context.IndicatorSnapshot.Symbol),
            "indicator.timeframe" => ResolvedValue.String(context.IndicatorSnapshot.Timeframe),
            "indicator.samplecount" => ResolvedValue.Number(context.IndicatorSnapshot.SampleCount),
            "indicator.requiredsamplecount" => ResolvedValue.Number(context.IndicatorSnapshot.RequiredSampleCount),
            "indicator.state" => ResolvedValue.String(context.IndicatorSnapshot.State.ToString()),
            "indicator.dataqualityreasoncode" => ResolvedValue.String(context.IndicatorSnapshot.DataQualityReasonCode.ToString()),
            "indicator.source" => ResolvedValue.String(context.IndicatorSnapshot.Source),
            "indicator.rsi.period" => ResolvedValue.Number(context.IndicatorSnapshot.Rsi.Period),
            "indicator.rsi.isready" => ResolvedValue.Boolean(context.IndicatorSnapshot.Rsi.IsReady),
            "indicator.rsi.value" => ResolvedValue.Number(
                context.IndicatorSnapshot.Rsi.Value
                ?? throw new StrategyRuleEvaluationException("Strategy rule path 'indicator.rsi.value' resolved to null.")),
            "indicator.macd.fastperiod" => ResolvedValue.Number(context.IndicatorSnapshot.Macd.FastPeriod),
            "indicator.macd.slowperiod" => ResolvedValue.Number(context.IndicatorSnapshot.Macd.SlowPeriod),
            "indicator.macd.signalperiod" => ResolvedValue.Number(context.IndicatorSnapshot.Macd.SignalPeriod),
            "indicator.macd.isready" => ResolvedValue.Boolean(context.IndicatorSnapshot.Macd.IsReady),
            "indicator.macd.macdline" => ResolvedValue.Number(
                context.IndicatorSnapshot.Macd.MacdLine
                ?? throw new StrategyRuleEvaluationException("Strategy rule path 'indicator.macd.macdLine' resolved to null.")),
            "indicator.macd.signalline" => ResolvedValue.Number(
                context.IndicatorSnapshot.Macd.SignalLine
                ?? throw new StrategyRuleEvaluationException("Strategy rule path 'indicator.macd.signalLine' resolved to null.")),
            "indicator.macd.histogram" => ResolvedValue.Number(
                context.IndicatorSnapshot.Macd.Histogram
                ?? throw new StrategyRuleEvaluationException("Strategy rule path 'indicator.macd.histogram' resolved to null.")),
            "indicator.bollinger.period" => ResolvedValue.Number(context.IndicatorSnapshot.Bollinger.Period),
            "indicator.bollinger.standarddeviationmultiplier" => ResolvedValue.Number(context.IndicatorSnapshot.Bollinger.StandardDeviationMultiplier),
            "indicator.bollinger.isready" => ResolvedValue.Boolean(context.IndicatorSnapshot.Bollinger.IsReady),
            "indicator.bollinger.middleband" => ResolvedValue.Number(
                context.IndicatorSnapshot.Bollinger.MiddleBand
                ?? throw new StrategyRuleEvaluationException("Strategy rule path 'indicator.bollinger.middleBand' resolved to null.")),
            "indicator.bollinger.upperband" => ResolvedValue.Number(
                context.IndicatorSnapshot.Bollinger.UpperBand
                ?? throw new StrategyRuleEvaluationException("Strategy rule path 'indicator.bollinger.upperBand' resolved to null.")),
            "indicator.bollinger.lowerband" => ResolvedValue.Number(
                context.IndicatorSnapshot.Bollinger.LowerBand
                ?? throw new StrategyRuleEvaluationException("Strategy rule path 'indicator.bollinger.lowerBand' resolved to null.")),
            "indicator.bollinger.standarddeviation" => ResolvedValue.Number(
                context.IndicatorSnapshot.Bollinger.StandardDeviation
                ?? throw new StrategyRuleEvaluationException("Strategy rule path 'indicator.bollinger.standardDeviation' resolved to null.")),
            _ => throw new StrategyRuleEvaluationException($"Strategy rule path '{path}' is not supported.")
        };
    }

    private static ResolvedValue ResolveLiteralValue(StrategyRuleOperand operand)
    {
        return operand.Kind switch
        {
            StrategyRuleOperandKind.Number => decimal.TryParse(operand.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var numericValue)
                ? ResolvedValue.Number(numericValue)
                : throw new StrategyRuleEvaluationException($"Strategy rule numeric literal '{operand.Value}' is invalid."),
            StrategyRuleOperandKind.String => ResolvedValue.String(operand.Value),
            StrategyRuleOperandKind.Boolean => bool.TryParse(operand.Value, out var booleanValue)
                ? ResolvedValue.Boolean(booleanValue)
                : throw new StrategyRuleEvaluationException($"Strategy rule boolean literal '{operand.Value}' is invalid."),
            _ => throw new StrategyRuleEvaluationException($"Strategy rule operand kind '{operand.Kind}' cannot be resolved as a literal.")
        };
    }

    private static bool CompareNumbers(string left, string right, StrategyRuleComparisonOperator comparison)
    {
        var leftValue = decimal.Parse(left, CultureInfo.InvariantCulture);
        var rightValue = decimal.Parse(right, CultureInfo.InvariantCulture);

        return comparison switch
        {
            StrategyRuleComparisonOperator.Equals => leftValue == rightValue,
            StrategyRuleComparisonOperator.NotEquals => leftValue != rightValue,
            StrategyRuleComparisonOperator.GreaterThan => leftValue > rightValue,
            StrategyRuleComparisonOperator.GreaterThanOrEqual => leftValue >= rightValue,
            StrategyRuleComparisonOperator.LessThan => leftValue < rightValue,
            StrategyRuleComparisonOperator.LessThanOrEqual => leftValue <= rightValue,
            _ => throw new StrategyRuleEvaluationException($"Unsupported numeric strategy comparison '{comparison}'.")
        };
    }

    private static bool CompareStrings(string left, string right, StrategyRuleComparisonOperator comparison)
    {
        return comparison switch
        {
            StrategyRuleComparisonOperator.Equals => string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
            StrategyRuleComparisonOperator.NotEquals => !string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
            _ => throw new StrategyRuleEvaluationException($"Unsupported string strategy comparison '{comparison}'.")
        };
    }

    private static bool CompareBooleans(string left, string right, StrategyRuleComparisonOperator comparison)
    {
        var leftValue = bool.Parse(left);
        var rightValue = bool.Parse(right);

        return comparison switch
        {
            StrategyRuleComparisonOperator.Equals => leftValue == rightValue,
            StrategyRuleComparisonOperator.NotEquals => leftValue != rightValue,
            _ => throw new StrategyRuleEvaluationException($"Unsupported boolean strategy comparison '{comparison}'.")
        };
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new StrategyRuleEvaluationException("Strategy rule path is required.");
        }

        return path.Trim().ToLowerInvariant();
    }

    private sealed record ResolvedValue(StrategyRuleOperandKind Kind, string Value)
    {
        public static ResolvedValue Boolean(bool value)
        {
            return new ResolvedValue(StrategyRuleOperandKind.Boolean, value.ToString());
        }

        public static ResolvedValue Number(decimal value)
        {
            return new ResolvedValue(StrategyRuleOperandKind.Number, value.ToString(CultureInfo.InvariantCulture));
        }

        public static ResolvedValue String(string value)
        {
            return new ResolvedValue(StrategyRuleOperandKind.String, value);
        }
    }

    private sealed record NodeEvaluation(bool Matched, StrategyRuleResultSnapshot Snapshot);
}
