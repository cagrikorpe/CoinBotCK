using CoinBot.Application.Abstractions.Alerts;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Execution;

public sealed class DataLatencyCircuitBreaker(
    ApplicationDbContext dbContext,
    IAlertService alertService,
    IOptions<DataLatencyGuardOptions> options,
    TimeProvider timeProvider,
    ILogger<DataLatencyCircuitBreaker> logger) : IDataLatencyCircuitBreaker
{
    private readonly DataLatencyGuardOptions optionsValue = options.Value;

    public async Task<DegradedModeSnapshot> GetSnapshotAsync(
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var evaluationTimeUtc = timeProvider.GetUtcNow().UtcDateTime;
        var state = await GetOrCreateTrackedStateAsync(evaluationTimeUtc, cancellationToken);
        var previousSnapshot = MapSnapshot(state, evaluationTimeUtc, isPersisted: true);
        var desiredState = EvaluateState(state, evaluationTimeUtc);
        var hasStateChanged = ApplyState(state, desiredState, evaluationTimeUtc);
        var wasCreated = dbContext.Entry(state).State == EntityState.Added;

        if (wasCreated || hasStateChanged)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (hasStateChanged)
        {
            LogStateTransition(desiredState);
            await SendAlertIfStateChangedAsync(previousSnapshot, desiredState, state, correlationId, cancellationToken);
        }

        return MapSnapshot(state, evaluationTimeUtc, isPersisted: true);
    }

    public async Task<DegradedModeSnapshot> RecordHeartbeatAsync(
        DataLatencyHeartbeat heartbeat,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);

        var heartbeatSource = NormalizeRequired(heartbeat.Source, nameof(heartbeat.Source), 64);
        var evaluationTimeUtc = timeProvider.GetUtcNow().UtcDateTime;
        var dataTimestampUtc = NormalizeTimestamp(heartbeat.DataTimestampUtc);
        var clockDriftMilliseconds = ToMilliseconds(Math.Abs((evaluationTimeUtc - dataTimestampUtc).TotalMilliseconds));
        var state = await GetOrCreateTrackedStateAsync(evaluationTimeUtc, cancellationToken);
        var previousSnapshot = MapSnapshot(state, evaluationTimeUtc, isPersisted: true);

        state.LatestDataTimestampAtUtc = dataTimestampUtc;
        state.LatestHeartbeatReceivedAtUtc = evaluationTimeUtc;
        state.LatestClockDriftMilliseconds = clockDriftMilliseconds;

        var desiredState = EvaluateState(
            state,
            evaluationTimeUtc,
            heartbeat.GuardStateCode,
            heartbeat.GuardReasonCode);
        var hasStateChanged = ApplyState(state, desiredState, evaluationTimeUtc);

        await dbContext.SaveChangesAsync(cancellationToken);

        if (hasStateChanged)
        {
            logger.LogWarning(
                "Data latency circuit breaker moved to {StateCode} with reason {ReasonCode} after heartbeat source {HeartbeatSource}.",
                desiredState.StateCode,
                desiredState.ReasonCode,
                heartbeatSource);

            await SendAlertIfStateChangedAsync(previousSnapshot, desiredState, state, correlationId, cancellationToken);
        }
        else
        {
            logger.LogDebug(
                "Data latency heartbeat from {HeartbeatSource} kept the circuit breaker at {StateCode}.",
                heartbeatSource,
                desiredState.StateCode);
        }

        return MapSnapshot(state, evaluationTimeUtc, isPersisted: true);
    }

    private async Task<DegradedModeState> GetOrCreateTrackedStateAsync(DateTime evaluationTimeUtc, CancellationToken cancellationToken)
    {
        var state = await dbContext.DegradedModeStates
            .SingleOrDefaultAsync(entity => entity.Id == DegradedModeDefaults.SingletonId, cancellationToken);

        if (state is null)
        {
            state = DegradedModeDefaults.CreateEntity(evaluationTimeUtc);
            dbContext.DegradedModeStates.Add(state);
            return state;
        }

        if (state.IsDeleted)
        {
            state.IsDeleted = false;
        }

        return state;
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

        if (guardReasonCode is DegradedModeReasonCode explicitGuardReasonCode)
        {
            if (explicitGuardReasonCode != DegradedModeReasonCode.None)
            {
                return new DesiredState(
                    guardStateCode ?? DegradedModeStateCode.Stopped,
                    explicitGuardReasonCode,
                    SignalFlowBlocked: true,
                    ExecutionFlowBlocked: true,
                    LatestDataAgeMilliseconds: latestDataAgeMilliseconds);
            }
        }
        else if (IsContinuityGuardReason(state.ReasonCode))
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

        if (state.LatestClockDriftMilliseconds is int clockDriftMilliseconds &&
            clockDriftMilliseconds >= ClockDriftThresholdMilliseconds)
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

        var message = $"State={desiredState.StateCode}; Reason={desiredState.ReasonCode}; DataAgeMs={FormatNullableInt(desiredState.LatestDataAgeMilliseconds)}; ClockDriftMs={FormatNullableInt(state.LatestClockDriftMilliseconds)}; LatestDataTimestampUtc={state.LatestDataTimestampAtUtc?.ToString("O") ?? "missing"}; SignalBlocked={desiredState.SignalFlowBlocked}; ExecutionBlocked={desiredState.ExecutionFlowBlocked}.";

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
            isPersisted);
    }

    private int StaleDataThresholdMilliseconds => checked(optionsValue.StaleDataThresholdSeconds * 1000);

    private int StopDataThresholdMilliseconds => checked(optionsValue.StopDataThresholdSeconds * 1000);

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

