using System.Globalization;
using System.Text;
using CoinBot.Application.Abstractions.Ai;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Ai;

public sealed class TrainingDatasetBuilderService(
    ApplicationDbContext dbContext,
    IAiShadowDecisionService aiShadowDecisionService,
    TimeProvider timeProvider) : ITrainingDatasetBuilderService
{
    private const int DefaultTake = 500;
    private const int MaxTake = 1000;
    private const string CsvContentType = "text/csv; charset=utf-8";
    private const string LabelVersion = "TDL-1.v1";

    private static readonly IReadOnlyCollection<TrainingDatasetColumnSnapshot> Columns =
    [
        new("meta_user_id", TrainingDatasetColumnGroup.Metadata, "string", "Scoped owner user id."),
        new("meta_bot_id", TrainingDatasetColumnGroup.Metadata, "guid", "Trading bot id."),
        new("meta_feature_snapshot_id", TrainingDatasetColumnGroup.Metadata, "guid", "Feature snapshot id."),
        new("meta_ai_shadow_decision_id", TrainingDatasetColumnGroup.Metadata, "guid", "AI shadow decision id."),
        new("meta_strategy_signal_id", TrainingDatasetColumnGroup.Metadata, "guid", "Strategy signal id when available."),
        new("meta_execution_order_id", TrainingDatasetColumnGroup.Metadata, "guid", "Resolved execution order id when available."),
        new("meta_correlation_id", TrainingDatasetColumnGroup.Metadata, "string", "Runtime/root correlation id."),
        new("meta_snapshot_key", TrainingDatasetColumnGroup.Metadata, "string", "Deterministic feature snapshot key."),
        new("meta_symbol", TrainingDatasetColumnGroup.Metadata, "string", "Market symbol."),
        new("meta_timeframe", TrainingDatasetColumnGroup.Metadata, "string", "Market timeframe."),
        new("meta_strategy_key", TrainingDatasetColumnGroup.Metadata, "string", "Strategy key at evaluation time."),
        new("meta_feature_version", TrainingDatasetColumnGroup.Metadata, "string", "Feature schema version."),
        new("meta_feature_anchor_time_utc", TrainingDatasetColumnGroup.Metadata, "datetime", "Deterministic feature anchor timestamp."),
        new("meta_feature_evaluated_at_utc", TrainingDatasetColumnGroup.Metadata, "datetime", "Feature snapshot evaluation time."),
        new("meta_market_data_timestamp_utc", TrainingDatasetColumnGroup.Metadata, "datetime", "Market-data timestamp carried by the feature snapshot."),
        new("meta_decision_evaluated_at_utc", TrainingDatasetColumnGroup.Metadata, "datetime", "AI shadow decision evaluation time."),
        new("meta_outcome_reference_close_time_utc", TrainingDatasetColumnGroup.Metadata, "datetime", "Reference candle close used for label scoring."),
        new("meta_outcome_future_close_time_utc", TrainingDatasetColumnGroup.Metadata, "datetime", "Future horizon candle close used for label scoring."),
        new("meta_split_bucket", TrainingDatasetColumnGroup.Metadata, "string", "Deterministic time-series split bucket."),
        new("meta_is_training_eligible", TrainingDatasetColumnGroup.Metadata, "bool", "True when the row carries scored, trainable labels."),
        new("meta_label_direction", TrainingDatasetColumnGroup.Metadata, "string", "Resolved directional side used for MFE/MAE and TP/SL labels."),

        new("feature_plane", TrainingDatasetColumnGroup.Feature, "string", "Execution plane at snapshot time."),
        new("feature_trading_mode", TrainingDatasetColumnGroup.Feature, "string", "Trading mode at snapshot time."),
        new("feature_snapshot_state", TrainingDatasetColumnGroup.Feature, "string", "Feature snapshot readiness state."),
        new("feature_quality_reason_code", TrainingDatasetColumnGroup.Feature, "string", "Feature quality reason code."),
        new("feature_market_data_reason_code", TrainingDatasetColumnGroup.Feature, "string", "Market-data degraded-mode reason code."),
        new("feature_sample_count", TrainingDatasetColumnGroup.Feature, "int", "Observed candle sample count."),
        new("feature_required_sample_count", TrainingDatasetColumnGroup.Feature, "int", "Required candle sample count."),
        new("feature_reference_price", TrainingDatasetColumnGroup.Feature, "decimal", "Reference price captured in the feature snapshot."),
        new("feature_ema20", TrainingDatasetColumnGroup.Feature, "decimal", "EMA20 value."),
        new("feature_ema50", TrainingDatasetColumnGroup.Feature, "decimal", "EMA50 value."),
        new("feature_ema200", TrainingDatasetColumnGroup.Feature, "decimal", "EMA200 value."),
        new("feature_alma", TrainingDatasetColumnGroup.Feature, "decimal", "ALMA value."),
        new("feature_frama", TrainingDatasetColumnGroup.Feature, "decimal", "FRAMA value."),
        new("feature_rsi", TrainingDatasetColumnGroup.Feature, "decimal", "RSI value."),
        new("feature_macd_line", TrainingDatasetColumnGroup.Feature, "decimal", "MACD line."),
        new("feature_macd_signal", TrainingDatasetColumnGroup.Feature, "decimal", "MACD signal line."),
        new("feature_macd_histogram", TrainingDatasetColumnGroup.Feature, "decimal", "MACD histogram."),
        new("feature_kdj_k", TrainingDatasetColumnGroup.Feature, "decimal", "KDJ K value."),
        new("feature_kdj_d", TrainingDatasetColumnGroup.Feature, "decimal", "KDJ D value."),
        new("feature_kdj_j", TrainingDatasetColumnGroup.Feature, "decimal", "KDJ J value."),
        new("feature_fisher_transform", TrainingDatasetColumnGroup.Feature, "decimal", "Fisher transform value."),
        new("feature_atr", TrainingDatasetColumnGroup.Feature, "decimal", "ATR value."),
        new("feature_bollinger_percent_b", TrainingDatasetColumnGroup.Feature, "decimal", "Bollinger %B."),
        new("feature_bollinger_band_width", TrainingDatasetColumnGroup.Feature, "decimal", "Bollinger band width."),
        new("feature_keltner_channel_relation", TrainingDatasetColumnGroup.Feature, "decimal", "Keltner channel relation."),
        new("feature_pmax_value", TrainingDatasetColumnGroup.Feature, "decimal", "PMAX value."),
        new("feature_chandelier_exit", TrainingDatasetColumnGroup.Feature, "decimal", "Chandelier exit value."),
        new("feature_volume_spike_ratio", TrainingDatasetColumnGroup.Feature, "decimal", "Volume spike ratio."),
        new("feature_relative_volume", TrainingDatasetColumnGroup.Feature, "decimal", "Relative volume."),
        new("feature_obv", TrainingDatasetColumnGroup.Feature, "decimal", "On-balance volume."),
        new("feature_mfi", TrainingDatasetColumnGroup.Feature, "decimal", "Money flow index."),
        new("feature_klinger_oscillator", TrainingDatasetColumnGroup.Feature, "decimal", "Klinger oscillator."),
        new("feature_klinger_signal", TrainingDatasetColumnGroup.Feature, "decimal", "Klinger signal."),
        new("feature_has_open_position", TrainingDatasetColumnGroup.Feature, "bool", "Open-position context at evaluation time."),
        new("feature_is_in_cooldown", TrainingDatasetColumnGroup.Feature, "bool", "Cooldown context at evaluation time."),
        new("feature_primary_regime", TrainingDatasetColumnGroup.Feature, "string", "Primary regime classification."),
        new("feature_momentum_bias", TrainingDatasetColumnGroup.Feature, "string", "Momentum bias classification."),
        new("feature_volatility_state", TrainingDatasetColumnGroup.Feature, "string", "Volatility state classification."),

        new("label_outcome_state", TrainingDatasetColumnGroup.Label, "string", "Outcome scoring state."),
        new("label_outcome_score", TrainingDatasetColumnGroup.Label, "decimal", "Directional outcome score from AI shadow outcome."),
        new("label_realized_return", TrainingDatasetColumnGroup.Label, "decimal", "Return between reference and future horizon close."),
        new("label_realized_directionality", TrainingDatasetColumnGroup.Label, "string", "Observed directionality over the scored horizon."),
        new("label_profitable_within_horizon", TrainingDatasetColumnGroup.Label, "bool", "True when the directional path moved favorably within the scored horizon."),
        new("label_mfe_return", TrainingDatasetColumnGroup.Label, "decimal", "Maximum favorable excursion over the scored candle window."),
        new("label_mae_return", TrainingDatasetColumnGroup.Label, "decimal", "Maximum adverse excursion over the scored candle window."),
        new("label_take_profit_touched", TrainingDatasetColumnGroup.Label, "bool", "True when the actual execution take-profit price was touched within the horizon window."),
        new("label_stop_loss_touched", TrainingDatasetColumnGroup.Label, "bool", "True when the actual execution stop-loss price was touched within the horizon window."),
        new("label_false_positive", TrainingDatasetColumnGroup.Label, "bool", "AI positive directional call that scored negative."),
        new("label_false_neutral", TrainingDatasetColumnGroup.Label, "bool", "Neutral call during a directional move."),
        new("label_overtrading", TrainingDatasetColumnGroup.Label, "bool", "Directional call against a flat/neutral move."),
        new("label_suppression_candidate", TrainingDatasetColumnGroup.Label, "bool", "Decision marked as suppression candidate by shadow outcome logic."),
        new("label_suppression_aligned", TrainingDatasetColumnGroup.Label, "bool", "Suppression candidate that aligned with realized outcome."),
        new("label_was_blocked", TrainingDatasetColumnGroup.Label, "bool", "True when the decision resolved to a blocked/no-submit path."),
        new("label_final_action", TrainingDatasetColumnGroup.Label, "string", "Final advisory action recorded by AI shadow decision."),
        new("label_block_reason", TrainingDatasetColumnGroup.Label, "string", "Primary block reason for no-submit paths."),
        new("label_no_submit_reason", TrainingDatasetColumnGroup.Label, "string", "Explicit no-submit reason."),
        new("label_has_execution_order", TrainingDatasetColumnGroup.Label, "bool", "True when an execution order exists for the strategy signal."),
        new("label_was_submitted_to_broker", TrainingDatasetColumnGroup.Label, "bool", "True when the resolved execution order reached broker submit."),
        new("label_was_filled", TrainingDatasetColumnGroup.Label, "bool", "True when the resolved execution order reached filled state."),
        new("label_execution_state", TrainingDatasetColumnGroup.Label, "string", "Resolved execution order state."),
        new("label_execution_failure_code", TrainingDatasetColumnGroup.Label, "string", "Resolved execution failure code."),
        new("label_execution_rejection_stage", TrainingDatasetColumnGroup.Label, "string", "Resolved execution rejection stage."),
        new("label_good_entry", TrainingDatasetColumnGroup.Label, "bool", "True when the directional entry outcome resolved as favorable."),
        new("label_good_exit", TrainingDatasetColumnGroup.Label, "bool", "True when the exit order resolved as a favorable close."),
        new("label_outcome", TrainingDatasetColumnGroup.Label, "string", "Deterministic directional outcome classification: Win, Loss, Neutral, or Unknown."),
        new("label_false_signal", TrainingDatasetColumnGroup.Label, "bool", "True when the directional recommendation resolved as a loss."),
        new("label_expected_move_pct", TrainingDatasetColumnGroup.Label, "decimal", "Ex-ante expected move proxy from ATR/reference price."),
        new("label_max_favorable_excursion", TrainingDatasetColumnGroup.Label, "decimal", "Positive maximum favorable excursion magnitude."),
        new("label_max_adverse_excursion", TrainingDatasetColumnGroup.Label, "decimal", "Positive maximum adverse excursion magnitude."),
        new("label_realized_pnl", TrainingDatasetColumnGroup.Label, "decimal", "Actual realized PnL linked to the resolved execution order when available."),
        new("label_estimated_pnl", TrainingDatasetColumnGroup.Label, "decimal", "Directional estimated PnL from outcome return and order notional."),
        new("label_drawdown", TrainingDatasetColumnGroup.Label, "decimal", "Directional drawdown estimate from adverse excursion and order notional."),
        new("label_risk_violation_count", TrainingDatasetColumnGroup.Label, "int", "Count of deterministic risk or pilot-safety veto signals on the decision."),
        new("label_duplicate_order_count", TrainingDatasetColumnGroup.Label, "int", "Count of duplicate-suppressed execution rows for the resolved strategy signal."),
        new("label_stale_data_entry_count", TrainingDatasetColumnGroup.Label, "int", "Count of stale or missing-fresh-signal entry blockers on the decision."),
        new("label_reduce_only_violation_count", TrainingDatasetColumnGroup.Label, "int", "Count of exit execution rows that were not reduce-only."),
        new("label_completeness", TrainingDatasetColumnGroup.Label, "string", "Completeness classification for the derived label set."),
        new("label_version", TrainingDatasetColumnGroup.Label, "string", "Deterministic label schema version.")
    ];

    private static readonly IReadOnlyCollection<TrainingDatasetLabelDefinitionSnapshot> LabelDefinitions =
    [
        new("label_outcome_score", "Directional return score aligned to the advisory direction over the requested horizon.", false, "Derived from AiShadowDecisionOutcome. Positive is better."),
        new("label_realized_return", "Reference-close to future-close return over the requested horizon.", false, "Market path label, not actual live PnL."),
        new("label_mfe_return", "Maximum favorable excursion between reference candle close and future horizon close.", true, "Directional label. Null when direction or candle window is unavailable."),
        new("label_mae_return", "Maximum adverse excursion between reference candle close and future horizon close.", true, "Directional label. Negative values indicate adverse move magnitude."),
        new("label_profitable_within_horizon", "Whether the directional path moved favorably at any point inside the horizon window.", true, "Uses MFE and the resolved advisory direction."),
        new("label_take_profit_touched", "Whether the resolved execution order take-profit level was touched within the horizon window.", true, "Touch-only label. It does not encode first-hit sequencing."),
        new("label_stop_loss_touched", "Whether the resolved execution order stop-loss level was touched within the horizon window.", true, "Touch-only label. It does not encode first-hit sequencing."),
        new("label_was_blocked", "Whether the decision resolved to a no-submit or blocked path.", false, "Operational label for blocked-trade dataset slices."),
        new("label_has_execution_order", "Whether a concrete execution order exists for the strategy signal.", false, "Separates decision-only rows from execution-backed rows."),
        new("label_was_submitted_to_broker", "Whether the resolved execution order reached broker submit.", false, "Different from filled/live PnL closure."),
        new("label_was_filled", "Whether the resolved execution order reached the filled state.", false, "Execution truth label, not a model feature."),
        new("label_false_positive", "Whether a positive directional recommendation scored negatively.", false, "Copied from AiShadowDecisionOutcome."),
        new("label_false_neutral", "Whether a neutral recommendation missed a meaningful move.", false, "Copied from AiShadowDecisionOutcome."),
        new("label_overtrading", "Whether a directional recommendation fired into a neutral outcome band.", false, "Copied from AiShadowDecisionOutcome."),
        new("label_suppression_aligned", "Whether suppression would have aligned with the realized outcome.", false, "Useful for no-trade policy evaluation."),
        new("label_outcome", "Deterministic directional outcome classification using the resolved directional return.", false, "Win/Loss/Neutral/Unknown."),
        new("label_good_entry", "Whether the entry idea was favorable over the scored horizon.", true, "Independent from whether the trade was blocked."),
        new("label_good_exit", "Whether the resolved exit order closed favorably.", true, "Only populated for exit or reduce-only closes."),
        new("label_false_signal", "Whether the directional recommendation resolved as a loss.", true, "Null when outcome is unknown."),
        new("label_expected_move_pct", "Expected move proxy from ATR/reference price at feature time.", true, "Ex-ante snapshot-derived label aid, not future leakage."),
        new("label_realized_pnl", "Actual realized PnL linked to the resolved order when ledger/fill evidence exists.", true, "Null when no actual realized PnL evidence exists."),
        new("label_estimated_pnl", "Directional estimated PnL from realized return and order notional.", true, "Used when actual realized PnL is unavailable."),
        new("label_drawdown", "Directional drawdown estimate from adverse excursion and order notional.", true, "Positive magnitude."),
        new("label_risk_violation_count", "Count of structured risk/pilot safety veto signals.", false, "Negative or safety-oriented label."),
        new("label_duplicate_order_count", "Count of duplicate-suppressed orders for the resolved strategy signal.", false, "Operational safety label."),
        new("label_stale_data_entry_count", "Count of stale-data blockers on the decision.", false, "Operational data-quality label."),
        new("label_reduce_only_violation_count", "Count of exit orders that violated reduce-only expectations.", false, "Should normally stay zero."),
        new("label_completeness", "Completeness state for the extended deterministic label set.", false, "Complete, Partial, or Unknown."),
        new("label_version", "Version identifier for the extended deterministic label schema.", false, LabelVersion)
    ];

    private static readonly IReadOnlyCollection<TrainingDatasetLeakageRuleSnapshot> LeakageRules =
    [
        new("feature_last_veto_reason_code", "Excluded because it carries prior policy-veto state rather than market state."),
        new("feature_last_decision_outcome", "Excluded because it is a post-decision control output."),
        new("feature_last_decision_code", "Excluded because it is a post-decision control output."),
        new("feature_last_execution_state", "Excluded because it is a post-submit execution result."),
        new("feature_last_failure_code", "Excluded because it is a post-submit failure result."),
        new("feature_feature_summary", "Excluded because it is post-hoc summary text."),
        new("feature_top_signal_hints", "Excluded because it is post-hoc summary text."),
        new("feature_normalization_meta", "Excluded because it is post-hoc summary text."),
        new("meta_ai_direction", "Excluded from feature columns because it is the decision target side, not an input feature."),
        new("meta_strategy_decision_outcome", "Excluded from feature columns because it is a downstream decision result.")
    ];

    private static readonly IReadOnlySet<string> SanitizedMetadataColumns = new HashSet<string>(StringComparer.Ordinal)
    {
        "meta_user_id",
        "meta_bot_id",
        "meta_feature_snapshot_id",
        "meta_ai_shadow_decision_id",
        "meta_strategy_signal_id",
        "meta_execution_order_id",
        "meta_correlation_id",
        "meta_snapshot_key"
    };

    private static readonly IReadOnlyCollection<string> InternalColumnOrder = Columns.Select(column => column.Name).ToArray();
    private static readonly IReadOnlyCollection<string> SanitizedColumnOrder = Columns
        .Where(column => !SanitizedMetadataColumns.Contains(column.Name))
        .Select(column => column.Name)
        .ToArray();

    public async Task<TrainingDatasetBuildSnapshot> BuildAsync(
        TrainingDatasetBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalized = NormalizeRequest(request);
        normalized = normalized with { UserId = dbContext.EnsureCurrentUserScope(normalized.UserId) };
        var decisions = await LoadDecisionRowsAsync(normalized, cancellationToken);
        if (decisions.Count == 0)
        {
            return new TrainingDatasetBuildSnapshot(
                normalized.UserId,
                normalized.BotId,
                normalized.Symbol,
                normalized.Timeframe,
                normalized.HorizonKind,
                normalized.HorizonValue,
                0,
                0,
                0,
                LabelDefinitions,
                LeakageRules,
                Columns,
                []);
        }

        await EnsureOutcomeCoverageAsync(normalized, decisions, cancellationToken);

        var outcomesByDecisionId = await LoadOutcomeRowsAsync(normalized, decisions, cancellationToken);
        var featuresById = await LoadFeatureRowsAsync(decisions, cancellationToken);
        var executionOrderContextsBySignalId = await LoadExecutionOrdersAsync(normalized.UserId, decisions, cancellationToken);
        var candleWindowsByMarket = await LoadCandleWindowsAsync(outcomesByDecisionId.Values, cancellationToken);

        var sourceRows = BuildSourceRows(
            decisions,
            featuresById,
            outcomesByDecisionId,
            executionOrderContextsBySignalId,
            candleWindowsByMarket);

        var trainingEligibleRowCount = sourceRows.Count(row => row.IsTrainingEligible);
        var returnedRows = AssignSplitBuckets(sourceRows, normalized.TrainingEligibleOnly);

        return new TrainingDatasetBuildSnapshot(
            normalized.UserId,
            normalized.BotId,
            normalized.Symbol,
            normalized.Timeframe,
            normalized.HorizonKind,
            normalized.HorizonValue,
            sourceRows.Count,
            returnedRows.Count,
            trainingEligibleRowCount,
            LabelDefinitions,
            LeakageRules,
            Columns,
            returnedRows);
    }

    public async Task<TrainingDatasetExportSnapshot> ExportCsvAsync(
        TrainingDatasetBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        var dataset = await BuildAsync(request, cancellationToken);
        var columnOrder = ResolveExportColumnOrder(request.ExportMode);
        var csv = BuildCsv(dataset.Rows, columnOrder);
        var fileName = BuildFileName(dataset, request.ExportMode);

        return new TrainingDatasetExportSnapshot(
            fileName,
            CsvContentType,
            csv,
            dataset.SourceRowCount,
            dataset.RowCount,
            request.ExportMode,
            columnOrder);
    }

    private async Task<IReadOnlyCollection<DecisionProjection>> LoadDecisionRowsAsync(
        NormalizedRequest request,
        CancellationToken cancellationToken)
    {
        var query = dbContext.AiShadowDecisions
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == request.UserId &&
                entity.FeatureSnapshotId.HasValue &&
                !entity.IsDeleted);

        if (request.BotId.HasValue)
        {
            query = query.Where(entity => entity.BotId == request.BotId.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.Symbol))
        {
            query = query.Where(entity => entity.Symbol == request.Symbol);
        }

        if (!string.IsNullOrWhiteSpace(request.Timeframe))
        {
            query = query.Where(entity => entity.Timeframe == request.Timeframe);
        }

        if (request.FromUtc.HasValue)
        {
            query = query.Where(entity => entity.EvaluatedAtUtc >= request.FromUtc.Value);
        }

        if (request.ToUtc.HasValue)
        {
            query = query.Where(entity => entity.EvaluatedAtUtc <= request.ToUtc.Value);
        }

        return await query
            .OrderByDescending(entity => entity.EvaluatedAtUtc)
            .ThenByDescending(entity => entity.CreatedDate)
            .Take(request.Take)
            .Select(entity => new DecisionProjection(
                entity.Id,
                entity.BotId,
                entity.FeatureSnapshotId!.Value,
                entity.StrategySignalId,
                entity.CorrelationId,
                entity.StrategyKey,
                entity.Symbol,
                entity.Timeframe,
                entity.EvaluatedAtUtc,
                entity.MarketDataTimestampUtc,
                entity.StrategyDirection,
                entity.AiDirection,
                entity.FinalAction,
                entity.HypotheticalSubmitAllowed,
                entity.HypotheticalBlockReason,
                entity.NoSubmitReason,
                entity.RiskVetoPresent,
                entity.PilotSafetyBlocked))
            .ToListAsync(cancellationToken);
    }

    private async Task EnsureOutcomeCoverageAsync(
        NormalizedRequest request,
        IReadOnlyCollection<DecisionProjection> decisions,
        CancellationToken cancellationToken)
    {
        var decisionIds = decisions.Select(entity => entity.Id).ToArray();
        var existingOutcomeIds = await dbContext.AiShadowDecisionOutcomes
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == request.UserId &&
                decisionIds.Contains(entity.AiShadowDecisionId) &&
                entity.HorizonKind == request.HorizonKind &&
                entity.HorizonValue == request.HorizonValue &&
                !entity.IsDeleted)
            .Select(entity => entity.AiShadowDecisionId)
            .ToListAsync(cancellationToken);

        foreach (var missingDecisionId in decisionIds.Except(existingOutcomeIds))
        {
            await aiShadowDecisionService.ScoreOutcomeAsync(
                request.UserId,
                missingDecisionId,
                request.HorizonKind,
                request.HorizonValue,
                cancellationToken);
        }
    }

    private async Task<IReadOnlyDictionary<Guid, OutcomeProjection>> LoadOutcomeRowsAsync(
        NormalizedRequest request,
        IReadOnlyCollection<DecisionProjection> decisions,
        CancellationToken cancellationToken)
    {
        var decisionIds = decisions.Select(entity => entity.Id).ToArray();
        var rows = await dbContext.AiShadowDecisionOutcomes
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == request.UserId &&
                decisionIds.Contains(entity.AiShadowDecisionId) &&
                entity.HorizonKind == request.HorizonKind &&
                entity.HorizonValue == request.HorizonValue &&
                !entity.IsDeleted)
            .Select(entity => new OutcomeProjection(
                entity.AiShadowDecisionId,
                entity.Symbol,
                entity.Timeframe,
                entity.OutcomeState,
                entity.OutcomeScore,
                entity.RealizedReturn,
                entity.RealizedDirectionality,
                entity.FalsePositive,
                entity.FalseNeutral,
                entity.Overtrading,
                entity.SuppressionCandidate,
                entity.SuppressionAligned,
                entity.ReferenceCandleCloseTimeUtc,
                entity.FutureCandleCloseTimeUtc,
                entity.ReferenceClosePrice))
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(entity => entity.AiShadowDecisionId);
    }

    private async Task<IReadOnlyDictionary<Guid, FeatureProjection>> LoadFeatureRowsAsync(
        IReadOnlyCollection<DecisionProjection> decisions,
        CancellationToken cancellationToken)
    {
        var featureIds = decisions
            .Select(entity => entity.FeatureSnapshotId)
            .Distinct()
            .ToArray();

        var rows = await dbContext.TradingFeatureSnapshots
            .AsNoTracking()
            .Where(entity => featureIds.Contains(entity.Id) && !entity.IsDeleted)
            .Select(entity => new FeatureProjection(
                entity.Id,
                entity.OwnerUserId,
                entity.BotId,
                entity.ExchangeAccountId,
                entity.CorrelationId,
                entity.SnapshotKey,
                entity.StrategyKey,
                entity.Symbol,
                entity.Timeframe,
                entity.EvaluatedAtUtc,
                entity.FeatureAnchorTimeUtc,
                entity.MarketDataTimestampUtc,
                entity.FeatureVersion,
                entity.SnapshotState,
                entity.QualityReasonCode,
                entity.MarketDataReasonCode,
                entity.SampleCount,
                entity.RequiredSampleCount,
                entity.ReferencePrice,
                entity.Ema20,
                entity.Ema50,
                entity.Ema200,
                entity.Alma,
                entity.Frama,
                entity.Rsi,
                entity.MacdLine,
                entity.MacdSignal,
                entity.MacdHistogram,
                entity.KdjK,
                entity.KdjD,
                entity.KdjJ,
                entity.FisherTransform,
                entity.Atr,
                entity.BollingerPercentB,
                entity.BollingerBandWidth,
                entity.KeltnerChannelRelation,
                entity.PmaxValue,
                entity.ChandelierExit,
                entity.VolumeSpikeRatio,
                entity.RelativeVolume,
                entity.Obv,
                entity.Mfi,
                entity.KlingerOscillator,
                entity.KlingerSignal,
                entity.Plane,
                entity.TradingMode,
                entity.HasOpenPosition,
                entity.IsInCooldown,
                entity.PrimaryRegime,
                entity.MomentumBias,
                entity.VolatilityState))
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(entity => entity.Id);
    }

    private async Task<IReadOnlyDictionary<Guid, ExecutionOrderContextProjection>> LoadExecutionOrdersAsync(
        string userId,
        IReadOnlyCollection<DecisionProjection> decisions,
        CancellationToken cancellationToken)
    {
        var strategySignalIds = decisions
            .Where(entity => entity.StrategySignalId.HasValue)
            .Select(entity => entity.StrategySignalId!.Value)
            .Distinct()
            .ToArray();

        if (strategySignalIds.Length == 0)
        {
            return new Dictionary<Guid, ExecutionOrderContextProjection>();
        }

        var rows = await dbContext.ExecutionOrders
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == userId &&
                strategySignalIds.Contains(entity.StrategySignalId) &&
                !entity.IsDeleted)
            .Select(entity => new ExecutionOrderProjection(
                entity.Id,
                entity.StrategySignalId,
                entity.SignalType,
                entity.Side,
                entity.Quantity,
                entity.FilledQuantity,
                entity.Price,
                entity.AverageFillPrice,
                entity.State,
                entity.SubmittedToBroker,
                entity.ReduceOnly,
                entity.DuplicateSuppressed,
                entity.FailureCode,
                entity.RejectionStage,
                entity.StopLossPrice,
                entity.TakeProfitPrice,
                entity.LastStateChangedAtUtc,
                entity.CreatedDate))
            .ToListAsync(cancellationToken);

        var selectedOrdersBySignalId = rows
            .GroupBy(entity => entity.StrategySignalId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(entity => entity.SubmittedToBroker)
                    .ThenByDescending(entity => entity.State == ExecutionOrderState.Filled)
                    .ThenByDescending(entity => entity.LastStateChangedAtUtc)
                    .ThenByDescending(entity => entity.CreatedDate)
                    .First());

        var realizedPnlByExecutionOrderId = await LoadRealizedPnlByExecutionOrderIdAsync(
            userId,
            selectedOrdersBySignalId.Values.Select(entity => entity.Id).Distinct().ToArray(),
            cancellationToken);

        return rows
            .GroupBy(entity => entity.StrategySignalId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var selectedOrder = selectedOrdersBySignalId[group.Key];
                    return new ExecutionOrderContextProjection(
                        selectedOrder,
                        group.Count(entity => entity.DuplicateSuppressed),
                        group.Count(entity => entity.SignalType == StrategySignalType.Exit && !entity.ReduceOnly),
                        realizedPnlByExecutionOrderId.TryGetValue(selectedOrder.Id, out var realizedPnl)
                            ? realizedPnl
                            : null);
                });
    }

    private async Task<IReadOnlyDictionary<Guid, decimal>> LoadRealizedPnlByExecutionOrderIdAsync(
        string userId,
        IReadOnlyCollection<Guid> executionOrderIds,
        CancellationToken cancellationToken)
    {
        if (executionOrderIds.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        var spotPnlRows = await dbContext.SpotPortfolioFills
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == userId &&
                executionOrderIds.Contains(entity.ExecutionOrderId) &&
                !entity.IsDeleted)
            .GroupBy(entity => entity.ExecutionOrderId)
            .Select(group => new
            {
                ExecutionOrderId = group.Key,
                RealizedPnl = group.Sum(entity => entity.RealizedPnlDelta)
            })
            .ToListAsync(cancellationToken);

        var normalizedExecutionOrderIds = executionOrderIds
            .Select(static entity => entity.ToString("N"))
            .ToArray();

        var demoPnlRows = await dbContext.DemoLedgerTransactions
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == userId &&
                entity.OrderId != null &&
                normalizedExecutionOrderIds.Contains(entity.OrderId) &&
                !entity.IsDeleted)
            .GroupBy(entity => entity.OrderId!)
            .Select(group => new
            {
                OrderId = group.Key,
                RealizedPnl = group.Sum(entity => entity.RealizedPnlDelta ?? 0m)
            })
            .ToListAsync(cancellationToken);

        var result = spotPnlRows.ToDictionary(entity => entity.ExecutionOrderId, entity => entity.RealizedPnl);
        foreach (var row in demoPnlRows)
        {
            if (!Guid.TryParseExact(row.OrderId, "N", out var executionOrderId))
            {
                continue;
            }

            result[executionOrderId] = result.TryGetValue(executionOrderId, out var existingRealizedPnl)
                ? existingRealizedPnl + row.RealizedPnl
                : row.RealizedPnl;
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<MarketKey, IReadOnlyCollection<CandleProjection>>> LoadCandleWindowsAsync(
        IEnumerable<OutcomeProjection> outcomes,
        CancellationToken cancellationToken)
    {
        var groups = outcomes
            .Where(entity => entity.ReferenceCandleCloseTimeUtc.HasValue && entity.FutureCandleCloseTimeUtc.HasValue)
            .GroupBy(entity => new MarketKey(entity.Symbol, entity.Timeframe))
            .ToArray();

        var result = new Dictionary<MarketKey, IReadOnlyCollection<CandleProjection>>();
        foreach (var group in groups)
        {
            var minReferenceTimeUtc = group.Min(entity => entity.ReferenceCandleCloseTimeUtc!.Value);
            var maxFutureTimeUtc = group.Max(entity => entity.FutureCandleCloseTimeUtc!.Value);

            var candles = await dbContext.HistoricalMarketCandles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(entity =>
                    !entity.IsDeleted &&
                    entity.Symbol == group.Key.Symbol &&
                    entity.Interval == group.Key.Timeframe &&
                    entity.CloseTimeUtc > minReferenceTimeUtc &&
                    entity.CloseTimeUtc <= maxFutureTimeUtc)
                .OrderBy(entity => entity.CloseTimeUtc)
                .ThenByDescending(entity => entity.ReceivedAtUtc)
                .Select(entity => new CandleProjection(
                    entity.CloseTimeUtc,
                    entity.HighPrice,
                    entity.LowPrice,
                    entity.ClosePrice,
                    entity.ReceivedAtUtc))
                .ToListAsync(cancellationToken);

            result[group.Key] = candles
                .GroupBy(entity => NormalizeTimestamp(entity.CloseTimeUtc))
                .OrderBy(entity => entity.Key)
                .Select(entity => entity.OrderByDescending(item => item.ReceivedAtUtc).First())
                .ToArray();
        }

        return result;
    }

    private IReadOnlyCollection<DatasetSourceRow> BuildSourceRows(
        IReadOnlyCollection<DecisionProjection> decisions,
        IReadOnlyDictionary<Guid, FeatureProjection> featuresById,
        IReadOnlyDictionary<Guid, OutcomeProjection> outcomesByDecisionId,
        IReadOnlyDictionary<Guid, ExecutionOrderContextProjection> executionOrderContextsBySignalId,
        IReadOnlyDictionary<MarketKey, IReadOnlyCollection<CandleProjection>> candleWindowsByMarket)
    {
        var rows = new List<DatasetSourceRow>(decisions.Count);

        foreach (var decision in decisions)
        {
            if (!featuresById.TryGetValue(decision.FeatureSnapshotId, out var feature))
            {
                continue;
            }

            outcomesByDecisionId.TryGetValue(decision.Id, out var outcome);
            var executionOrderContext = decision.StrategySignalId.HasValue &&
                                        executionOrderContextsBySignalId.TryGetValue(decision.StrategySignalId.Value, out var resolvedOrderContext)
                ? resolvedOrderContext
                : null;

            var executionOrder = executionOrderContext?.SelectedOrder;

            var labelDirection = ResolveLabelDirection(decision.AiDirection, decision.StrategyDirection);
            var directionalReturn = ResolveDirectionalReturn(outcome, labelDirection);
            var outcomeLabel = ResolveOutcomeLabel(outcome, directionalReturn);
            var expectedMovePct = ResolveExpectedMovePct(feature);
            var riskViolationCount = ResolveRiskViolationCount(decision);
            var staleDataEntryCount = ResolveStaleDataEntryCount(decision, feature);
            var duplicateOrderCount = executionOrderContext?.DuplicateOrderCount ?? 0;
            var reduceOnlyViolationCount = executionOrderContext?.ReduceOnlyViolationCount ?? 0;
            var orderNotional = ResolveOrderNotional(executionOrder);
            var realizedPnl = executionOrderContext?.RealizedPnl;

            var marketKey = new MarketKey(feature.Symbol, feature.Timeframe);
            candleWindowsByMarket.TryGetValue(marketKey, out var marketCandles);
            var excursions = ResolveExcursions(outcome, labelDirection, marketCandles);
            var estimatedPnl = ResolveEstimatedPnl(directionalReturn, orderNotional);
            var drawdown = ResolveDrawdown(excursions.MaeReturn, orderNotional);
            var goodEntry = ResolveGoodEntry(outcomeLabel);
            var goodExit = ResolveGoodExit(executionOrder, realizedPnl, estimatedPnl, outcomeLabel);
            var falseSignal = ResolveFalseSignal(outcomeLabel);
            var labelCompleteness = ResolveLabelCompleteness(outcome, labelDirection, expectedMovePct, outcomeLabel);
            var isTrainingEligible = outcome is not null && outcome.OutcomeState == AiShadowOutcomeState.Scored;
            var protectiveDirection = ResolveExecutionDirection(executionOrder, labelDirection);
            var takeProfitTouched = ResolveProtectiveTouch(protectiveDirection, executionOrder?.TakeProfitPrice, marketCandles, outcome, isStopLoss: false);
            var stopLossTouched = ResolveProtectiveTouch(protectiveDirection, executionOrder?.StopLossPrice, marketCandles, outcome, isStopLoss: true);
            var profitableWithinHorizon = excursions.MfeReturn.HasValue
                ? excursions.MfeReturn.Value > 0m
                : (bool?)null;

            var values = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["meta_user_id"] = feature.UserId,
                ["meta_bot_id"] = feature.BotId.ToString("D"),
                ["meta_feature_snapshot_id"] = feature.Id.ToString("D"),
                ["meta_ai_shadow_decision_id"] = decision.Id.ToString("D"),
                ["meta_strategy_signal_id"] = FormatGuid(decision.StrategySignalId),
                ["meta_execution_order_id"] = FormatGuid(executionOrder?.Id),
                ["meta_correlation_id"] = feature.CorrelationId ?? decision.CorrelationId,
                ["meta_snapshot_key"] = feature.SnapshotKey,
                ["meta_symbol"] = feature.Symbol,
                ["meta_timeframe"] = feature.Timeframe,
                ["meta_strategy_key"] = feature.StrategyKey,
                ["meta_feature_version"] = feature.FeatureVersion,
                ["meta_feature_anchor_time_utc"] = FormatDate(feature.FeatureAnchorTimeUtc),
                ["meta_feature_evaluated_at_utc"] = FormatDate(feature.EvaluatedAtUtc),
                ["meta_market_data_timestamp_utc"] = FormatDate(feature.MarketDataTimestampUtc),
                ["meta_decision_evaluated_at_utc"] = FormatDate(decision.EvaluatedAtUtc),
                ["meta_outcome_reference_close_time_utc"] = FormatDate(outcome?.ReferenceCandleCloseTimeUtc),
                ["meta_outcome_future_close_time_utc"] = FormatDate(outcome?.FutureCandleCloseTimeUtc),
                ["meta_label_direction"] = labelDirection,

                ["feature_plane"] = feature.Plane.ToString(),
                ["feature_trading_mode"] = feature.TradingMode.ToString(),
                ["feature_snapshot_state"] = feature.SnapshotState.ToString(),
                ["feature_quality_reason_code"] = feature.QualityReasonCode.ToString(),
                ["feature_market_data_reason_code"] = feature.MarketDataReasonCode.ToString(),
                ["feature_sample_count"] = feature.SampleCount.ToString(CultureInfo.InvariantCulture),
                ["feature_required_sample_count"] = feature.RequiredSampleCount.ToString(CultureInfo.InvariantCulture),
                ["feature_reference_price"] = FormatDecimal(feature.ReferencePrice),
                ["feature_ema20"] = FormatDecimal(feature.Ema20),
                ["feature_ema50"] = FormatDecimal(feature.Ema50),
                ["feature_ema200"] = FormatDecimal(feature.Ema200),
                ["feature_alma"] = FormatDecimal(feature.Alma),
                ["feature_frama"] = FormatDecimal(feature.Frama),
                ["feature_rsi"] = FormatDecimal(feature.Rsi),
                ["feature_macd_line"] = FormatDecimal(feature.MacdLine),
                ["feature_macd_signal"] = FormatDecimal(feature.MacdSignal),
                ["feature_macd_histogram"] = FormatDecimal(feature.MacdHistogram),
                ["feature_kdj_k"] = FormatDecimal(feature.KdjK),
                ["feature_kdj_d"] = FormatDecimal(feature.KdjD),
                ["feature_kdj_j"] = FormatDecimal(feature.KdjJ),
                ["feature_fisher_transform"] = FormatDecimal(feature.FisherTransform),
                ["feature_atr"] = FormatDecimal(feature.Atr),
                ["feature_bollinger_percent_b"] = FormatDecimal(feature.BollingerPercentB),
                ["feature_bollinger_band_width"] = FormatDecimal(feature.BollingerBandWidth),
                ["feature_keltner_channel_relation"] = FormatDecimal(feature.KeltnerChannelRelation),
                ["feature_pmax_value"] = FormatDecimal(feature.PmaxValue),
                ["feature_chandelier_exit"] = FormatDecimal(feature.ChandelierExit),
                ["feature_volume_spike_ratio"] = FormatDecimal(feature.VolumeSpikeRatio),
                ["feature_relative_volume"] = FormatDecimal(feature.RelativeVolume),
                ["feature_obv"] = FormatDecimal(feature.Obv),
                ["feature_mfi"] = FormatDecimal(feature.Mfi),
                ["feature_klinger_oscillator"] = FormatDecimal(feature.KlingerOscillator),
                ["feature_klinger_signal"] = FormatDecimal(feature.KlingerSignal),
                ["feature_has_open_position"] = FormatBool(feature.HasOpenPosition),
                ["feature_is_in_cooldown"] = FormatBool(feature.IsInCooldown),
                ["feature_primary_regime"] = feature.PrimaryRegime,
                ["feature_momentum_bias"] = feature.MomentumBias,
                ["feature_volatility_state"] = feature.VolatilityState,

                ["label_outcome_state"] = outcome?.OutcomeState.ToString(),
                ["label_outcome_score"] = FormatDecimal(outcome?.OutcomeScore),
                ["label_realized_return"] = FormatDecimal(outcome?.RealizedReturn),
                ["label_realized_directionality"] = outcome?.RealizedDirectionality,
                ["label_profitable_within_horizon"] = FormatBool(profitableWithinHorizon),
                ["label_mfe_return"] = FormatDecimal(excursions.MfeReturn),
                ["label_mae_return"] = FormatDecimal(excursions.MaeReturn),
                ["label_take_profit_touched"] = FormatBool(takeProfitTouched),
                ["label_stop_loss_touched"] = FormatBool(stopLossTouched),
                ["label_false_positive"] = FormatBool(outcome?.FalsePositive),
                ["label_false_neutral"] = FormatBool(outcome?.FalseNeutral),
                ["label_overtrading"] = FormatBool(outcome?.Overtrading),
                ["label_suppression_candidate"] = FormatBool(outcome?.SuppressionCandidate),
                ["label_suppression_aligned"] = FormatBool(outcome?.SuppressionAligned),
                ["label_was_blocked"] = FormatBool(ResolveWasBlocked(decision)),
                ["label_final_action"] = decision.FinalAction,
                ["label_block_reason"] = ResolveBlockReason(decision),
                ["label_no_submit_reason"] = decision.NoSubmitReason,
                ["label_has_execution_order"] = FormatBool(executionOrder is not null),
                ["label_was_submitted_to_broker"] = FormatBool(executionOrder?.SubmittedToBroker),
                ["label_was_filled"] = FormatBool(executionOrder?.State == ExecutionOrderState.Filled),
                ["label_execution_state"] = executionOrder?.State.ToString(),
                ["label_execution_failure_code"] = executionOrder?.FailureCode,
                ["label_execution_rejection_stage"] = executionOrder?.RejectionStage.ToString(),
                ["label_good_entry"] = FormatBool(goodEntry),
                ["label_good_exit"] = FormatBool(goodExit),
                ["label_outcome"] = outcomeLabel,
                ["label_false_signal"] = FormatBool(falseSignal),
                ["label_expected_move_pct"] = FormatDecimal(expectedMovePct),
                ["label_max_favorable_excursion"] = FormatDecimal(excursions.MfeReturn),
                ["label_max_adverse_excursion"] = FormatDecimal(ResolveAdverseExcursionMagnitude(excursions.MaeReturn)),
                ["label_realized_pnl"] = FormatDecimal(realizedPnl),
                ["label_estimated_pnl"] = FormatDecimal(estimatedPnl),
                ["label_drawdown"] = FormatDecimal(drawdown),
                ["label_risk_violation_count"] = riskViolationCount.ToString(CultureInfo.InvariantCulture),
                ["label_duplicate_order_count"] = duplicateOrderCount.ToString(CultureInfo.InvariantCulture),
                ["label_stale_data_entry_count"] = staleDataEntryCount.ToString(CultureInfo.InvariantCulture),
                ["label_reduce_only_violation_count"] = reduceOnlyViolationCount.ToString(CultureInfo.InvariantCulture),
                ["label_completeness"] = labelCompleteness,
                ["label_version"] = LabelVersion
            };

            rows.Add(new DatasetSourceRow(
                feature.Id,
                decision.Id,
                feature.FeatureAnchorTimeUtc ?? feature.EvaluatedAtUtc,
                isTrainingEligible,
                values));
        }

        return rows;
    }

    private static IReadOnlyCollection<TrainingDatasetRowSnapshot> AssignSplitBuckets(
        IReadOnlyCollection<DatasetSourceRow> sourceRows,
        bool trainingEligibleOnly)
    {
        var orderedRows = sourceRows
            .OrderBy(row => row.AnchorTimeUtc)
            .ThenBy(row => row.AiShadowDecisionId)
            .ToArray();

        var eligibleRows = orderedRows
            .Where(row => row.IsTrainingEligible)
            .ToArray();

        var splitMap = eligibleRows
            .Select((row, index) => new
            {
                row.AiShadowDecisionId,
                Bucket = ResolveSplitBucket(index, eligibleRows.Length)
            })
            .ToDictionary(item => item.AiShadowDecisionId, item => item.Bucket);

        var outputRows = new List<TrainingDatasetRowSnapshot>(orderedRows.Length);
        foreach (var row in orderedRows)
        {
            var splitBucket = splitMap.TryGetValue(row.AiShadowDecisionId, out var resolvedSplitBucket)
                ? resolvedSplitBucket
                : "Excluded";

            var values = new Dictionary<string, string?>(row.Values, StringComparer.Ordinal)
            {
                ["meta_split_bucket"] = splitBucket,
                ["meta_is_training_eligible"] = FormatBool(row.IsTrainingEligible)
            };

            if (!trainingEligibleOnly || row.IsTrainingEligible)
            {
                outputRows.Add(new TrainingDatasetRowSnapshot(
                    row.FeatureSnapshotId,
                    row.AiShadowDecisionId,
                    splitBucket,
                    row.IsTrainingEligible,
                    values));
            }
        }

        return outputRows;
    }

    private static string ResolveSplitBucket(int index, int totalCount)
    {
        if (totalCount <= 1)
        {
            return "Train";
        }

        var position = index / (decimal)(totalCount - 1);
        if (position < 0.70m)
        {
            return "Train";
        }

        if (position < 0.85m)
        {
            return "Validation";
        }

        return "Test";
    }

    private static ExcursionSnapshot ResolveExcursions(
        OutcomeProjection? outcome,
        string? labelDirection,
        IReadOnlyCollection<CandleProjection>? candles)
    {
        if (outcome?.ReferenceCandleCloseTimeUtc is null ||
            outcome.FutureCandleCloseTimeUtc is null ||
            outcome.ReferenceClosePrice is not > 0m ||
            string.IsNullOrWhiteSpace(labelDirection) ||
            candles is null)
        {
            return ExcursionSnapshot.Empty;
        }

        var window = candles
            .Where(entity =>
                entity.CloseTimeUtc > outcome.ReferenceCandleCloseTimeUtc.Value &&
                entity.CloseTimeUtc <= outcome.FutureCandleCloseTimeUtc.Value)
            .ToArray();

        if (window.Length == 0)
        {
            return ExcursionSnapshot.Empty;
        }

        var referencePrice = outcome.ReferenceClosePrice.Value;
        var maxHigh = window.Max(entity => entity.HighPrice);
        var minLow = window.Min(entity => entity.LowPrice);

        return string.Equals(labelDirection, "Short", StringComparison.Ordinal)
            ? new ExcursionSnapshot(
                RoundLabelReturn((referencePrice - minLow) / referencePrice),
                RoundLabelReturn((referencePrice - maxHigh) / referencePrice))
            : new ExcursionSnapshot(
                RoundLabelReturn((maxHigh - referencePrice) / referencePrice),
                RoundLabelReturn((minLow - referencePrice) / referencePrice));
    }

    private static bool? ResolveProtectiveTouch(
        string? labelDirection,
        decimal? thresholdPrice,
        IReadOnlyCollection<CandleProjection>? candles,
        OutcomeProjection? outcome,
        bool isStopLoss)
    {
        if (string.IsNullOrWhiteSpace(labelDirection) ||
            thresholdPrice is null ||
            outcome?.ReferenceCandleCloseTimeUtc is null ||
            outcome.FutureCandleCloseTimeUtc is null ||
            candles is null)
        {
            return null;
        }

        var window = candles
            .Where(entity =>
                entity.CloseTimeUtc > outcome.ReferenceCandleCloseTimeUtc.Value &&
                entity.CloseTimeUtc <= outcome.FutureCandleCloseTimeUtc.Value)
            .ToArray();

        if (window.Length == 0)
        {
            return null;
        }

        if (string.Equals(labelDirection, "Short", StringComparison.Ordinal))
        {
            return isStopLoss
                ? window.Any(entity => entity.HighPrice >= thresholdPrice.Value)
                : window.Any(entity => entity.LowPrice <= thresholdPrice.Value);
        }

        return isStopLoss
            ? window.Any(entity => entity.LowPrice <= thresholdPrice.Value)
            : window.Any(entity => entity.HighPrice >= thresholdPrice.Value);
    }

    private static bool ResolveWasBlocked(DecisionProjection decision)
    {
        return !decision.HypotheticalSubmitAllowed ||
               string.Equals(decision.FinalAction, "NoSubmit", StringComparison.Ordinal);
    }

    private static decimal? ResolveDirectionalReturn(OutcomeProjection? outcome, string? labelDirection)
    {
        if (outcome?.OutcomeState != AiShadowOutcomeState.Scored ||
            outcome.RealizedReturn is null ||
            string.IsNullOrWhiteSpace(labelDirection))
        {
            return null;
        }

        return string.Equals(labelDirection, "Short", StringComparison.Ordinal)
            ? RoundLabelReturn(-outcome.RealizedReturn.Value)
            : RoundLabelReturn(outcome.RealizedReturn.Value);
    }

    private static string ResolveOutcomeLabel(OutcomeProjection? outcome, decimal? directionalReturn)
    {
        if (outcome?.OutcomeState != AiShadowOutcomeState.Scored || directionalReturn is null)
        {
            return "Unknown";
        }

        if (directionalReturn.Value > 0m)
        {
            return "Win";
        }

        if (directionalReturn.Value < 0m)
        {
            return "Loss";
        }

        return "Neutral";
    }

    private static bool? ResolveGoodEntry(string outcomeLabel)
    {
        return outcomeLabel switch
        {
            "Win" => true,
            "Loss" => false,
            "Neutral" => false,
            _ => null
        };
    }

    private static bool? ResolveGoodExit(
        ExecutionOrderProjection? executionOrder,
        decimal? realizedPnl,
        decimal? estimatedPnl,
        string outcomeLabel)
    {
        if (executionOrder is null ||
            (executionOrder.SignalType != StrategySignalType.Exit && !executionOrder.ReduceOnly))
        {
            return null;
        }

        var resolvedPnl = realizedPnl ?? estimatedPnl;
        if (resolvedPnl.HasValue)
        {
            return resolvedPnl.Value > 0m;
        }

        return outcomeLabel switch
        {
            "Win" => true,
            "Loss" => false,
            "Neutral" => false,
            _ => null
        };
    }

    private static bool? ResolveFalseSignal(string outcomeLabel)
    {
        return outcomeLabel switch
        {
            "Loss" => true,
            "Win" => false,
            "Neutral" => false,
            _ => null
        };
    }

    private static decimal? ResolveExpectedMovePct(FeatureProjection feature)
    {
        if (feature.ReferencePrice is not > 0m || feature.Atr is null)
        {
            return null;
        }

        return RoundLabelReturn(feature.Atr.Value / feature.ReferencePrice.Value);
    }

    private static decimal? ResolveOrderNotional(ExecutionOrderProjection? executionOrder)
    {
        if (executionOrder is null)
        {
            return null;
        }

        var price = executionOrder.AverageFillPrice ?? executionOrder.Price;
        var quantity = executionOrder.FilledQuantity > 0m
            ? executionOrder.FilledQuantity
            : executionOrder.Quantity;

        if (price <= 0m || quantity <= 0m)
        {
            return null;
        }

        return decimal.Round(price * quantity, 8, MidpointRounding.AwayFromZero);
    }

    private static decimal? ResolveEstimatedPnl(decimal? directionalReturn, decimal? orderNotional)
    {
        if (directionalReturn is null || orderNotional is null)
        {
            return null;
        }

        return decimal.Round(directionalReturn.Value * orderNotional.Value, 8, MidpointRounding.AwayFromZero);
    }

    private static decimal? ResolveDrawdown(decimal? maeReturn, decimal? orderNotional)
    {
        if (maeReturn is null || orderNotional is null)
        {
            return null;
        }

        return decimal.Round(Math.Abs(maeReturn.Value) * orderNotional.Value, 8, MidpointRounding.AwayFromZero);
    }

    private static decimal? ResolveAdverseExcursionMagnitude(decimal? maeReturn)
    {
        return maeReturn.HasValue
            ? RoundLabelReturn(Math.Abs(maeReturn.Value))
            : null;
    }

    private static int ResolveRiskViolationCount(DecisionProjection decision)
    {
        var count = 0;
        if (decision.RiskVetoPresent)
        {
            count++;
        }

        if (decision.PilotSafetyBlocked)
        {
            count++;
        }

        return count;
    }

    private static int ResolveStaleDataEntryCount(DecisionProjection decision, FeatureProjection feature)
    {
        var staleReasonCount = 0;
        if (ContainsStaleReason(decision.HypotheticalBlockReason))
        {
            staleReasonCount++;
        }

        if (ContainsStaleReason(decision.NoSubmitReason) &&
            !string.Equals(decision.NoSubmitReason, decision.HypotheticalBlockReason, StringComparison.Ordinal))
        {
            staleReasonCount++;
        }

        if (staleReasonCount > 0)
        {
            return staleReasonCount;
        }

        return ResolveWasBlocked(decision) && feature.MarketDataReasonCode != DegradedModeReasonCode.None
            ? 1
            : 0;
    }

    private static string ResolveLabelCompleteness(
        OutcomeProjection? outcome,
        string? labelDirection,
        decimal? expectedMovePct,
        string outcomeLabel)
    {
        if (outcomeLabel == "Unknown" || outcome?.OutcomeState != AiShadowOutcomeState.Scored)
        {
            return "Unknown";
        }

        return !string.IsNullOrWhiteSpace(labelDirection) && expectedMovePct.HasValue
            ? "Complete"
            : "Partial";
    }

    private static bool ContainsStaleReason(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               (value.Contains("StaleMarketData", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("MissingFreshSignalData", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ResolveBlockReason(DecisionProjection decision)
    {
        return NormalizeOptional(decision.HypotheticalBlockReason, 64)
               ?? (ResolveWasBlocked(decision) ? NormalizeOptional(decision.NoSubmitReason, 64) : null);
    }

    private static string? ResolveLabelDirection(string aiDirection, string strategyDirection)
    {
        var normalizedAiDirection = NormalizeDirection(aiDirection);
        if (normalizedAiDirection is not null)
        {
            return normalizedAiDirection;
        }

        return NormalizeDirection(strategyDirection);
    }

    private static string? ResolveExecutionDirection(ExecutionOrderProjection? executionOrder, string? fallbackDirection)
    {
        if (executionOrder is null)
        {
            return fallbackDirection;
        }

        return executionOrder.Side switch
        {
            ExecutionOrderSide.Sell => "Short",
            _ => "Long"
        };
    }

    private static string? NormalizeDirection(string? value)
    {
        return value?.Trim() switch
        {
            "Long" => "Long",
            "Short" => "Short",
            _ => null
        };
    }

    private static string BuildCsv(
        IReadOnlyCollection<TrainingDatasetRowSnapshot> rows,
        IReadOnlyCollection<string> columnOrder)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", columnOrder.Select(EscapeCsv)));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(",",
                columnOrder.Select(column =>
                    EscapeCsv(row.Values.TryGetValue(column, out var value) ? value : null))));
        }

        return builder.ToString();
    }

    private string BuildFileName(TrainingDatasetBuildSnapshot dataset, TrainingDatasetExportMode exportMode)
    {
        var symbol = string.IsNullOrWhiteSpace(dataset.Symbol) ? "all-symbols" : dataset.Symbol;
        var timeframe = string.IsNullOrWhiteSpace(dataset.Timeframe) ? "all-timeframes" : dataset.Timeframe;
        var exportModeSuffix = exportMode == TrainingDatasetExportMode.Sanitized ? "-sanitized" : string.Empty;
        return $"training-dataset-{symbol}-{timeframe}-{dataset.HorizonKind}-{dataset.HorizonValue}-{DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime):yyyyMMdd}{exportModeSuffix}.csv";
    }

    private static IReadOnlyCollection<string> ResolveExportColumnOrder(TrainingDatasetExportMode exportMode)
    {
        return exportMode == TrainingDatasetExportMode.Sanitized
            ? SanitizedColumnOrder
            : InternalColumnOrder;
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string? FormatGuid(Guid? value)
    {
        return value?.ToString("D");
    }

    private static string? FormatBool(bool? value)
    {
        return value.HasValue ? value.Value ? "true" : "false" : null;
    }

    private static string? FormatDate(DateTime? value)
    {
        return value.HasValue ? NormalizeTimestamp(value.Value).ToString("O", CultureInfo.InvariantCulture) : null;
    }

    private static string? FormatDecimal(decimal? value)
    {
        return value?.ToString("0.##################", CultureInfo.InvariantCulture);
    }

    private static decimal RoundLabelReturn(decimal value)
    {
        return decimal.Round(value, 8, MidpointRounding.AwayFromZero);
    }

    private static NormalizedRequest NormalizeRequest(TrainingDatasetBuildRequest request)
    {
        var normalizedUserId = NormalizeRequired(request.UserId, 450, nameof(request.UserId));
        var normalizedBotId = request.BotId == Guid.Empty ? null : request.BotId;
        var normalizedSymbol = NormalizeOptional(request.Symbol, 32)?.ToUpperInvariant();
        var normalizedTimeframe = NormalizeOptional(request.Timeframe, 16);
        var normalizedFromUtc = request.FromUtc.HasValue ? (DateTime?)NormalizeTimestamp(request.FromUtc.Value) : null;
        var normalizedToUtc = request.ToUtc.HasValue ? (DateTime?)NormalizeTimestamp(request.ToUtc.Value) : null;
        if (normalizedFromUtc.HasValue && normalizedToUtc.HasValue && normalizedFromUtc > normalizedToUtc)
        {
            throw new ArgumentException("The requested dataset time range is invalid because FromUtc is greater than ToUtc.");
        }

        if (request.HorizonKind != AiShadowOutcomeHorizonKind.BarsForward)
        {
            throw new ArgumentOutOfRangeException(nameof(request.HorizonKind), request.HorizonKind, "Only BarsForward training labels are supported.");
        }

        if (request.HorizonValue <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.HorizonValue), request.HorizonValue, "HorizonValue must be greater than zero.");
        }

        return new NormalizedRequest(
            normalizedUserId,
            normalizedBotId,
            normalizedSymbol,
            normalizedTimeframe,
            normalizedFromUtc,
            normalizedToUtc,
            request.HorizonKind,
            Math.Min(request.HorizonValue, 32),
            request.Take <= 0 ? DefaultTake : Math.Min(request.Take, MaxTake),
            request.TrainingEligibleOnly);
    }

    private static string NormalizeRequired(string? value, int maxLength, string parameterName)
    {
        var normalizedValue = NormalizeOptional(value, maxLength);
        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        return normalizedValue;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalizedValue = value.Trim();
        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : normalizedValue[..maxLength];
    }

    private static DateTime NormalizeTimestamp(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private sealed record NormalizedRequest(
        string UserId,
        Guid? BotId,
        string? Symbol,
        string? Timeframe,
        DateTime? FromUtc,
        DateTime? ToUtc,
        AiShadowOutcomeHorizonKind HorizonKind,
        int HorizonValue,
        int Take,
        bool TrainingEligibleOnly);

    private sealed record DecisionProjection(
        Guid Id,
        Guid BotId,
        Guid FeatureSnapshotId,
        Guid? StrategySignalId,
        string CorrelationId,
        string StrategyKey,
        string Symbol,
        string Timeframe,
        DateTime EvaluatedAtUtc,
        DateTime? MarketDataTimestampUtc,
        string StrategyDirection,
        string AiDirection,
        string FinalAction,
        bool HypotheticalSubmitAllowed,
        string? HypotheticalBlockReason,
        string NoSubmitReason,
        bool RiskVetoPresent,
        bool PilotSafetyBlocked);

    private sealed record FeatureProjection(
        Guid Id,
        string UserId,
        Guid BotId,
        Guid? ExchangeAccountId,
        string? CorrelationId,
        string? SnapshotKey,
        string StrategyKey,
        string Symbol,
        string Timeframe,
        DateTime EvaluatedAtUtc,
        DateTime? FeatureAnchorTimeUtc,
        DateTime? MarketDataTimestampUtc,
        string FeatureVersion,
        FeatureSnapshotState SnapshotState,
        FeatureSnapshotQualityReason QualityReasonCode,
        DegradedModeReasonCode MarketDataReasonCode,
        int SampleCount,
        int RequiredSampleCount,
        decimal? ReferencePrice,
        decimal? Ema20,
        decimal? Ema50,
        decimal? Ema200,
        decimal? Alma,
        decimal? Frama,
        decimal? Rsi,
        decimal? MacdLine,
        decimal? MacdSignal,
        decimal? MacdHistogram,
        decimal? KdjK,
        decimal? KdjD,
        decimal? KdjJ,
        decimal? FisherTransform,
        decimal? Atr,
        decimal? BollingerPercentB,
        decimal? BollingerBandWidth,
        decimal? KeltnerChannelRelation,
        decimal? PmaxValue,
        decimal? ChandelierExit,
        decimal? VolumeSpikeRatio,
        decimal? RelativeVolume,
        decimal? Obv,
        decimal? Mfi,
        decimal? KlingerOscillator,
        decimal? KlingerSignal,
        ExchangeDataPlane Plane,
        ExecutionEnvironment TradingMode,
        bool HasOpenPosition,
        bool IsInCooldown,
        string PrimaryRegime,
        string MomentumBias,
        string VolatilityState);

    private sealed record OutcomeProjection(
        Guid AiShadowDecisionId,
        string Symbol,
        string Timeframe,
        AiShadowOutcomeState OutcomeState,
        decimal? OutcomeScore,
        decimal? RealizedReturn,
        string RealizedDirectionality,
        bool FalsePositive,
        bool FalseNeutral,
        bool Overtrading,
        bool SuppressionCandidate,
        bool SuppressionAligned,
        DateTime? ReferenceCandleCloseTimeUtc,
        DateTime? FutureCandleCloseTimeUtc,
        decimal? ReferenceClosePrice);

    private sealed record ExecutionOrderContextProjection(
        ExecutionOrderProjection SelectedOrder,
        int DuplicateOrderCount,
        int ReduceOnlyViolationCount,
        decimal? RealizedPnl);

    private sealed record ExecutionOrderProjection(
        Guid Id,
        Guid StrategySignalId,
        StrategySignalType SignalType,
        ExecutionOrderSide Side,
        decimal Quantity,
        decimal FilledQuantity,
        decimal Price,
        decimal? AverageFillPrice,
        ExecutionOrderState State,
        bool SubmittedToBroker,
        bool ReduceOnly,
        bool DuplicateSuppressed,
        string? FailureCode,
        ExecutionRejectionStage RejectionStage,
        decimal? StopLossPrice,
        decimal? TakeProfitPrice,
        DateTime LastStateChangedAtUtc,
        DateTime CreatedDate);

    private sealed record CandleProjection(
        DateTime CloseTimeUtc,
        decimal HighPrice,
        decimal LowPrice,
        decimal ClosePrice,
        DateTime ReceivedAtUtc);

    private readonly record struct MarketKey(string Symbol, string Timeframe);

    private sealed record DatasetSourceRow(
        Guid FeatureSnapshotId,
        Guid AiShadowDecisionId,
        DateTime AnchorTimeUtc,
        bool IsTrainingEligible,
        IReadOnlyDictionary<string, string?> Values);

    private readonly record struct ExcursionSnapshot(decimal? MfeReturn, decimal? MaeReturn)
    {
        public static ExcursionSnapshot Empty => new(null, null);
    }
}
