using System.Globalization;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Strategies;

namespace CoinBot.Web.StrategyBuilderSupport;

public static class StrategyBuilderRuntimeParityHelper
{
    public static StrategyBuilderRuntimeConfig BuildRuntimeConfig(BotExecutionPilotOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new StrategyBuilderRuntimeConfig(
            BuildDirectionConfig(options, StrategyTradeDirection.Long),
            BuildDirectionConfig(options, StrategyTradeDirection.Short));
    }

    public static StrategyBuilderExplainabilitySnapshot BuildSnapshot(string? definitionJson, BotExecutionPilotOptions options)
    {
        var runtimeConfig = BuildRuntimeConfig(options);
        var messages = new List<StrategyBuilderExplainabilityMessage>();
        var longRows = new List<StrategyBuilderParityRow>();
        var shortRows = new List<StrategyBuilderParityRow>();

        if (string.IsNullOrWhiteSpace(definitionJson))
        {
            messages.Add(new StrategyBuilderExplainabilityMessage(
                "warning",
                "Definition JSON bulunamadi. Runtime gate parity yalniz threshold ozeti ile gosteriliyor."));

            PopulateMissingRows(longRows, runtimeConfig.Long, "Long");
            PopulateMissingRows(shortRows, runtimeConfig.Short, "Short");
            return new StrategyBuilderExplainabilitySnapshot(runtimeConfig, messages, longRows, shortRows);
        }

        StrategyRuleDocument document;
        try
        {
            document = new StrategyRuleParser().Parse(definitionJson);
        }
        catch (StrategyRuleParseException)
        {
            messages.Add(new StrategyBuilderExplainabilityMessage(
                "danger",
                "Definition JSON cozumlenemedi. Runtime gate parity yalniz threshold ozeti ile gosteriliyor."));

            PopulateMissingRows(longRows, runtimeConfig.Long, "Long");
            PopulateMissingRows(shortRows, runtimeConfig.Short, "Short");
            return new StrategyBuilderExplainabilitySnapshot(runtimeConfig, messages, longRows, shortRows);
        }

        var sharedEntryRefs = CollectConditionRefs(document.Entry, "Entry");
        var longEntryRefs = sharedEntryRefs.Concat(CollectConditionRefs(document.LongEntry, "Long Entry")).ToArray();
        var shortEntryRefs = sharedEntryRefs.Concat(CollectConditionRefs(document.ShortEntry, "Short Entry")).ToArray();

        if (longEntryRefs.Length == 0 && shortEntryRefs.Length == 0)
        {
            messages.Add(new StrategyBuilderExplainabilityMessage(
                "warning",
                "Entry koku veya aktif entry kurali bulunamadi. Bu durumda 'neden entry yok' cevabi dogrudan strategy giris tarafindadir."));
        }

        AppendDirectionRows(longRows, messages, "Long", runtimeConfig.Long, longEntryRefs);
        AppendDirectionRows(shortRows, messages, "Short", runtimeConfig.Short, shortEntryRefs);

        if (messages.Count == 0)
        {
            messages.Add(new StrategyBuilderExplainabilityMessage(
                "success",
                "Strategy threshold'lari ile runtime gate threshold'lari ayni ekranda hizalandi. Bariz bir parity cakisimi bulunmadi."));
        }

        return new StrategyBuilderExplainabilitySnapshot(runtimeConfig, messages, longRows, shortRows);
    }

    private static StrategyBuilderDirectionRuntimeConfig BuildDirectionConfig(BotExecutionPilotOptions options, StrategyTradeDirection direction)
    {
        var enabled = options.IsRegimeAwareEntryDisciplineEnabled(direction);
        var rsiThreshold = options.ResolveRegimeMaxEntryRsi(direction);
        var macdThreshold = options.ResolveRegimeMacdThreshold(direction);
        var bollingerWidthThreshold = options.ResolveRegimeMinBollingerWidthPercentage(direction);
        var summary = options.BuildRegimeThresholdSummary(direction);

        return new StrategyBuilderDirectionRuntimeConfig(
            direction.ToString(),
            enabled,
            rsiThreshold,
            macdThreshold,
            bollingerWidthThreshold,
            summary);
    }

