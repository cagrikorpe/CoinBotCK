using CoinBot.Application.Abstractions.Alerts;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Execution;

public sealed class DataLatencyCircuitBreaker(
    ApplicationDbContext dbContext,
    IAlertService alertService,
    IOptions<DataLatencyGuardOptions> options,
    TimeProvider timeProvider,
    ILogger<DataLatencyCircuitBreaker> logger,
    IMarketDataService? marketDataService = null) : IDataLatencyCircuitBreaker
{
    private readonly DataLatencyGuardOptions optionsValue = options.Value;

    public async Task<DegradedModeSnapshot> GetSnapshotAsync(
        string? correlationId = null,
        string? symbol = null,
        string? timeframe = null,
        CancellationToken cancellationToken = default)
    {
        var evaluationTimeUtc = timeProvider.GetUtcNow().UtcDateTime;
        var state = await GetOrCreateTrackedStateAsync(
            evaluationTimeUtc,
            symbol,
            timeframe,
            cancellationToken);
        var hasSharedSnapshotRefresh = await TrySynchronizeScopedStateFromSharedKlineAsync(
            state,
            evaluationTimeUtc,
            symbol,
            timeframe,
            cancellationToken);
        var previousSnapshot = MapSnapshot(state, evaluationTimeUtc, isPersisted: true);
        var desiredState = EvaluateState(state, evaluationTimeUtc);
        var previousReasonCode = state.ReasonCode;
        var hasStateChanged = ApplyState(state, desiredState, evaluationTimeUtc);
        UpdateContinuityHistory(state, previousReasonCode, desiredState.ReasonCode, evaluationTimeUtc);
        var wasCreated = dbContext.Entry(state).State == EntityState.Added;

        if (wasCreated || hasSharedSnapshotRefresh || hasStateChanged)
        {
            state = await SaveStateAsync(state, cancellationToken);
        }

        if (hasStateChanged)
        {
            LogStateTransition(desiredState);
            await SendAlertIfStateChangedAsync(previousSnapshot, desiredState, state, correlationId, cancellationToken);
        }

        return MapSnapshot(state, evaluationTimeUtc, isPersisted: true);
    }

    private async Task<bool> TrySynchronizeScopedStateFromSharedKlineAsync(
        DegradedModeState state,
        DateTime evaluationTimeUtc,
        string? symbol,
        string? timeframe,
        CancellationToken cancellationToken)
    {
        if (marketDataService is null ||
            string.IsNullOrWhiteSpace(symbol) ||
            string.IsNullOrWhiteSpace(timeframe))
        {
            return false;
        }

        var normalizedSymbol = NormalizeOptional(symbol, maxLength: 32);
        var normalizedTimeframe = NormalizeOptional(timeframe, maxLength: 16);
        if (normalizedSymbol is null || normalizedTimeframe is null)
        {
            return false;
        }

        var previousDataTimestampUtc = state.LatestDataTimestampAtUtc;
        var previousHeartbeatReceivedAtUtc = state.LatestHeartbeatReceivedAtUtc;
        var previousClockDriftMilliseconds = state.LatestClockDriftMilliseconds;
        var previousHeartbeatSource = state.LatestHeartbeatSource;
        var previousExpectedOpenTimeUtc = state.LatestExpectedOpenTimeUtc;
        var previousContinuityGapCount = state.LatestContinuityGapCount;

        var sharedKlineRead = await marketDataService.ReadLatestKlineAsync(
            normalizedSymbol,
            normalizedTimeframe,
            cancellationToken);

        var hasSharedKlineHit = sharedKlineRead.Status is SharedMarketDataCacheReadStatus.HitFresh or SharedMarketDataCacheReadStatus.HitStale;
        if (hasSharedKlineHit &&
            sharedKlineRead.Entry?.Payload is MarketCandleSnapshot sharedSnapshot)
        {
            var sharedDataTimestampUtc = NormalizeTimestamp(sharedKlineRead.Entry.UpdatedAtUtc);
            var sharedHeartbeatReceivedAtUtc = NormalizeTimestamp(sharedSnapshot.ReceivedAtUtc);
            state.LatestDataTimestampAtUtc = sharedDataTimestampUtc;
            state.LatestHeartbeatReceivedAtUtc = sharedHeartbeatReceivedAtUtc;
            state.LatestClockDriftMilliseconds = ResolveSharedKlineClockDriftMilliseconds(sharedSnapshot, sharedDataTimestampUtc);
            state.LatestHeartbeatSource = sharedKlineRead.Entry.Source;
            state.LatestSymbol = normalizedSymbol;
            state.LatestTimeframe = normalizedTimeframe;
            if (previousExpectedOpenTimeUtc.HasValue &&
                HasReachedExpectedOpenTime(sharedDataTimestampUtc, sharedHeartbeatReceivedAtUtc, previousExpectedOpenTimeUtc.Value))
            {
                state.LatestContinuityGapCount = 0;
            }

            state.LatestExpectedOpenTimeUtc = NormalizeTimestamp(sharedSnapshot.CloseTimeUtc).AddMilliseconds(1);

            return state.LatestDataTimestampAtUtc != previousDataTimestampUtc ||
                state.LatestHeartbeatReceivedAtUtc != previousHeartbeatReceivedAtUtc ||
                state.LatestClockDriftMilliseconds != previousClockDriftMilliseconds ||
                !string.Equals(state.LatestHeartbeatSource, previousHeartbeatSource, StringComparison.Ordinal) ||
                state.LatestExpectedOpenTimeUtc != previousExpectedOpenTimeUtc ||
                state.LatestContinuityGapCount != previousContinuityGapCount;
        }

        if (sharedKlineRead.Status == SharedMarketDataCacheReadStatus.Miss ||
            sharedKlineRead.Status == SharedMarketDataCacheReadStatus.ProviderUnavailable ||
            sharedKlineRead.Status == SharedMarketDataCacheReadStatus.DeserializeFailed ||
            sharedKlineRead.Status == SharedMarketDataCacheReadStatus.InvalidPayload)
        {
            var latestHeartbeatSource = $"shared-cache:kline:{sharedKlineRead.Status}";

            if (state.LatestDataTimestampAtUtc is not null)
            {
                state.LatestHeartbeatSource = latestHeartbeatSource;
                state.LatestSymbol = normalizedSymbol;
                state.LatestTimeframe = normalizedTimeframe;

                return !string.Equals(state.LatestHeartbeatSource, previousHeartbeatSource, StringComparison.Ordinal);
            }

            state.LatestDataTimestampAtUtc = null;
            state.LatestHeartbeatReceivedAtUtc = null;
            state.LatestClockDriftMilliseconds = null;
            state.LatestHeartbeatSource = latestHeartbeatSource;
            state.LatestSymbol = normalizedSymbol;
            state.LatestTimeframe = normalizedTimeframe;
            state.LatestExpectedOpenTimeUtc = null;
            state.LatestContinuityGapCount = null;

            return state.LatestDataTimestampAtUtc != previousDataTimestampUtc ||
                state.LatestHeartbeatReceivedAtUtc != previousHeartbeatReceivedAtUtc ||
                state.LatestClockDriftMilliseconds != previousClockDriftMilliseconds ||
                !string.Equals(state.LatestHeartbeatSource, previousHeartbeatSource, StringComparison.Ordinal) ||
                state.LatestExpectedOpenTimeUtc != previousExpectedOpenTimeUtc;
        }

        return false;
    }

    public async Task<DegradedModeSnapshot> RecordHeartbeatAsync(
        DataLatencyHeartbeat heartbeat,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);

        var heartbeatSource = NormalizeRequired(heartbeat.Source, nameof(heartbeat.Source), 64);
        var symbol = NormalizeOptional(heartbeat.Symbol, maxLength: 32);
        var timeframe = NormalizeOptional(heartbeat.Timeframe, maxLength: 16);
        var hasScopedState = !string.IsNullOrWhiteSpace(symbol) &&
            !string.IsNullOrWhiteSpace(timeframe);
        var evaluationTimeUtc = timeProvider.GetUtcNow().UtcDateTime;
        var dataTimestampUtc = NormalizeTimestamp(heartbeat.DataTimestampUtc);
        var heartbeatReceivedAtUtc = NormalizeTimestamp(heartbeat.HeartbeatReceivedAtUtc ?? evaluationTimeUtc);
        var clockDriftMilliseconds = ResolveHeartbeatClockDriftMilliseconds(heartbeatSource, heartbeatReceivedAtUtc, dataTimestampUtc);
        var scopedSnapshot = await RecordHeartbeatForScopeAsync(
            evaluationTimeUtc,
            dataTimestampUtc,
            heartbeatReceivedAtUtc,
            clockDriftMilliseconds,
            heartbeatSource,
            symbol,
            timeframe,
            heartbeat.GuardStateCode,
            heartbeat.GuardReasonCode,
            heartbeat.ExpectedOpenTimeUtc,
            heartbeat.ContinuityGapCount,
            correlationId,
            sendAlertOnStateChange: !hasScopedState,
            cancellationToken);

        if (hasScopedState)
        {
            await RecordHeartbeatForScopeAsync(
                evaluationTimeUtc,
                dataTimestampUtc,
            heartbeatReceivedAtUtc,
                clockDriftMilliseconds,
                heartbeatSource,
                symbol: null,
                timeframe: null,
                heartbeat.GuardStateCode,
                heartbeat.GuardReasonCode,
                heartbeat.ExpectedOpenTimeUtc,
                heartbeat.ContinuityGapCount,
                correlationId,
                sendAlertOnStateChange: true,
                cancellationToken,
                latestSymbolOverride: symbol,
                latestTimeframeOverride: timeframe);
        }

        return scopedSnapshot;
    }

    private async Task<DegradedModeSnapshot> RecordHeartbeatForScopeAsync(
        DateTime evaluationTimeUtc,
        DateTime dataTimestampUtc,
        DateTime heartbeatReceivedAtUtc,
        int clockDriftMilliseconds,
        string heartbeatSource,
        string? symbol,
        string? timeframe,
        DegradedModeStateCode? guardStateCode,
        DegradedModeReasonCode? guardReasonCode,
        DateTime? expectedOpenTimeUtc,
        int? continuityGapCount,
        string? correlationId,
        bool sendAlertOnStateChange,
        CancellationToken cancellationToken,
        string? latestSymbolOverride = null,
        string? latestTimeframeOverride = null)
    {
        var state = await GetOrCreateTrackedStateAsync(
            evaluationTimeUtc,
            symbol,
            timeframe,
            cancellationToken);
        var previousSnapshot = MapSnapshot(state, evaluationTimeUtc, isPersisted: true);

        state.LatestDataTimestampAtUtc = dataTimestampUtc;
        state.LatestHeartbeatReceivedAtUtc = heartbeatReceivedAtUtc;
        state.LatestClockDriftMilliseconds = clockDriftMilliseconds;
        state.LatestHeartbeatSource = heartbeatSource;
        state.LatestSymbol = latestSymbolOverride ?? symbol;
        state.LatestTimeframe = latestTimeframeOverride ?? timeframe;
        state.LatestExpectedOpenTimeUtc = expectedOpenTimeUtc is null
            ? null
            : NormalizeTimestamp(expectedOpenTimeUtc.Value);
        state.LatestContinuityGapCount = continuityGapCount is null
            ? null
            : Math.Max(0, continuityGapCount.Value);

        var previousReasonCode = state.ReasonCode;
        var desiredState = EvaluateState(
            state,
            evaluationTimeUtc,
            guardStateCode,
            guardReasonCode);
        var hasStateChanged = ApplyState(state, desiredState, evaluationTimeUtc);
        UpdateContinuityHistory(state, previousReasonCode, desiredState.ReasonCode, evaluationTimeUtc);

        state = await SaveStateAsync(state, cancellationToken);

        if (hasStateChanged)
        {
            logger.LogWarning(
                "Data latency circuit breaker moved to {StateCode} with reason {ReasonCode} after heartbeat source {HeartbeatSource} for {Symbol} {Timeframe}.",
                desiredState.StateCode,
                desiredState.ReasonCode,
                heartbeatSource,
                state.LatestSymbol ?? "missing",
                state.LatestTimeframe ?? "missing");

            if (sendAlertOnStateChange)
            {
                await SendAlertIfStateChangedAsync(previousSnapshot, desiredState, state, correlationId, cancellationToken);
            }
        }
        else
        {
            logger.LogDebug(
                "Data latency heartbeat from {HeartbeatSource} for {Symbol} {Timeframe} kept the circuit breaker at {StateCode} with continuity gap count {ContinuityGapCount}.",
                heartbeatSource,
                state.LatestSymbol ?? "missing",
                state.LatestTimeframe ?? "missing",
                desiredState.StateCode,
                state.LatestContinuityGapCount ?? 0);
        }

        return MapSnapshot(state, evaluationTimeUtc, isPersisted: true);
    }

    private async Task<DegradedModeState> GetOrCreateTrackedStateAsync(
        DateTime evaluationTimeUtc,
        string? symbol,
        string? timeframe,
        CancellationToken cancellationToken)
    {
        var stateId = DegradedModeDefaults.ResolveStateId(symbol, timeframe);
        var state = await dbContext.DegradedModeStates
            .SingleOrDefaultAsync(entity => entity.Id == stateId, cancellationToken);

        if (state is null)
        {
            state = DegradedModeDefaults.CreateEntity(evaluationTimeUtc, symbol, timeframe);
            dbContext.DegradedModeStates.Add(state);
            return state;
        }

        if (state.IsDeleted)
        {
            state.IsDeleted = false;
        }

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            state.LatestSymbol = symbol;
        }

        if (!string.IsNullOrWhiteSpace(timeframe))
        {
            state.LatestTimeframe = timeframe;
        }

        return state;
    }
    private async Task<DegradedModeState> SaveStateAsync(DegradedModeState state, CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return state;
        }
        catch (DbUpdateException exception) when (IsDuplicateStateInsert(state, exception))
        {
            dbContext.Entry(state).State = EntityState.Detached;
            var persistedState = await dbContext.DegradedModeStates
                .SingleAsync(entity => entity.Id == state.Id, cancellationToken);

            CopyState(state, persistedState);
            await dbContext.SaveChangesAsync(cancellationToken);
            return persistedState;
        }
    }

    private bool IsDuplicateStateInsert(DegradedModeState state, DbUpdateException exception)
    {
        return dbContext.Entry(state).State == EntityState.Added &&
               exception.InnerException is SqlException { Number: 2601 or 2627 };
    }

    private static void CopyState(DegradedModeState source, DegradedModeState target)
    {
        target.LatestDataTimestampAtUtc = source.LatestDataTimestampAtUtc;
        target.LatestHeartbeatReceivedAtUtc = source.LatestHeartbeatReceivedAtUtc;
        target.LatestClockDriftMilliseconds = source.LatestClockDriftMilliseconds;
        target.LatestHeartbeatSource = source.LatestHeartbeatSource;
        target.LatestSymbol = source.LatestSymbol;
        target.LatestTimeframe = source.LatestTimeframe;
        target.LatestExpectedOpenTimeUtc = source.LatestExpectedOpenTimeUtc;
        target.LatestContinuityGapCount = source.LatestContinuityGapCount;
        target.LatestContinuityGapStartedAtUtc = source.LatestContinuityGapStartedAtUtc;
        target.LatestContinuityGapLastSeenAtUtc = source.LatestContinuityGapLastSeenAtUtc;
        target.LatestContinuityRecoveredAtUtc = source.LatestContinuityRecoveredAtUtc;
        target.LastStateChangedAtUtc = source.LastStateChangedAtUtc;
        target.StateCode = source.StateCode;
        target.ReasonCode = source.ReasonCode;
        target.SignalFlowBlocked = source.SignalFlowBlocked;
        target.ExecutionFlowBlocked = source.ExecutionFlowBlocked;
        target.IsDeleted = false;
    }

    private DesiredState EvaluateState(
        DegradedModeState state,
        DateTime evaluationTimeUtc,
        DegradedModeStateCode? guardStateCode = null,
        DegradedModeReasonCode? guardReasonCode = null)
    {
        if (state.LatestDataTimestampAtUtc is null)
        {
            return new DesiredState(
                DegradedModeStateCode.Stopped,
                DegradedModeReasonCode.MarketDataUnavailable,
                SignalFlowBlocked: true,
                ExecutionFlowBlocked: true,
                LatestDataAgeMilliseconds: null);
        }

        var latestDataAgeMilliseconds = ToMilliseconds(Math.Max(
            0,
            (evaluationTimeUtc - state.LatestDataTimestampAtUtc.Value).TotalMilliseconds));

        if (state.LatestContinuityGapCount == 0 &&
            IsMarketDataHeartbeatSource(state.LatestHeartbeatSource) &&
            latestDataAgeMilliseconds < StaleDataThresholdMilliseconds)
        {
            return new DesiredState(
                DegradedModeStateCode.Normal,
                DegradedModeReasonCode.None,
                SignalFlowBlocked: false,
                ExecutionFlowBlocked: false,
                LatestDataAgeMilliseconds: latestDataAgeMilliseconds);
        }

        var hasExplicitGuardReason = guardReasonCode.HasValue;
        var explicitGuardReasonCode = guardReasonCode.GetValueOrDefault();

        if (hasExplicitGuardReason &&
            explicitGuardReasonCode != DegradedModeReasonCode.None &&
            IsContinuityGuardReason(explicitGuardReasonCode))
        {
            return new DesiredState(
                guardStateCode ?? DegradedModeStateCode.Stopped,
                explicitGuardReasonCode,
                SignalFlowBlocked: true,
                ExecutionFlowBlocked: true,
                LatestDataAgeMilliseconds: latestDataAgeMilliseconds);
        }

        if (!hasExplicitGuardReason &&
            IsContinuityGuardReason(state.ReasonCode) &&
            ShouldKeepContinuityGuardActive(state))
        {
            return new DesiredState(
                state.StateCode == DegradedModeStateCode.Normal
                    ? DegradedModeStateCode.Stopped
                    : state.StateCode,
                state.ReasonCode,
                SignalFlowBlocked: true,
                ExecutionFlowBlocked: true,
                LatestDataAgeMilliseconds: latestDataAgeMilliseconds);
        }

        if (hasExplicitGuardReason && explicitGuardReasonCode != DegradedModeReasonCode.None)
        {
            return new DesiredState(
                guardStateCode ?? DegradedModeStateCode.Stopped,
                explicitGuardReasonCode,
                SignalFlowBlocked: true,
                ExecutionFlowBlocked: true,
                LatestDataAgeMilliseconds: latestDataAgeMilliseconds);
        }

        if (ShouldTreatClockDriftAsBlocking(state, latestDataAgeMilliseconds, guardReasonCode))
        {
            return new DesiredState(
                DegradedModeStateCode.Stopped,
                DegradedModeReasonCode.ClockDriftExceeded,
                SignalFlowBlocked: true,
                ExecutionFlowBlocked: true,
                LatestDataAgeMilliseconds: latestDataAgeMilliseconds);
        }

        if (latestDataAgeMilliseconds >= StopDataThresholdMilliseconds)
        {
            return new DesiredState(
                DegradedModeStateCode.Stopped,
                DegradedModeReasonCode.MarketDataLatencyCritical,
                SignalFlowBlocked: true,
                ExecutionFlowBlocked: true,
                LatestDataAgeMilliseconds: latestDataAgeMilliseconds);
        }

        if (latestDataAgeMilliseconds >= StaleDataThresholdMilliseconds)
        {
            return new DesiredState(
                DegradedModeStateCode.Degraded,
                DegradedModeReasonCode.MarketDataLatencyBreached,
                SignalFlowBlocked: true,
                ExecutionFlowBlocked: true,
                LatestDataAgeMilliseconds: latestDataAgeMilliseconds);
        }

        return new DesiredState(
            DegradedModeStateCode.Normal,
            DegradedModeReasonCode.None,
            SignalFlowBlocked: false,
            ExecutionFlowBlocked: false,
            LatestDataAgeMilliseconds: latestDataAgeMilliseconds);
    }

    private static bool ApplyState(DegradedModeState state, DesiredState desiredState, DateTime evaluationTimeUtc)
    {
        var hasStateChanged = state.StateCode != desiredState.StateCode ||
                              state.ReasonCode != desiredState.ReasonCode ||
                              state.SignalFlowBlocked != desiredState.SignalFlowBlocked ||
                              state.ExecutionFlowBlocked != desiredState.ExecutionFlowBlocked;

        state.StateCode = desiredState.StateCode;
        state.ReasonCode = desiredState.ReasonCode;
        state.SignalFlowBlocked = desiredState.SignalFlowBlocked;
        state.ExecutionFlowBlocked = desiredState.ExecutionFlowBlocked;

        if (hasStateChanged)
        {
            state.LastStateChangedAtUtc = evaluationTimeUtc;
        }

        return hasStateChanged;
    }

    private static void UpdateContinuityHistory(
        DegradedModeState state,
        DegradedModeReasonCode previousReasonCode,
        DegradedModeReasonCode desiredReasonCode,
        DateTime evaluationTimeUtc)
    {
        if (IsContinuityGuardReason(desiredReasonCode))
        {
            if (!state.LatestContinuityGapStartedAtUtc.HasValue ||
                !IsContinuityGuardReason(previousReasonCode))
            {
                state.LatestContinuityGapStartedAtUtc = state.LatestExpectedOpenTimeUtc ?? evaluationTimeUtc;
            }

            state.LatestContinuityGapLastSeenAtUtc = evaluationTimeUtc;
            state.LatestContinuityRecoveredAtUtc = null;
            return;
        }

        if (IsContinuityGuardReason(previousReasonCode))
        {
            state.LatestContinuityRecoveredAtUtc = evaluationTimeUtc;
        }
    }

    private async Task SendAlertIfStateChangedAsync(
        DegradedModeSnapshot previousSnapshot,
        DesiredState desiredState,
        DegradedModeState state,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (previousSnapshot.StateCode == desiredState.StateCode &&
            previousSnapshot.ReasonCode == desiredState.ReasonCode &&
            previousSnapshot.SignalFlowBlocked == desiredState.SignalFlowBlocked &&
            previousSnapshot.ExecutionFlowBlocked == desiredState.ExecutionFlowBlocked)
        {
            return;
        }

        await alertService.SendAsync(
            CreateNotification(desiredState, state, correlationId),
            cancellationToken);
    }

    private AlertNotification CreateNotification(
        DesiredState desiredState,
        DegradedModeState state,
        string? correlationId)
    {
        var title = desiredState.StateCode switch
        {
            DegradedModeStateCode.Normal => "Degraded mode recovered",
            DegradedModeStateCode.Degraded => "Data latency guard entered degraded mode",
            _ when IsContinuityGuardReason(desiredState.ReasonCode) => "Candle data quality guard stopped signal and execution",
            _ => "Data latency guard stopped signal and execution"
        };

        var message = $"State={desiredState.StateCode}; Reason={desiredState.ReasonCode}; DataAgeMs={FormatNullableInt(desiredState.LatestDataAgeMilliseconds)}; ClockDriftMs={FormatNullableInt(state.LatestClockDriftMilliseconds)}; LatestDataTimestampUtc={state.LatestDataTimestampAtUtc?.ToString("O") ?? "missing"}; HeartbeatSource={state.LatestHeartbeatSource ?? "missing"}; Symbol={state.LatestSymbol ?? "missing"}; Timeframe={state.LatestTimeframe ?? "missing"}; ExpectedOpenTimeUtc={state.LatestExpectedOpenTimeUtc?.ToString("O") ?? "missing"}; ContinuityGapCount={FormatNullableInt(state.LatestContinuityGapCount)}; SignalBlocked={desiredState.SignalFlowBlocked}; ExecutionBlocked={desiredState.ExecutionFlowBlocked}.";

        return new AlertNotification(
            Code: $"DEGRADED_MODE_{desiredState.StateCode.ToString().ToUpperInvariant()}_{desiredState.ReasonCode.ToString().ToUpperInvariant()}",
            Severity: MapSeverity(desiredState.StateCode),
            Title: title,
            Message: message,
            CorrelationId: correlationId);
    }

    private void LogStateTransition(DesiredState desiredState)
    {
        if (desiredState.StateCode == DegradedModeStateCode.Normal)
        {
            logger.LogInformation(
                "Data latency circuit breaker recovered to {StateCode} with reason {ReasonCode}.",
                desiredState.StateCode,
                desiredState.ReasonCode);

            return;
        }

        logger.LogWarning(
            "Data latency circuit breaker entered {StateCode} with reason {ReasonCode}.",
            desiredState.StateCode,
            desiredState.ReasonCode);
    }

    private DegradedModeSnapshot MapSnapshot(DegradedModeState state, DateTime evaluationTimeUtc, bool isPersisted)
    {
        return new DegradedModeSnapshot(
            state.StateCode,
            state.ReasonCode,
            state.SignalFlowBlocked,
            state.ExecutionFlowBlocked,
            state.LatestDataTimestampAtUtc,
            state.LatestHeartbeatReceivedAtUtc,
            state.LatestDataTimestampAtUtc is null
                ? null
                : ToMilliseconds(Math.Max(0, (evaluationTimeUtc - state.LatestDataTimestampAtUtc.Value).TotalMilliseconds)),
            state.LatestClockDriftMilliseconds,
            state.LastStateChangedAtUtc,
            isPersisted,
            state.LatestHeartbeatSource,
            state.LatestSymbol,
            state.LatestTimeframe,
            state.LatestExpectedOpenTimeUtc,
            state.LatestContinuityGapCount,
            state.LatestContinuityGapStartedAtUtc,
            state.LatestContinuityGapLastSeenAtUtc,
            state.LatestContinuityRecoveredAtUtc);
    }

    private int StaleDataThresholdMilliseconds => checked(optionsValue.StaleDataThresholdSeconds * 1000);

    private int StopDataThresholdMilliseconds => checked(optionsValue.StopDataThresholdSeconds * 1000);

    private bool ShouldTreatClockDriftAsBlocking(
        DegradedModeState state,
        int latestDataAgeMilliseconds,
        DegradedModeReasonCode? guardReasonCode)
    {
        if (state.LatestClockDriftMilliseconds is not int clockDriftMilliseconds ||
            clockDriftMilliseconds < ClockDriftThresholdMilliseconds)
        {
            return false;
        }

        if (latestDataAgeMilliseconds < StaleDataThresholdMilliseconds)
        {
            return true;
        }

        if ((guardReasonCode.HasValue && guardReasonCode.Value != DegradedModeReasonCode.None) ||
            state.ReasonCode == DegradedModeReasonCode.MarketDataLatencyCritical)
        {
            return true;
        }

        if (latestDataAgeMilliseconds >= StopDataThresholdMilliseconds)
        {
            return true;
        }

        return !IsMarketDataHeartbeatSource(state.LatestHeartbeatSource);
    }

    private static bool IsMarketDataHeartbeatSource(string? heartbeatSource)
    {
        if (string.IsNullOrWhiteSpace(heartbeatSource))
        {
            return false;
        }

        return heartbeatSource.StartsWith("binance:kline", StringComparison.OrdinalIgnoreCase) ||
               heartbeatSource.StartsWith("shared-cache:kline", StringComparison.OrdinalIgnoreCase) ||
               heartbeatSource.StartsWith("Binance.Rest.Kline", StringComparison.OrdinalIgnoreCase) ||
               heartbeatSource.StartsWith("Binance.WebSocket.Kline", StringComparison.OrdinalIgnoreCase) ||
               heartbeatSource.StartsWith("binance:rest-backfill", StringComparison.OrdinalIgnoreCase) ||
               heartbeatSource.StartsWith("market-scanner:historical-candles", StringComparison.OrdinalIgnoreCase);
    }

    private int ClockDriftThresholdMilliseconds => checked(optionsValue.ClockDriftThresholdSeconds * 1000);

    private static AlertSeverity MapSeverity(DegradedModeStateCode stateCode)
    {
        return stateCode switch
        {
            DegradedModeStateCode.Normal => AlertSeverity.Information,
            DegradedModeStateCode.Degraded => AlertSeverity.Warning,
            _ => AlertSeverity.Critical
        };
    }

    private static bool IsContinuityGuardReason(DegradedModeReasonCode reasonCode)
    {
        return reasonCode is DegradedModeReasonCode.CandleDataGapDetected or
            DegradedModeReasonCode.CandleDataDuplicateDetected or
            DegradedModeReasonCode.CandleDataOutOfOrderDetected;
    }

    private static bool ShouldKeepContinuityGuardActive(DegradedModeState state)
    {
        if (!IsContinuityGuardReason(state.ReasonCode))
        {
            return false;
        }

        if (!IsMarketDataHeartbeatSource(state.LatestHeartbeatSource))
        {
            return true;
        }

        if (state.LatestContinuityGapCount == 0)
        {
            return false;
        }

        if (state.LatestDataTimestampAtUtc is null)
        {
            return true;
        }

        if (state.LatestExpectedOpenTimeUtc is null)
        {
            return true;
        }

        return !HasReachedExpectedOpenTime(state);
    }

    private static bool HasReachedExpectedOpenTime(DegradedModeState state)
    {
        if (state.LatestExpectedOpenTimeUtc is not DateTime expectedOpenTimeUtc)
        {
            return false;
        }

        if (state.LatestDataTimestampAtUtc?.AddMilliseconds(1) >= expectedOpenTimeUtc)
        {
            return true;
        }

        return state.LatestHeartbeatReceivedAtUtc?.AddMilliseconds(1) >= expectedOpenTimeUtc;
    }

    private static bool HasReachedExpectedOpenTime(
        DateTime dataTimestampUtc,
        DateTime heartbeatReceivedAtUtc,
        DateTime expectedOpenTimeUtc)
    {
        return dataTimestampUtc.AddMilliseconds(1) >= expectedOpenTimeUtc ||
               heartbeatReceivedAtUtc.AddMilliseconds(1) >= expectedOpenTimeUtc;
    }

    private static int ResolveHeartbeatClockDriftMilliseconds(
        string heartbeatSource,
        DateTime heartbeatReceivedAtUtc,
        DateTime dataTimestampUtc)
    {
        if (heartbeatSource.StartsWith("binance:rest-backfill", StringComparison.OrdinalIgnoreCase) ||
            heartbeatSource.StartsWith("market-scanner:historical-candles", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return ToMilliseconds(Math.Abs((heartbeatReceivedAtUtc - dataTimestampUtc).TotalMilliseconds));
    }


    private static int ResolveSharedKlineClockDriftMilliseconds(
        MarketCandleSnapshot sharedSnapshot,
        DateTime sharedDataTimestampUtc)
    {
        if (sharedSnapshot.Source.StartsWith("Binance.Rest.", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var sharedHeartbeatReceivedAtUtc = NormalizeTimestamp(sharedSnapshot.ReceivedAtUtc);
        return ToMilliseconds(Math.Abs((sharedHeartbeatReceivedAtUtc - sharedDataTimestampUtc).TotalMilliseconds));
    }


    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return null;
        }

        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : normalizedValue[..maxLength];
    }

    private static string NormalizeRequired(string? value, string parameterName, int maxLength)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        if (normalizedValue.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"The value cannot exceed {maxLength} characters.");
        }

        return normalizedValue;
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

    private static int ToMilliseconds(double value)
    {
        if (value <= 0)
        {
            return 0;
        }

        if (value >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private static string FormatNullableInt(int? value)
    {
        return value?.ToString() ?? "missing";
    }

    private sealed record DesiredState(
        DegradedModeStateCode StateCode,
        DegradedModeReasonCode ReasonCode,
        bool SignalFlowBlocked,
        bool ExecutionFlowBlocked,
        int? LatestDataAgeMilliseconds);
}
