using System.Globalization;
using CoinBot.Application.Abstractions.Strategies;

namespace CoinBot.Infrastructure.Strategies;

public sealed class StrategyEvaluatorService(
    IStrategyRuleParser parser,
    IStrategyDefinitionValidator? validator = null) : IStrategyEvaluatorService
{
    private readonly IStrategyDefinitionValidator validator = validator ?? new StrategyDefinitionValidator();

    public StrategyEvaluationResult Evaluate(string definitionJson, StrategyEvaluationContext context)
    {
        var document = parser.Parse(definitionJson);
        EnsureValid(document);
        return EvaluateDocument(document, context);
    }

    public StrategyEvaluationResult Evaluate(StrategyRuleDocument document, StrategyEvaluationContext context)
    {
        EnsureValid(document);
        return EvaluateDocument(document, context);
    }

    public StrategyEvaluationReportSnapshot EvaluateReport(StrategyEvaluationReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.EvaluationContext);

        var document = parser.Parse(request.DefinitionJson);
        EnsureValid(document);

        var evaluationResult = EvaluateDocument(document, request.EvaluationContext);
        var rootSnapshots = new[]
            {
                evaluationResult.EntryRuleResult,
                evaluationResult.ExitRuleResult,
                evaluationResult.RiskRuleResult
            }
            .Where(snapshot => snapshot is not null)
            .Select(snapshot => snapshot!)
            .ToArray();
        var leafRules = rootSnapshots
            .SelectMany(CollectLeafRules)
            .Where(rule => rule.Enabled)
            .ToArray();
        var totalWeight = leafRules.Sum(rule => rule.Weight);
        var matchedWeight = leafRules.Where(rule => rule.Matched).Sum(rule => rule.Weight);
        var aggregateScore = totalWeight <= 0m
            ? 0
            : Math.Clamp((int)Math.Round(matchedWeight / totalWeight * 100m, MidpointRounding.AwayFromZero), 0, 100);
        var passedRules = leafRules
            .Where(rule => rule.Matched)
            .Select(DescribeRuleResult)
            .ToArray();
        var failedRules = leafRules
            .Where(rule => !rule.Matched)
            .Select(DescribeRuleResult)
            .ToArray();
        var outcome = ResolveOutcome(evaluationResult);
        var summary = BuildExplainabilitySummary(
            request,
            document.Metadata,
            outcome,
            aggregateScore,
            passedRules,
            failedRules);

        return new StrategyEvaluationReportSnapshot(
            request.TradingStrategyId,
            request.TradingStrategyVersionId,
            request.StrategyVersionNumber,
            request.StrategyKey,
            request.StrategyDisplayName,
            document.Metadata?.TemplateKey,
            document.Metadata?.TemplateName,
            request.EvaluationContext.IndicatorSnapshot.Symbol,
            request.EvaluationContext.IndicatorSnapshot.Timeframe,
            NormalizeUtc(request.EvaluatedAtUtc),
            outcome,
            aggregateScore,
            passedRules.Length,
            failedRules.Length,
            evaluationResult,
            passedRules,
            failedRules,
            summary);
    }

    private StrategyEvaluationResult EvaluateDocument(StrategyRuleDocument document, StrategyEvaluationContext context)
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

    private void EnsureValid(StrategyRuleDocument document)
    {
        var validation = validator.Validate(document);
        if (!validation.IsValid)
        {
            throw new StrategyDefinitionValidationException(validation.StatusCode, validation.Summary, validation.FailureReasons);
        }
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
        var metadata = group.Metadata ?? StrategyRuleMetadata.Default;
        if (!metadata.Enabled)
        {
            return new NodeEvaluation(
                true,
                new StrategyRuleResultSnapshot(
                    Matched: true,
                    GroupOperator: group.Operator,
                    Path: null,
                    Comparison: null,
                    Operand: null,
                    OperandKind: null,
                    LeftValue: null,
                    RightValue: null,
                    Children: Array.Empty<StrategyRuleResultSnapshot>(),
                    RuleId: metadata.RuleId,
                    RuleType: metadata.RuleType,
                    Timeframe: metadata.Timeframe,
                    Weight: metadata.Weight,
                    Enabled: false,
                    Group: metadata.Group,
                    Reason: "Rule group is disabled and was skipped."));
        }

        if (!string.IsNullOrWhiteSpace(metadata.Timeframe) &&
            !string.Equals(metadata.Timeframe.Trim(), context.IndicatorSnapshot.Timeframe, StringComparison.OrdinalIgnoreCase))
        {
            return new NodeEvaluation(
                false,
                new StrategyRuleResultSnapshot(
                    Matched: false,
                    GroupOperator: group.Operator,
                    Path: null,
                    Comparison: null,
                    Operand: null,
                    OperandKind: null,
                    LeftValue: context.IndicatorSnapshot.Timeframe,
                    RightValue: metadata.Timeframe.Trim(),
                    Children: Array.Empty<StrategyRuleResultSnapshot>(),
                    RuleId: metadata.RuleId,
                    RuleType: metadata.RuleType,
                    Timeframe: metadata.Timeframe,
                    Weight: metadata.Weight,
                    Enabled: true,
                    Group: metadata.Group,
                    Reason: $"Rule group timeframe '{metadata.Timeframe.Trim()}' does not match indicator timeframe '{context.IndicatorSnapshot.Timeframe}'."));
        }

        var childEvaluations = group.Rules
            .Select(rule => EvaluateNode(rule, context))
            .ToArray();

        var matched = group.Operator switch
        {
            StrategyRuleGroupOperator.All => childEvaluations.All(rule => rule.Matched),
            StrategyRuleGroupOperator.Any => childEvaluations.Any(rule => rule.Matched),
            _ => throw new StrategyRuleEvaluationException($"Unsupported strategy rule group operator '{group.Operator}'.")
        };
        var matchedChildCount = childEvaluations.Count(rule => rule.Matched);
        var reason = group.Operator == StrategyRuleGroupOperator.All
            ? $"ALL group matched {matchedChildCount}/{childEvaluations.Length} child rules."
            : $"ANY group matched {matchedChildCount}/{childEvaluations.Length} child rules.";

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
                Children: childEvaluations.Select(rule => rule.Snapshot).ToArray(),
                RuleId: metadata.RuleId,
                RuleType: metadata.RuleType,
                Timeframe: metadata.Timeframe,
                Weight: metadata.Weight,
                Enabled: true,
                Group: metadata.Group,
                Reason: reason));
    }

    private static NodeEvaluation EvaluateCondition(StrategyRuleCondition condition, StrategyEvaluationContext context)
    {
        var metadata = condition.Metadata ?? StrategyRuleMetadata.Default;
        if (!metadata.Enabled)
        {
            return new NodeEvaluation(
                true,
                new StrategyRuleResultSnapshot(
                    Matched: true,
                    GroupOperator: null,
                    Path: condition.Path,
                    Comparison: condition.Comparison,
                    Operand: condition.Operand.Value,
                    OperandKind: condition.Operand.Kind,
                    LeftValue: null,
                    RightValue: null,
                    Children: Array.Empty<StrategyRuleResultSnapshot>(),
                    RuleId: metadata.RuleId,
                    RuleType: metadata.RuleType,
                    Timeframe: metadata.Timeframe,
                    Weight: metadata.Weight,
                    Enabled: false,
                    Group: metadata.Group,
                    Reason: "Rule is disabled and was skipped."));
        }

        if (!string.IsNullOrWhiteSpace(metadata.Timeframe) &&
            !string.Equals(metadata.Timeframe.Trim(), context.IndicatorSnapshot.Timeframe, StringComparison.OrdinalIgnoreCase))
        {
            return new NodeEvaluation(
                false,
                new StrategyRuleResultSnapshot(
                    Matched: false,
                    GroupOperator: null,
                    Path: condition.Path,
                    Comparison: condition.Comparison,
                    Operand: condition.Operand.Value,
                    OperandKind: condition.Operand.Kind,
                    LeftValue: context.IndicatorSnapshot.Timeframe,
                    RightValue: metadata.Timeframe.Trim(),
                    Children: Array.Empty<StrategyRuleResultSnapshot>(),
                    RuleId: metadata.RuleId,
                    RuleType: metadata.RuleType,
                    Timeframe: metadata.Timeframe,
                    Weight: metadata.Weight,
                    Enabled: true,
                    Group: metadata.Group,
                    Reason: $"Rule timeframe '{metadata.Timeframe.Trim()}' does not match indicator timeframe '{context.IndicatorSnapshot.Timeframe}'."));
        }

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
        var reason = matched
            ? $"Matched {condition.Path} {condition.Comparison} {right.Value}; left={left.Value}."
            : $"Failed {condition.Path} {condition.Comparison} {right.Value}; left={left.Value}.";

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
                Children: Array.Empty<StrategyRuleResultSnapshot>(),
                RuleId: metadata.RuleId,
                RuleType: metadata.RuleType,
                Timeframe: metadata.Timeframe,
                Weight: metadata.Weight,
                Enabled: true,
                Group: metadata.Group,
                Reason: reason));
    }

    private static IEnumerable<StrategyRuleResultSnapshot> CollectLeafRules(StrategyRuleResultSnapshot snapshot)
    {
        if (snapshot.Children.Count == 0)
        {
            yield return snapshot;
            yield break;
        }

        foreach (var child in snapshot.Children.SelectMany(CollectLeafRules))
        {
            yield return child;
        }
    }

    private static string DescribeRuleResult(StrategyRuleResultSnapshot snapshot)
    {
        var label = !string.IsNullOrWhiteSpace(snapshot.RuleId)
            ? snapshot.RuleId!.Trim()
            : !string.IsNullOrWhiteSpace(snapshot.Path)
                ? snapshot.Path!.Trim()
                : snapshot.GroupOperator?.ToString() ?? "rule";
        var ruleType = string.IsNullOrWhiteSpace(snapshot.RuleType) ? "rule" : snapshot.RuleType!.Trim();
        var timeframe = string.IsNullOrWhiteSpace(snapshot.Timeframe) ? "n/a" : snapshot.Timeframe!.Trim();
        var reason = string.IsNullOrWhiteSpace(snapshot.Reason) ? "No reason." : snapshot.Reason!.Trim();

        return FormattableString.Invariant(
            $"{label} [{ruleType}/{timeframe}] {(snapshot.Matched ? "PASS" : "FAIL")} w={snapshot.Weight:0.####} :: {reason}");
    }

    private static string ResolveOutcome(StrategyEvaluationResult result)
    {
        if (result.HasRiskRules && !result.RiskPassed)
        {
            return "RiskVetoed";
        }

        if (result.HasEntryRules && result.EntryMatched)
        {
            return "EntryMatched";
        }

        if (result.HasExitRules && result.ExitMatched)
        {
            return "ExitMatched";
        }

        return "NoSignalCandidate";
    }

    private static string BuildExplainabilitySummary(
        StrategyEvaluationReportRequest request,
        StrategyDefinitionMetadata? metadata,
        string outcome,
        int aggregateScore,
        IReadOnlyCollection<string> passedRules,
        IReadOnlyCollection<string> failedRules)
    {
        var templateLabel = string.IsNullOrWhiteSpace(metadata?.TemplateName)
            ? "custom"
            : metadata.TemplateName!.Trim();
        var failedText = failedRules.Count == 0 ? "none" : string.Join(" | ", failedRules.Take(2));
        var passedText = passedRules.Count == 0 ? "none" : string.Join(" | ", passedRules.Take(2));

        return FormattableString.Invariant(
            $"Strategy={request.StrategyKey}; Template={templateLabel}; Symbol={request.EvaluationContext.IndicatorSnapshot.Symbol}; Timeframe={request.EvaluationContext.IndicatorSnapshot.Timeframe}; Outcome={outcome}; Score={aggregateScore}; Passed={passedRules.Count}; Failed={failedRules.Count}; TopPassed={passedText}; TopFailed={failedText}");
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

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
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