    private static void PopulateMissingRows(
        List<StrategyBuilderParityRow> rows,
        StrategyBuilderDirectionRuntimeConfig runtime,
        string directionLabel)
    {
        rows.Add(BuildMissingRow(directionLabel, "RSI", runtime, BuildRuntimeDisplay(directionLabel, "RSI", runtime)));
        rows.Add(BuildMissingRow(directionLabel, "MACD histogram", runtime, BuildRuntimeDisplay(directionLabel, "MACD histogram", runtime)));
        rows.Add(BuildMissingRow(directionLabel, "Bollinger width %", runtime, BuildRuntimeDisplay(directionLabel, "Bollinger width %", runtime)));
    }

    private static void AppendDirectionRows(
        List<StrategyBuilderParityRow> rows,
        List<StrategyBuilderExplainabilityMessage> messages,
        string directionLabel,
        StrategyBuilderDirectionRuntimeConfig runtime,
        IReadOnlyCollection<StrategyConditionRef> refs)
    {
        if (refs.Count == 0)
        {
            messages.Add(new StrategyBuilderExplainabilityMessage(
                "warning",
                $"{directionLabel} entry tarafinda aktif numeric kural bulunamadi. Runtime gate block ozetleri strategy inputlarindan bagimsiz gorunebilir."));
            PopulateMissingRows(rows, runtime, directionLabel);
            return;
        }

        EvaluateAndAppendMetric(rows, messages, directionLabel, runtime, refs, MetricSpec.LongRsi, MetricSpec.ShortRsi);
        EvaluateAndAppendMetric(rows, messages, directionLabel, runtime, refs, MetricSpec.LongMacdHistogram, MetricSpec.ShortMacdHistogram);
        EvaluateAndAppendMetric(rows, messages, directionLabel, runtime, refs, MetricSpec.LongBollingerWidth, MetricSpec.ShortBollingerWidth);
    }

    private static void EvaluateAndAppendMetric(
        List<StrategyBuilderParityRow> rows,
        List<StrategyBuilderExplainabilityMessage> messages,
        string directionLabel,
        StrategyBuilderDirectionRuntimeConfig runtime,
        IReadOnlyCollection<StrategyConditionRef> refs,
        MetricSpec longSpec,
        MetricSpec shortSpec)
    {
        var spec = string.Equals(directionLabel, "Short", StringComparison.OrdinalIgnoreCase)
            ? shortSpec
            : longSpec;

        var runtimeDisplay = BuildRuntimeDisplay(directionLabel, spec.Label, runtime);
        var strategyRef = refs.FirstOrDefault(reference => spec.IsMatch(reference));
        if (!runtime.Enabled)
        {
            rows.Add(new StrategyBuilderParityRow(
                directionLabel,
                spec.Key,
                spec.Label,
                strategyRef?.InputLabel ?? $"{directionLabel} / {spec.Label} inputu",
                strategyRef is null ? "Tanimsiz" : strategyRef.StrategyDisplay,
                runtimeDisplay,
                "Runtime disabled",
                $"{directionLabel} runtime gate devre disi; save sonrasi bu metric runtime block uretmez."));
            return;
        }

        if (strategyRef is null)
        {
            rows.Add(BuildMissingRow(directionLabel, spec.Label, runtime, runtimeDisplay));
            messages.Add(new StrategyBuilderExplainabilityMessage(
                "warning",
                $"{directionLabel} / {spec.Label} inputu tanimli degil. Runtime gate yine de {runtimeDisplay} ister; bu yuzden entry uretilse bile blocked olabilir."));
            return;
        }

        var comparison = CompareThresholds(spec.Strictness, strategyRef.NumericValue, spec.ResolveRuntimeValue(runtime));
        var status = comparison switch
        {
            ThresholdComparison.RuntimeStricter => "Runtime stricter",
            ThresholdComparison.StrategyStricter => "Strategy stricter",
            _ => "Aligned"
        };
        var summary = comparison switch
        {
            ThresholdComparison.RuntimeStricter => $"{strategyRef.InputLabel} runtime gate'ten daha gevsek. Strategy {strategyRef.StrategyDisplay}; runtime {runtimeDisplay} ister.",
            ThresholdComparison.StrategyStricter => $"{strategyRef.InputLabel} runtime gate'ten daha siki. Strategy {strategyRef.StrategyDisplay}; runtime {runtimeDisplay} istegini de zaten kapsar.",
            _ => $"{strategyRef.InputLabel} ile runtime gate ayni yone bakiyor. Strategy {strategyRef.StrategyDisplay}; runtime {runtimeDisplay}."
        };

        rows.Add(new StrategyBuilderParityRow(
            directionLabel,
            spec.Key,
            spec.Label,
            strategyRef.InputLabel,
            strategyRef.StrategyDisplay,
            runtimeDisplay,
            status,
            summary));

        if (comparison == ThresholdComparison.RuntimeStricter)
        {
            messages.Add(new StrategyBuilderExplainabilityMessage(
                "warning",
                $"{strategyRef.InputLabel} runtime gate ile cakisiyor. Strategy {strategyRef.StrategyDisplay} olsa da runtime {runtimeDisplay} nedeniyle 'neden blocked' gorebilirsiniz."));
        }
    }

