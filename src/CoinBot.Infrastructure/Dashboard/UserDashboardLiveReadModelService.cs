using CoinBot.Application.Abstractions.Dashboard;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Dashboard;

public sealed class UserDashboardLiveReadModelService(
    ApplicationDbContext dbContext,
    IGlobalExecutionSwitchService globalExecutionSwitchService,
    IOptions<BotExecutionPilotOptions> pilotOptions,
    TimeProvider timeProvider) : IUserDashboardLiveReadModelService
{
    private readonly BotExecutionPilotOptions optionsValue = pilotOptions.Value;

    public async Task<UserDashboardLiveSnapshot> GetSnapshotAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = dbContext.EnsureCurrentUserScope(userId);
        var switchSnapshot = await globalExecutionSwitchService.GetSnapshotAsync(cancellationToken);
        var degradedModeState = await dbContext.DegradedModeStates
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(entity => entity.Id == DegradedModeDefaults.SingletonId, cancellationToken);
        var latestSyncState = await dbContext.ExchangeAccountSyncStates
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == normalizedUserId &&
                entity.Plane == ExchangeDataPlane.Futures &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.LastPrivateStreamEventAtUtc ?? entity.LastBalanceSyncedAtUtc ?? entity.LastPositionSyncedAtUtc ?? entity.LastStateReconciledAtUtc)
            .ThenByDescending(entity => entity.UpdatedDate)
            .FirstOrDefaultAsync(cancellationToken);
        var recentAiDecisions = await dbContext.AiShadowDecisions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => entity.OwnerUserId == normalizedUserId && !entity.IsDeleted)
            .OrderByDescending(entity => entity.EvaluatedAtUtc)
            .ThenByDescending(entity => entity.CreatedDate)
            .Take(24)
            .ToListAsync(cancellationToken);
        var featureSnapshotIds = recentAiDecisions
            .Where(entity => entity.FeatureSnapshotId.HasValue)
            .Select(entity => entity.FeatureSnapshotId!.Value)
            .Distinct()
            .ToArray();
        var featureSnapshots = featureSnapshotIds.Length == 0
            ? new Dictionary<Guid, TradingFeatureSnapshot>()
            : await dbContext.TradingFeatureSnapshots
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity => featureSnapshotIds.Contains(entity.Id) && !entity.IsDeleted)
                .ToDictionaryAsync(entity => entity.Id, cancellationToken);
        var latestRejectedOrder = await dbContext.ExecutionOrders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == normalizedUserId &&
                !entity.IsDeleted &&
                (entity.State == ExecutionOrderState.Rejected || entity.FailureCode != null))
            .OrderByDescending(entity => entity.CreatedDate)
            .ThenByDescending(entity => entity.UpdatedDate)
            .FirstOrDefaultAsync(cancellationToken);

        return new UserDashboardLiveSnapshot(
            BuildControlSnapshot(switchSnapshot, degradedModeState, latestSyncState),
            BuildLatestNoTradeSnapshot(recentAiDecisions.FirstOrDefault()),
            BuildLatestRejectSnapshot(latestRejectedOrder),
            BuildAiSummarySnapshot(recentAiDecisions),
            recentAiDecisions.Select(entity => MapAiHistoryRow(entity, featureSnapshots)).ToArray(),
            BuildBuckets(recentAiDecisions.Select(entity => entity.NoSubmitReason)),
            BuildBuckets(recentAiDecisions.Select(entity => entity.HypotheticalBlockReason)));
    }

    private UserDashboardLiveControlSnapshot BuildControlSnapshot(
        GlobalExecutionSwitchSnapshot switchSnapshot,
        DegradedModeState? degradedModeState,
        ExchangeAccountSyncState? latestSyncState)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var (tradeMasterLabel, tradeMasterTone) = switchSnapshot.IsPersisted
            ? switchSnapshot.IsTradeMasterArmed
                ? ("Armed", "positive")
                : ("Disarmed", "negative")
            : ("Unconfigured", "warning");
        var (tradingModeLabel, tradingModeTone) = switchSnapshot.DemoModeEnabled
            ? ("DemoOnly", "warning")
            : ("LiveAllowed", "negative");
        var (pilotActivationLabel, pilotActivationTone) = optionsValue.PilotActivationEnabled
            ? ("PilotEnabled", "positive")
            : ("ShadowOnly", "neutral");
        var (marketDataLabel, marketDataTone, marketDataSummary) = MapMarketDataState(degradedModeState);
        var (privatePlaneLabel, privatePlaneTone, privatePlaneSummary) = MapPrivatePlaneState(latestSyncState, nowUtc);

        return new UserDashboardLiveControlSnapshot(
            tradeMasterLabel,
            tradeMasterTone,
            tradingModeLabel,
            tradingModeTone,
            pilotActivationLabel,
            pilotActivationTone,
            marketDataLabel,
            marketDataTone,
            marketDataSummary,
            privatePlaneLabel,
            privatePlaneTone,
            privatePlaneSummary);
    }

    private static (string Label, string Tone, string Summary) MapMarketDataState(DegradedModeState? state)
    {
        if (state is null)
        {
            return ("Unknown", "neutral", "Henüz market-data readiness snapshot yok.");
        }

        var label = !state.ExecutionFlowBlocked && state.StateCode == DegradedModeStateCode.Normal
            ? "Fresh"
            : state.StateCode == DegradedModeStateCode.Degraded
                ? "Degraded"
                : "Blocked";
        var tone = label switch
        {
            "Fresh" => "positive",
            "Degraded" => "warning",
            _ => "negative"
        };
        var summary = $"Reason={state.ReasonCode}; Source={NormalizeOptional(state.LatestHeartbeatSource) ?? "n/a"}; Symbol={NormalizeOptional(state.LatestSymbol) ?? "n/a"}; Timeframe={NormalizeOptional(state.LatestTimeframe) ?? "n/a"}; DataAt={FormatUtc(state.LatestDataTimestampAtUtc)}; HeartbeatAt={FormatUtc(state.LatestHeartbeatReceivedAtUtc)}";

        return (label, tone, summary);
    }

    private (string Label, string Tone, string Summary) MapPrivatePlaneState(
        ExchangeAccountSyncState? state,
        DateTime nowUtc)
    {
        if (state is null)
        {
            return ("Unknown", "neutral", "Private plane sync snapshot yok.");
        }

        var freshnessTimestamp = MaxTimestamp(
            state.LastPrivateStreamEventAtUtc,
            state.LastBalanceSyncedAtUtc,
            state.LastPositionSyncedAtUtc,
            state.LastStateReconciledAtUtc);
        var freshnessAgeSeconds = freshnessTimestamp.HasValue
            ? Math.Max(0, (int)Math.Round((nowUtc - freshnessTimestamp.Value).TotalSeconds, MidpointRounding.AwayFromZero))
            : (int?)null;
        var isStale = !freshnessTimestamp.HasValue || freshnessAgeSeconds > optionsValue.PrivatePlaneFreshnessThresholdSeconds;
        var label = isStale
            ? "Stale"
            : state.PrivateStreamConnectionState == ExchangePrivateStreamConnectionState.Connected && state.DriftStatus == ExchangeStateDriftStatus.InSync
                ? "Fresh"
                : state.PrivateStreamConnectionState == ExchangePrivateStreamConnectionState.Reconnecting ||
                  state.PrivateStreamConnectionState == ExchangePrivateStreamConnectionState.Connecting ||
                  state.DriftStatus == ExchangeStateDriftStatus.DriftDetected
                    ? "Warning"
                    : state.PrivateStreamConnectionState.ToString();
        var tone = label switch
        {
            "Fresh" => "positive",
            "Warning" => "warning",
            "Stale" => "negative",
            "Connected" => "positive",
            "Reconnecting" or "Connecting" => "warning",
            _ => "negative"
        };
        var summary = $"Stream={state.PrivateStreamConnectionState}; Drift={state.DriftStatus}; LastPrivateEvent={FormatUtc(state.LastPrivateStreamEventAtUtc)}; LastBalanceSync={FormatUtc(state.LastBalanceSyncedAtUtc)}; LastPositionSync={FormatUtc(state.LastPositionSyncedAtUtc)}; FreshnessAgeSec={(freshnessAgeSeconds?.ToString() ?? "n/a")}";

        return (label, tone, summary);
    }

    private static UserDashboardLatestNoTradeSnapshot BuildLatestNoTradeSnapshot(AiShadowDecision? entity)
    {
        if (entity is null)
        {
            return new UserDashboardLatestNoTradeSnapshot(
                "NoShadowData",
                "neutral",
                null,
                "Henüz AI shadow kaydı yok.",
                null);
        }

        var tone = entity.FinalAction == "NoSubmit"
            ? entity.RiskVetoPresent || entity.PilotSafetyBlocked
                ? "negative"
                : "warning"
            : "info";
        var summary = $"AI={entity.AiDirection} {entity.AiConfidence:P0}; Strategy={entity.StrategyDirection}; NoSubmit={entity.NoSubmitReason}; Hypothetical={(entity.HypotheticalSubmitAllowed ? "Allowed" : NormalizeOptional(entity.HypotheticalBlockReason) ?? "Blocked")}; Reason={entity.AiReasonSummary}";

        return new UserDashboardLatestNoTradeSnapshot(
            entity.FinalAction,
            tone,
            entity.NoSubmitReason,
            summary,
            entity.EvaluatedAtUtc);
    }

    private static UserDashboardLatestRejectSnapshot BuildLatestRejectSnapshot(ExecutionOrder? entity)
    {
        if (entity is null)
        {
            return new UserDashboardLatestRejectSnapshot(
                "NoReject",
                "neutral",
                null,
                "Son execution reject kaydı yok.",
                null,
                null);
        }

        var summary = NormalizeOptional(entity.FailureDetail)
            ?? NormalizeOptional(entity.ReconciliationSummary)
            ?? $"{entity.Symbol} {entity.Timeframe} execution reject kaydı.";
        var reconciliationLabel = entity.ReconciliationStatus != ExchangeStateDriftStatus.Unknown
            ? entity.ReconciliationStatus.ToString()
            : null;

        return new UserDashboardLatestRejectSnapshot(
            entity.State.ToString(),
            "negative",
            NormalizeOptional(entity.FailureCode),
            summary,
            reconciliationLabel,
            entity.CreatedDate);
    }

    private static UserDashboardAiSummarySnapshot BuildAiSummarySnapshot(IReadOnlyCollection<AiShadowDecision> rows)
    {
        var averageConfidence = rows.Count == 0
            ? 0m
            : decimal.Round(rows.Average(entity => entity.AiConfidence), 4, MidpointRounding.AwayFromZero);

        return new UserDashboardAiSummarySnapshot(
            rows.Count,
            rows.Count(entity => string.Equals(entity.AiDirection, "Long", StringComparison.Ordinal)),
            rows.Count(entity => string.Equals(entity.AiDirection, "Short", StringComparison.Ordinal)),
            rows.Count(entity => string.Equals(entity.AiDirection, "Neutral", StringComparison.Ordinal)),
            rows.Count(entity => entity.AiIsFallback),
            rows.Count(entity => string.Equals(entity.AgreementState, "Agreement", StringComparison.Ordinal)),
            rows.Count(entity => string.Equals(entity.AgreementState, "Disagreement", StringComparison.Ordinal)),
            averageConfidence,
            rows.Count(entity => entity.AiConfidence >= 0.70m),
            rows.Count(entity => entity.AiConfidence >= 0.40m && entity.AiConfidence < 0.70m),
            rows.Count(entity => entity.AiConfidence < 0.40m));
    }

    private static UserDashboardAiHistoryRowSnapshot MapAiHistoryRow(
        AiShadowDecision entity,
        IReadOnlyDictionary<Guid, TradingFeatureSnapshot> featureSnapshots)
    {
        featureSnapshots.TryGetValue(entity.FeatureSnapshotId ?? Guid.Empty, out var featureSnapshot);

        return new UserDashboardAiHistoryRowSnapshot(
            entity.Id,
            entity.BotId,
            entity.Symbol,
            entity.Timeframe,
            entity.EvaluatedAtUtc,
            entity.StrategyDirection,
            entity.StrategyConfidenceScore,
            entity.StrategyDecisionOutcome,
            entity.StrategyDecisionCode,
            entity.StrategySummary,
            entity.AiDirection,
            entity.AiConfidence,
            entity.AiReasonSummary,
            entity.AiProviderName,
            entity.AiProviderModel,
            entity.AiIsFallback,
            entity.AiFallbackReason,
            entity.RiskVetoPresent,
            entity.RiskVetoReason,
            entity.RiskVetoSummary,
            entity.PilotSafetyBlocked,
            entity.PilotSafetyReason,
            entity.PilotSafetySummary,
            entity.FinalAction,
            entity.HypotheticalSubmitAllowed,
            entity.HypotheticalBlockReason,
            entity.HypotheticalBlockSummary,
            entity.NoSubmitReason,
            entity.AgreementState,
            entity.FeatureSnapshotId,
            entity.FeatureVersion,
            entity.FeatureSummary,
            featureSnapshot?.TopSignalHints,
            featureSnapshot?.PrimaryRegime,
            featureSnapshot?.MomentumBias,
            featureSnapshot?.VolatilityState,
            entity.TradingMode,
            entity.Plane);
    }

    private static IReadOnlyCollection<UserDashboardReasonBucketSnapshot> BuildBuckets(IEnumerable<string?> values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .GroupBy(value => value, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(5)
            .Select(group => new UserDashboardReasonBucketSnapshot(group.Key, group.Count()))
            .ToArray();
    }

    private static DateTime? MaxTimestamp(params DateTime?[] values)
    {
        return values.Where(value => value.HasValue).Select(value => value!.Value).DefaultIfEmpty().Max();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static string FormatUtc(DateTime? value)
    {
        return value.HasValue
            ? value.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'")
            : "n/a";
    }
}