    private static StrategyBuilderParityRow BuildMissingRow(
        string directionLabel,
        string metricLabel,
        StrategyBuilderDirectionRuntimeConfig runtime,
        string runtimeDisplay)
    {
        return new StrategyBuilderParityRow(
            directionLabel,
            metricLabel.ToLowerInvariant().Replace(' ', '-').Replace('%', 'p'),
            metricLabel,
            $"{directionLabel} / {metricLabel} inputu",
            "Tanimsiz",
            runtimeDisplay,
            runtime.Enabled ? "Eksik" : "Runtime disabled",
            runtime.Enabled
                ? $"Strategy threshold tanimli degil. Runtime gate {runtimeDisplay} ile yine block uretebilir."
                : $"Runtime gate kapali oldugu icin {metricLabel} parity blok'u beklenmez.");
    }

    private static string BuildRuntimeDisplay(string directionLabel, string metricLabel, StrategyBuilderDirectionRuntimeConfig runtime)
    {
        if (!runtime.Enabled)
        {
            return "Disabled";
        }

        return metricLabel switch
        {
            "RSI" when string.Equals(directionLabel, "Long", StringComparison.OrdinalIgnoreCase) =>
                $"RSI < {runtime.RsiThreshold.ToString("0.##", CultureInfo.InvariantCulture)}",
            "RSI" =>
                $"RSI > {runtime.RsiThreshold.ToString("0.##", CultureInfo.InvariantCulture)}",
            "MACD histogram" when string.Equals(directionLabel, "Long", StringComparison.OrdinalIgnoreCase) =>
                $"MACD hist >= {runtime.MacdThreshold.ToString("0.####", CultureInfo.InvariantCulture)}",
            "MACD histogram" =>
                $"MACD hist <= {runtime.MacdThreshold.ToString("0.####", CultureInfo.InvariantCulture)}",
            _ => $"Bollinger width >= {runtime.BollingerWidthThreshold.ToString("0.####", CultureInfo.InvariantCulture)}%"
        };
    }

    private static ThresholdComparison CompareThresholds(ThresholdStrictness strictness, decimal strategyValue, decimal runtimeValue)
    {
        if (strategyValue == runtimeValue)
        {
            return ThresholdComparison.Aligned;
        }

        return strictness switch
        {
            ThresholdStrictness.LowerIsStricter => strategyValue < runtimeValue
                ? ThresholdComparison.StrategyStricter
                : ThresholdComparison.RuntimeStricter,
            ThresholdStrictness.HigherIsStricter => strategyValue > runtimeValue
                ? ThresholdComparison.StrategyStricter
                : ThresholdComparison.RuntimeStricter,
            _ => ThresholdComparison.Aligned
        };
    }

    private static StrategyConditionRef[] CollectConditionRefs(StrategyRuleNode? node, string sectionLabel)
    {
        var results = new List<StrategyConditionRef>();
        Traverse(node, sectionLabel, results);
        return results.ToArray();
    }

    private static void Traverse(StrategyRuleNode? node, string label, List<StrategyConditionRef> results)
    {
        if (node is null)
        {
            return;
        }

        switch (node)
        {
            case StrategyRuleCondition condition:
                AddCondition(label, condition, results);
                return;
            case StrategyRuleGroup group:
                var activeRuleIndex = 0;
                foreach (var child in group.Rules)
                {
                    if (child is StrategyRuleCondition conditionChild && conditionChild.Metadata?.Enabled == false)
                    {
                        continue;
                    }

                    if (child is StrategyRuleGroup groupChild && groupChild.Metadata?.Enabled == false)
                    {
                        continue;
                    }

                    activeRuleIndex++;
                    Traverse(child, $"{label} / Kural #{activeRuleIndex}", results);
                }
                return;
        }
    }

    private static void AddCondition(string label, StrategyRuleCondition condition, List<StrategyConditionRef> results)
    {
        if (condition.Metadata?.Enabled == false)
        {
            return;
        }

        if (!TryResolveNumericOperand(condition.Operand, out var numericValue))
        {
            return;
        }

        var metricLabel = ResolvePathLabel(condition.Path);
        var comparisonLabel = ResolveComparisonLabel(condition.Comparison);
        results.Add(new StrategyConditionRef(
            condition.Path,
            condition.Comparison,
            numericValue,
            $"{label} / Value ({metricLabel})",
            $"{comparisonLabel} {numericValue.ToString(metricLabel == "RSI" ? "0.##" : "0.####", CultureInfo.InvariantCulture)}"));
    }

    private static bool TryResolveNumericOperand(StrategyRuleOperand operand, out decimal value)
    {
        value = 0m;
        if (operand.Kind != StrategyRuleOperandKind.Number)
        {
            return false;
        }

        return decimal.TryParse(operand.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static string ResolvePathLabel(string path)
    {
        return path.Trim().ToLowerInvariant() switch
        {
            "indicator.rsi.value" => "RSI",
            "indicator.macd.histogram" => "MACD histogram",
            "indicator.bollinger.bandwidth" => "Bollinger width %",
            _ => path
        };
    }

    private static string ResolveComparisonLabel(StrategyRuleComparisonOperator comparison)
    {
        return comparison switch
        {
            StrategyRuleComparisonOperator.GreaterThan => ">",
            StrategyRuleComparisonOperator.GreaterThanOrEqual => ">=",
            StrategyRuleComparisonOperator.LessThan => "<",
            StrategyRuleComparisonOperator.LessThanOrEqual => "<=",
            StrategyRuleComparisonOperator.Equals => "=",
            _ => comparison.ToString()
        };
    }

    private sealed record StrategyConditionRef(
        string Path,
        StrategyRuleComparisonOperator Comparison,
        decimal NumericValue,
        string InputLabel,
        string StrategyDisplay);

    private sealed record MetricSpec(
        string Key,
        string Label,
        ThresholdStrictness Strictness,
        Func<StrategyBuilderDirectionRuntimeConfig, decimal> ResolveRuntimeValue,
        Func<StrategyConditionRef, bool> IsMatch)
    {
        public static MetricSpec LongRsi { get; } = new(
            "long-rsi",
            "RSI",
            ThresholdStrictness.LowerIsStricter,
            runtime => runtime.RsiThreshold,
            reference => string.Equals(reference.Path, "indicator.rsi.value", StringComparison.OrdinalIgnoreCase) &&
                         reference.Comparison is StrategyRuleComparisonOperator.LessThan or StrategyRuleComparisonOperator.LessThanOrEqual);

        public static MetricSpec ShortRsi { get; } = new(
            "short-rsi",
            "RSI",
            ThresholdStrictness.HigherIsStricter,
            runtime => runtime.RsiThreshold,
            reference => string.Equals(reference.Path, "indicator.rsi.value", StringComparison.OrdinalIgnoreCase) &&
                         reference.Comparison is StrategyRuleComparisonOperator.GreaterThan or StrategyRuleComparisonOperator.GreaterThanOrEqual);

        public static MetricSpec LongMacdHistogram { get; } = new(
            "long-macd-histogram",
            "MACD histogram",
            ThresholdStrictness.HigherIsStricter,
            runtime => runtime.MacdThreshold,
            reference => string.Equals(reference.Path, "indicator.macd.histogram", StringComparison.OrdinalIgnoreCase) &&
                         reference.Comparison is StrategyRuleComparisonOperator.GreaterThan or StrategyRuleComparisonOperator.GreaterThanOrEqual);

        public static MetricSpec ShortMacdHistogram { get; } = new(
            "short-macd-histogram",
            "MACD histogram",
            ThresholdStrictness.LowerIsStricter,
            runtime => runtime.MacdThreshold,
            reference => string.Equals(reference.Path, "indicator.macd.histogram", StringComparison.OrdinalIgnoreCase) &&
                         reference.Comparison is StrategyRuleComparisonOperator.LessThan or StrategyRuleComparisonOperator.LessThanOrEqual);

        public static MetricSpec LongBollingerWidth { get; } = new(
            "long-bollinger-width",
            "Bollinger width %",
            ThresholdStrictness.HigherIsStricter,
            runtime => runtime.BollingerWidthThreshold,
            reference => string.Equals(reference.Path, "indicator.bollinger.bandwidth", StringComparison.OrdinalIgnoreCase) &&
                         reference.Comparison is StrategyRuleComparisonOperator.GreaterThan or StrategyRuleComparisonOperator.GreaterThanOrEqual);

        public static MetricSpec ShortBollingerWidth { get; } = new(
            "short-bollinger-width",
            "Bollinger width %",
            ThresholdStrictness.HigherIsStricter,
            runtime => runtime.BollingerWidthThreshold,
            reference => string.Equals(reference.Path, "indicator.bollinger.bandwidth", StringComparison.OrdinalIgnoreCase) &&
                         reference.Comparison is StrategyRuleComparisonOperator.GreaterThan or StrategyRuleComparisonOperator.GreaterThanOrEqual);
    }

    private enum ThresholdStrictness
    {
        LowerIsStricter = 0,
        HigherIsStricter = 1
    }

    private enum ThresholdComparison
    {
        Aligned = 0,
        RuntimeStricter = 1,
        StrategyStricter = 2
    }
}

public sealed record StrategyBuilderRuntimeConfig(
    StrategyBuilderDirectionRuntimeConfig Long,
    StrategyBuilderDirectionRuntimeConfig Short);

public sealed record StrategyBuilderDirectionRuntimeConfig(
    string Direction,
    bool Enabled,
    decimal RsiThreshold,
    decimal MacdThreshold,
    decimal BollingerWidthThreshold,
    string Summary);

public sealed record StrategyBuilderExplainabilitySnapshot(
    StrategyBuilderRuntimeConfig Runtime,
    IReadOnlyCollection<StrategyBuilderExplainabilityMessage> Messages,
    IReadOnlyCollection<StrategyBuilderParityRow> LongRows,
    IReadOnlyCollection<StrategyBuilderParityRow> ShortRows);

public sealed record StrategyBuilderExplainabilityMessage(
    string Tone,
    string Message);

public sealed record StrategyBuilderParityRow(
    string Direction,
    string MetricKey,
    string MetricLabel,
    string StrategyInputLabel,
    string StrategyThreshold,
    string RuntimeThreshold,
    string Status,
    string Summary);
