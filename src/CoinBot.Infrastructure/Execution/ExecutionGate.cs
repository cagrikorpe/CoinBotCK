using System.Diagnostics;
using System.Text.Json;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DemoPortfolio;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Observability;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Execution;

public sealed class ExecutionGate(
    IDemoSessionService demoSessionService,
    IGlobalSystemStateService globalSystemStateService,
    IGlobalExecutionSwitchService globalExecutionSwitchService,
    IDataLatencyCircuitBreaker dataLatencyCircuitBreaker,
    ITradingModeResolver tradingModeResolver,
    IAuditLogService auditLogService,
    ILogger<ExecutionGate> logger,
    IHostEnvironment? hostEnvironment = null,
    ITraceService? traceService = null,
    TimeProvider? timeProvider = null,
    IOptions<DataLatencyGuardOptions>? dataLatencyGuardOptions = null) : IExecutionGate
{
    private readonly ITraceService? traceWriter = traceService;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly int staleThresholdMilliseconds = checked((dataLatencyGuardOptions?.Value ?? new DataLatencyGuardOptions()).StaleDataThresholdSeconds * 1000);

    public async Task<GlobalExecutionSwitchSnapshot> EnsureExecutionAllowedAsync(
        ExecutionGateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (TryResolveAdministrativeOverrideReason(request.Context, out var administrativeOverrideReason))
        {
            var switchSnapshot = await globalExecutionSwitchService.GetSnapshotAsync(cancellationToken);
            var administrativeSnapshot = CreateAdministrativeOverrideSnapshot(request);
            var administrativeDecision = CreateDecisionDescriptor(
                request,
                isBlocked: false,
                reasonCode: ExecutionDecisionDiagnostics.AllowedDecisionCode,
                summary: "Administrative override allowed the request.",
                administrativeSnapshot,
                clock.GetUtcNow().UtcDateTime);

            await auditLogService.WriteAsync(
                new AuditLogWriteRequest(
                    request.Actor,
                    request.Action,
                    request.Target,
                    BuildAuditContext(
                        request.Context,
                        administrativeSnapshot,
                        administrativeDecision,
                        administrativeOverrideReason: administrativeOverrideReason),
                    request.CorrelationId,
                    "Allowed:AdministrativeOverride",
                    request.Environment.ToString()),
                cancellationToken);

            await WriteDecisionTraceAsync(
                request,
                administrativeDecision,
                administrativeSnapshot,
                cancellationToken,
                administrativeOverrideReason: administrativeOverrideReason);

            logger.LogWarning(
                "Execution gate accepted an administrative override for {Target}. Reason={AdministrativeOverrideReason}",
                request.Target,
                administrativeOverrideReason);

            return switchSnapshot;
        }

        using var signalActivity = CoinBotActivity.StartActivity("CoinBot.Signal.Intake");
        ApplyTags(signalActivity, request);
        logger.LogInformation(
            "Signal stage accepted an execution decision request for {ExecutionEnvironment}.",
            request.Environment);

        var (latencySnapshot, latencyBlockedReason) = await EvaluateDataLatencyAsync(
            request,
            signalActivity,
            cancellationToken);

        signalActivity.SetTag("coinbot.signal.result", latencyBlockedReason?.ToString() ?? "Accepted");

        if (latencyBlockedReason is not null)
        {
            using var executionActivity = CoinBotActivity.StartActivity("CoinBot.Execution.Gate");
            ApplyTags(executionActivity, request);
            executionActivity.SetTag("coinbot.execution.result", latencyBlockedReason.ToString());
            var latencyDecision = CreateDecisionDescriptor(
                request,
                isBlocked: true,
                reasonCode: latencyBlockedReason.Value.ToString(),
                summary: ExecutionDecisionDiagnostics.ExtractHumanSummary(
                    CreateMessage(latencyBlockedReason.Value, request.Environment, latencySnapshot)) ??
                    CreateMessage(latencyBlockedReason.Value, request.Environment, latencySnapshot),
                latencySnapshot,
                clock.GetUtcNow().UtcDateTime);

            await auditLogService.WriteAsync(
                new AuditLogWriteRequest(
                    request.Actor,
                    request.Action,
                    request.Target,
                    BuildAuditContext(request.Context, latencySnapshot, latencyDecision),
                    request.CorrelationId,
                    MapOutcome(latencyBlockedReason.Value),
                    request.Environment.ToString()),
                cancellationToken);

            await WriteDecisionTraceAsync(
                request,
                latencyDecision,
                latencySnapshot,
                cancellationToken);

            logger.LogWarning(
                "Signal stage blocked the request with reason {BlockedReason}.",
                latencyBlockedReason);

            throw new ExecutionGateRejectedException(
                latencyBlockedReason.Value,
                request.Environment,
                CreateMessage(latencyBlockedReason.Value, request.Environment, latencySnapshot));
        }

        GlobalSystemStateSnapshot globalSystemStateSnapshot;

        try
        {
            globalSystemStateSnapshot = await globalSystemStateService.GetSnapshotAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            using var executionActivity = CoinBotActivity.StartActivity("CoinBot.Execution.Gate");
            ApplyTags(executionActivity, request);
            executionActivity.SetTag("coinbot.execution.result", "GlobalSystemStateUnavailable");
            var unavailableDecision = CreateDecisionDescriptor(
                request,
                isBlocked: true,
                reasonCode: "GlobalSystemStateUnavailable",
                summary: "Execution blocked because global system state could not be evaluated.",
                latencySnapshot,
                clock.GetUtcNow().UtcDateTime);

            await auditLogService.WriteAsync(
                new AuditLogWriteRequest(
                    request.Actor,
                    request.Action,
                    request.Target,
                    BuildAuditContext(request.Context, latencySnapshot, unavailableDecision),
                    request.CorrelationId,
                    "Blocked:GlobalSystemStateUnavailable",
                    request.Environment.ToString()),
                cancellationToken);

            await WriteDecisionTraceAsync(
                request,
                unavailableDecision,
                latencySnapshot,
                cancellationToken);

            logger.LogWarning(exception, "Execution stage failed closed because global system state could not be evaluated.");

            throw new InvalidOperationException("Execution blocked because global system state could not be evaluated.", exception);
        }

        if (globalSystemStateSnapshot.IsExecutionBlocked)
        {
            using var executionActivity = CoinBotActivity.StartActivity("CoinBot.Execution.Gate");
            ApplyTags(executionActivity, request);
            executionActivity.SetTag("coinbot.execution.result", $"GlobalSystem:{globalSystemStateSnapshot.State}");
            var globalSystemDecision = CreateDecisionDescriptor(
                request,
                isBlocked: true,
                reasonCode: ResolveGlobalSystemDecisionReasonCode(globalSystemStateSnapshot.State),
                summary: CreateGlobalSystemStateMessage(globalSystemStateSnapshot),
                latencySnapshot,
                clock.GetUtcNow().UtcDateTime);

            await auditLogService.WriteAsync(
                new AuditLogWriteRequest(
                    request.Actor,
                    request.Action,
                    request.Target,
                    BuildAuditContext(
                        request.Context,
                        latencySnapshot,
                        globalSystemDecision,
                        globalSystemStateSnapshot: globalSystemStateSnapshot),
                    request.CorrelationId,
                    BuildGlobalSystemOutcome(globalSystemStateSnapshot.State),
                    request.Environment.ToString()),
                cancellationToken);

            await WriteDecisionTraceAsync(
                request,
                globalSystemDecision,
                latencySnapshot,
                cancellationToken,
                globalSystemStateSnapshot: globalSystemStateSnapshot);

            logger.LogWarning(
                "Execution stage blocked because global system state is {GlobalSystemState}.",
                globalSystemStateSnapshot.State);

            throw new InvalidOperationException(CreateGlobalSystemStateMessage(globalSystemStateSnapshot));
        }

        DemoSessionSnapshot? demoSessionSnapshot = null;

        if (request.Environment == ExecutionEnvironment.Demo &&
            !string.IsNullOrWhiteSpace(request.UserId))
        {
            demoSessionSnapshot = await demoSessionService.RunConsistencyCheckAsync(request.UserId, cancellationToken);

            if (demoSessionSnapshot is not null &&
                demoSessionSnapshot.ConsistencyStatus == DemoConsistencyStatus.DriftDetected)
            {
                using var executionActivity = CoinBotActivity.StartActivity("CoinBot.Execution.Gate");
                ApplyTags(executionActivity, request);
                executionActivity.SetTag("coinbot.execution.result", ExecutionGateBlockedReason.DemoSessionDriftDetected.ToString());
                var demoSessionDecision = CreateDecisionDescriptor(
                    request,
                    isBlocked: true,
                    reasonCode: ExecutionGateBlockedReason.DemoSessionDriftDetected.ToString(),
                    summary: CreateMessage(ExecutionGateBlockedReason.DemoSessionDriftDetected, request.Environment, latencySnapshot),
                    latencySnapshot,
                    clock.GetUtcNow().UtcDateTime);

                await auditLogService.WriteAsync(
                new AuditLogWriteRequest(
                    request.Actor,
                    request.Action,
                    request.Target,
                    BuildAuditContext(
                        request.Context,
                        latencySnapshot,
                        demoSessionDecision,
                        demoSessionSnapshot: demoSessionSnapshot,
                        globalSystemStateSnapshot: globalSystemStateSnapshot),
                    request.CorrelationId,
                    MapOutcome(ExecutionGateBlockedReason.DemoSessionDriftDetected),
                    request.Environment.ToString()),
                    cancellationToken);

                await WriteDecisionTraceAsync(
                    request,
                    demoSessionDecision,
                    latencySnapshot,
                    cancellationToken,
                    demoSessionSnapshot: demoSessionSnapshot,
                    globalSystemStateSnapshot: globalSystemStateSnapshot);

                logger.LogWarning("Execution stage blocked the request because the active demo session drifted.");

                throw new ExecutionGateRejectedException(
                    ExecutionGateBlockedReason.DemoSessionDriftDetected,
                    request.Environment,
                    CreateMessage(ExecutionGateBlockedReason.DemoSessionDriftDetected, request.Environment, latencySnapshot));
            }
        }

        var snapshot = await globalExecutionSwitchService.GetSnapshotAsync(cancellationToken);
        TradingModeResolution modeResolution;

        using (var riskActivity = CoinBotActivity.StartActivity("CoinBot.Risk.Resolve"))
        {
            ApplyTags(riskActivity, request);
            modeResolution = await tradingModeResolver.ResolveAsync(
                new TradingModeResolutionRequest(
                    request.UserId,
                    request.BotId,
                    request.StrategyKey),
                cancellationToken);

            riskActivity.SetTag("coinbot.risk.effective_mode", modeResolution.EffectiveMode.ToString());
            riskActivity.SetTag("coinbot.risk.resolution_source", modeResolution.ResolutionSource.ToString());

            logger.LogInformation(
                "Risk stage resolved mode {EffectiveMode} from {ResolutionSource}.",
                modeResolution.EffectiveMode,
                modeResolution.ResolutionSource);
        }

        using var finalExecutionActivity = CoinBotActivity.StartActivity("CoinBot.Execution.Gate");
        ApplyTags(finalExecutionActivity, request);
        ApplyLatencyTags(finalExecutionActivity, latencySnapshot);
        var blockedReason = Evaluate(
            snapshot,
            request.Environment,
            modeResolution,
            IsDevelopmentFuturesPilotOverrideAllowed(request));
        finalExecutionActivity.SetTag("coinbot.execution.result", blockedReason?.ToString() ?? "Allowed");
        var finalDecision = CreateDecisionDescriptor(
            request,
            isBlocked: blockedReason is not null,
            reasonCode: blockedReason?.ToString() ?? ExecutionDecisionDiagnostics.AllowedDecisionCode,
            summary: blockedReason is null
                ? "Execution decision allowed the request."
                : ExecutionDecisionDiagnostics.ExtractHumanSummary(CreateMessage(blockedReason.Value, request.Environment, latencySnapshot)) ??
                    CreateMessage(blockedReason.Value, request.Environment, latencySnapshot),
            latencySnapshot,
            clock.GetUtcNow().UtcDateTime);

        await auditLogService.WriteAsync(
            new AuditLogWriteRequest(
                request.Actor,
                request.Action,
                request.Target,
                BuildAuditContext(
                    request.Context,
                    latencySnapshot,
                    finalDecision,
                    modeResolution,
                    demoSessionSnapshot,
                    globalSystemStateSnapshot),
                request.CorrelationId,
                blockedReason is null ? "Allowed" : MapOutcome(blockedReason.Value),
                request.Environment.ToString()),
            cancellationToken);

        await WriteDecisionTraceAsync(
            request,
            finalDecision,
            latencySnapshot,
            cancellationToken,
            modeResolution,
            demoSessionSnapshot,
            globalSystemStateSnapshot);

        if (blockedReason is not null)
        {
            logger.LogWarning(
                "Execution stage blocked the request with reason {BlockedReason}.",
                blockedReason);

            throw new ExecutionGateRejectedException(
                blockedReason.Value,
                request.Environment,
                CreateMessage(blockedReason.Value, request.Environment, latencySnapshot));
        }

        logger.LogInformation("Execution stage allowed the request.");

        return snapshot;
    }

    private async Task<(DegradedModeSnapshot Snapshot, ExecutionGateBlockedReason? BlockedReason)> EvaluateDataLatencyAsync(
        ExecutionGateRequest request,
        Activity signalActivity,
        CancellationToken cancellationToken)
    {
        using var marketDataActivity = CoinBotActivity.StartActivity("CoinBot.MarketData.LatencyGuard");
        ApplyTags(marketDataActivity, request);

        try
        {
            var snapshot = await dataLatencyCircuitBreaker.GetSnapshotAsync(
                request.CorrelationId,
                request.Symbol,
                request.Timeframe,
                cancellationToken);
            ApplyLatencyTags(signalActivity, snapshot);
            ApplyLatencyTags(marketDataActivity, snapshot);

            logger.LogInformation(
                "Market data latency state {StateCode} resolved with reason {ReasonCode}.",
                snapshot.StateCode,
                snapshot.ReasonCode);

            return (snapshot, ResolveLatencyBlockedReason(snapshot));
        }
        catch
        {
            logger.LogWarning("Data latency circuit breaker failed while evaluating the request.");

            var unavailableSnapshot = new DegradedModeSnapshot(
                DegradedModeStateCode.Stopped,
                DegradedModeReasonCode.MarketDataUnavailable,
                SignalFlowBlocked: true,
                ExecutionFlowBlocked: true,
                LatestDataTimestampAtUtc: null,
                LatestHeartbeatReceivedAtUtc: null,
                LatestDataAgeMilliseconds: null,
                LatestClockDriftMilliseconds: null,
                LastStateChangedAtUtc: null,
                IsPersisted: false);

            ApplyLatencyTags(signalActivity, unavailableSnapshot);
            ApplyLatencyTags(marketDataActivity, unavailableSnapshot);

            return (unavailableSnapshot, ExecutionGateBlockedReason.DataLatencyGuardUnavailable);
        }
    }

    private static void ApplyTags(Activity activity, ExecutionGateRequest request)
    {
        activity.SetTag("coinbot.execution.action", request.Action);
        activity.SetTag("coinbot.execution.target", request.Target);
        activity.SetTag("coinbot.execution.environment", request.Environment.ToString());

        if (!string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            activity.SetTag("coinbot.correlation_id", request.CorrelationId);
        }
    }

    private static void ApplyLatencyTags(Activity activity, DegradedModeSnapshot snapshot)
    {
        activity.SetTag("coinbot.market_data.state", snapshot.StateCode.ToString());
        activity.SetTag("coinbot.market_data.reason", snapshot.ReasonCode.ToString());
        activity.SetTag("coinbot.market_data.signal_blocked", snapshot.SignalFlowBlocked);
        activity.SetTag("coinbot.market_data.execution_blocked", snapshot.ExecutionFlowBlocked);

        if (snapshot.LatestDataAgeMilliseconds is int latestDataAgeMilliseconds)
        {
            activity.SetTag("coinbot.market_data.age_ms", latestDataAgeMilliseconds);
        }

        if (snapshot.LatestClockDriftMilliseconds is int latestClockDriftMilliseconds)
        {
            activity.SetTag("coinbot.market_data.clock_drift_ms", latestClockDriftMilliseconds);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.LatestHeartbeatSource))
        {
            activity.SetTag("coinbot.market_data.heartbeat_source", snapshot.LatestHeartbeatSource);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.LatestSymbol))
        {
            activity.SetTag("coinbot.market_data.symbol", snapshot.LatestSymbol);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.LatestTimeframe))
        {
            activity.SetTag("coinbot.market_data.timeframe", snapshot.LatestTimeframe);
        }

        if (snapshot.LatestExpectedOpenTimeUtc is DateTime latestExpectedOpenTimeUtc)
        {
            activity.SetTag("coinbot.market_data.expected_open_time_utc", latestExpectedOpenTimeUtc.ToString("O"));
        }

        if (snapshot.LatestContinuityGapCount is int latestContinuityGapCount)
        {
            activity.SetTag("coinbot.market_data.continuity_gap_count", latestContinuityGapCount);
        }

        activity.SetTag("coinbot.market_data.decision_source_layer", ResolveDecisionSourceLayer(snapshot));
        activity.SetTag("coinbot.market_data.decision_method", "ExecutionGate.EvaluateDataLatencyAsync");
    }

    private static ExecutionGateBlockedReason? ResolveLatencyBlockedReason(DegradedModeSnapshot snapshot)
    {
        if (!snapshot.SignalFlowBlocked && !snapshot.ExecutionFlowBlocked)
        {
            return null;
        }

        return snapshot.ReasonCode switch
        {
            DegradedModeReasonCode.MarketDataUnavailable => ExecutionGateBlockedReason.MarketDataUnavailable,
            DegradedModeReasonCode.ClockDriftExceeded => ExecutionGateBlockedReason.ClockDriftExceeded,
            DegradedModeReasonCode.MarketDataLatencyBreached => ExecutionGateBlockedReason.StaleMarketData,
            DegradedModeReasonCode.MarketDataLatencyCritical => ExecutionGateBlockedReason.StaleMarketData,
            DegradedModeReasonCode.CandleDataGapDetected => ExecutionGateBlockedReason.ContinuityGap,
            DegradedModeReasonCode.CandleDataDuplicateDetected => ExecutionGateBlockedReason.ContinuityGap,
            DegradedModeReasonCode.CandleDataOutOfOrderDetected => ExecutionGateBlockedReason.ContinuityGap,
            _ => ExecutionGateBlockedReason.StaleMarketData
        };
    }

    private static ExecutionGateBlockedReason? Evaluate(
        GlobalExecutionSwitchSnapshot snapshot,
        ExecutionEnvironment requestedEnvironment,
        TradingModeResolution modeResolution,
        bool allowDevelopmentFuturesPilotOverride)
    {
        if (!snapshot.IsPersisted)
        {
            return ExecutionGateBlockedReason.SwitchConfigurationMissing;
        }

        if (!snapshot.IsTradeMasterArmed)
        {
            return ExecutionGateBlockedReason.TradeMasterDisarmed;
        }

        if (requestedEnvironment == ExecutionEnvironment.Live &&
            snapshot.DemoModeEnabled &&
            !allowDevelopmentFuturesPilotOverride)
        {
            return ExecutionGateBlockedReason.LiveExecutionBlockedByDemoMode;
        }

        if (requestedEnvironment != modeResolution.EffectiveMode &&
            !allowDevelopmentFuturesPilotOverride)
        {
            return ExecutionGateBlockedReason.RequestedEnvironmentDoesNotMatchResolvedMode;
        }

        return null;
    }

    private static string MapOutcome(ExecutionGateBlockedReason blockedReason)
    {
        return blockedReason switch
        {
            ExecutionGateBlockedReason.SwitchConfigurationMissing => "Blocked:SwitchConfigurationMissing",
            ExecutionGateBlockedReason.TradeMasterDisarmed => "Blocked:TradeMasterDisarmed",
            ExecutionGateBlockedReason.LiveExecutionBlockedByDemoMode => "Blocked:LiveExecutionClosedByDemoMode",
            ExecutionGateBlockedReason.RequestedEnvironmentDoesNotMatchResolvedMode => "Blocked:RequestedEnvironmentDoesNotMatchResolvedMode",
            ExecutionGateBlockedReason.MarketDataUnavailable => "Blocked:MarketDataUnavailable",
            ExecutionGateBlockedReason.StaleMarketData => "Blocked:StaleMarketData",
            ExecutionGateBlockedReason.ContinuityGap => "Blocked:ContinuityGap",
            ExecutionGateBlockedReason.ClockDriftExceeded => "Blocked:ClockDriftExceeded",
            ExecutionGateBlockedReason.DataLatencyGuardUnavailable => "Blocked:DataLatencyGuardUnavailable",
            ExecutionGateBlockedReason.DemoSessionDriftDetected => "Blocked:DemoSessionDriftDetected",
            _ => "Blocked:Unknown"
        };
    }

    private static string CreateMessage(
        ExecutionGateBlockedReason blockedReason,
        ExecutionEnvironment requestedEnvironment,
        DegradedModeSnapshot? latencySnapshot = null)
    {
        return blockedReason switch
        {
            ExecutionGateBlockedReason.SwitchConfigurationMissing =>
                "Execution blocked because the global execution switches are not configured.",
            ExecutionGateBlockedReason.TradeMasterDisarmed =>
                "Execution blocked because TradeMaster is disarmed.",
            ExecutionGateBlockedReason.LiveExecutionBlockedByDemoMode =>
                $"Execution blocked because DemoMode is enabled and the {requestedEnvironment} path is closed.",
            ExecutionGateBlockedReason.RequestedEnvironmentDoesNotMatchResolvedMode =>
                $"Execution blocked because the requested {requestedEnvironment} path does not match the resolved trading mode.",
            ExecutionGateBlockedReason.MarketDataUnavailable =>
                CreateLatencyDecisionMessage(
                    "Execution blocked because market data heartbeat is unavailable.",
                    latencySnapshot),
            ExecutionGateBlockedReason.StaleMarketData =>
                CreateLatencyDecisionMessage(
                    "Execution blocked because market data is stale.",
                    latencySnapshot),
            ExecutionGateBlockedReason.ContinuityGap =>
                CreateLatencyDecisionMessage(
                    "Execution blocked because the candle continuity guard is active.",
                    latencySnapshot),
            ExecutionGateBlockedReason.ClockDriftExceeded =>
                CreateLatencyDecisionMessage(
                    "Execution blocked because clock drift exceeded the safety threshold.",
                    latencySnapshot),
            ExecutionGateBlockedReason.DataLatencyGuardUnavailable =>
                "Execution blocked because the data latency guard could not be evaluated.",
            ExecutionGateBlockedReason.DemoSessionDriftDetected =>
                "Execution blocked because the active demo session consistency watchdog detected drift.",
            _ => "Execution blocked by an unknown gate decision."
        };
    }

    private static string BuildGlobalSystemOutcome(GlobalSystemStateKind state)
    {
        return state switch
        {
            GlobalSystemStateKind.SoftHalt => "Blocked:GlobalSystemSoftHalt",
            GlobalSystemStateKind.FullHalt => "Blocked:GlobalSystemFullHalt",
            GlobalSystemStateKind.Maintenance => "Blocked:GlobalSystemMaintenance",
            GlobalSystemStateKind.Degraded => "Blocked:GlobalSystemDegraded",
            _ => "Blocked:GlobalSystemState"
        };
    }

    private static string ResolveGlobalSystemDecisionReasonCode(GlobalSystemStateKind state)
    {
        return state switch
        {
            GlobalSystemStateKind.SoftHalt => "GlobalSystemSoftHalt",
            GlobalSystemStateKind.FullHalt => "GlobalSystemFullHalt",
            GlobalSystemStateKind.Maintenance => "GlobalSystemMaintenance",
            GlobalSystemStateKind.Degraded => "GlobalSystemDegraded",
            _ => "GlobalSystemState"
        };
    }

    private static string CreateGlobalSystemStateMessage(GlobalSystemStateSnapshot snapshot)
    {
        return string.IsNullOrWhiteSpace(snapshot.Message)
            ? $"Execution blocked because global system state is {snapshot.State}."
            : $"Execution blocked because global system state is {snapshot.State}: {snapshot.Message}";
    }

    private static string CreateLatencyDecisionMessage(string baseMessage, DegradedModeSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return baseMessage;
        }

        return $"{baseMessage} LatencyReason={snapshot.ReasonCode}; HeartbeatSource={snapshot.LatestHeartbeatSource ?? "missing"}; Symbol={snapshot.LatestSymbol ?? "missing"}; Timeframe={snapshot.LatestTimeframe ?? "missing"}; LastCandleAtUtc={snapshot.LatestDataTimestampAtUtc?.ToString("O") ?? "missing"}; ExpectedOpenTimeUtc={snapshot.LatestExpectedOpenTimeUtc?.ToString("O") ?? "missing"}; DataAgeMs={snapshot.LatestDataAgeMilliseconds?.ToString() ?? "missing"}; ClockDriftMs={snapshot.LatestClockDriftMilliseconds?.ToString() ?? "missing"}; ContinuityGapCount={snapshot.LatestContinuityGapCount?.ToString() ?? "missing"}; DecisionSourceLayer={ResolveDecisionSourceLayer(snapshot)}; DecisionMethodName={"ExecutionGate.EvaluateDataLatencyAsync"}.";
    }

    private static string ResolveDecisionSourceLayer(DegradedModeSnapshot snapshot)
    {
        return IsContinuityGuardReason(snapshot.ReasonCode)
            ? "continuity-validator"
            : "heartbeat-watchdog";
    }

    private static bool IsContinuityGuardReason(DegradedModeReasonCode reasonCode)
    {
        return reasonCode is DegradedModeReasonCode.CandleDataGapDetected or
            DegradedModeReasonCode.CandleDataDuplicateDetected or
            DegradedModeReasonCode.CandleDataOutOfOrderDetected;
    }

    private DegradedModeSnapshot CreateAdministrativeOverrideSnapshot(ExecutionGateRequest request)
    {
        return new DegradedModeSnapshot(
            DegradedModeStateCode.Normal,
            DegradedModeReasonCode.None,
            SignalFlowBlocked: false,
            ExecutionFlowBlocked: false,
            LatestDataTimestampAtUtc: null,
            LatestHeartbeatReceivedAtUtc: null,
            LatestDataAgeMilliseconds: null,
            LatestClockDriftMilliseconds: null,
            LastStateChangedAtUtc: null,
            IsPersisted: false,
            LatestHeartbeatSource: "administrative-override",
            LatestSymbol: request.Symbol,
            LatestTimeframe: request.Timeframe);
    }

    private ExecutionDecisionDescriptor CreateDecisionDescriptor(
        ExecutionGateRequest request,
        bool isBlocked,
        string reasonCode,
        string summary,
        DegradedModeSnapshot latencySnapshot,
        DateTime decisionAtUtc)
    {
        var normalizedDecisionAtUtc = NormalizeUtc(decisionAtUtc);
        var normalizedReasonCode = ExecutionDecisionDiagnostics.ResolveDecisionReasonCode(
            isBlocked,
            reasonCode,
            latencySnapshot.ReasonCode == DegradedModeReasonCode.None
                ? null
                : latencySnapshot.ReasonCode.ToString());
        var normalizedReasonType = ExecutionDecisionDiagnostics.ResolveDecisionReasonType(
            normalizedReasonCode,
            latencySnapshot.ReasonCode == DegradedModeReasonCode.None
                ? null
                : latencySnapshot.ReasonCode.ToString());
        var normalizedSummary = Truncate(summary, 512) ??
            ExecutionDecisionDiagnostics.ResolveDecisionSummary(isBlocked, normalizedReasonType, normalizedReasonCode, summary);
        var normalizedLastCandleAtUtc = NormalizeUtcNullable(latencySnapshot.LatestDataTimestampAtUtc);
        var normalizedContinuityRecoveredAtUtc = NormalizeUtcNullable(latencySnapshot.LatestContinuityRecoveredAtUtc);

        return new ExecutionDecisionDescriptor(
            ExecutionDecisionDiagnostics.ResolveDecisionOutcome(isBlocked),
            normalizedReasonType,
            normalizedReasonCode,
            normalizedSummary,
            normalizedDecisionAtUtc,
            NormalizeDecisionDimension(request.Symbol, latencySnapshot.LatestSymbol, "n/a"),
            NormalizeDecisionDimension(request.Timeframe, latencySnapshot.LatestTimeframe, "n/a"),
            normalizedLastCandleAtUtc,
            latencySnapshot.LatestDataAgeMilliseconds ?? ResolveDataAgeMilliseconds(normalizedDecisionAtUtc, normalizedLastCandleAtUtc),
            staleThresholdMilliseconds,
            ExecutionDecisionDiagnostics.ResolveStaleReason(
                latencySnapshot.ReasonCode == DegradedModeReasonCode.None
                    ? null
                    : latencySnapshot.ReasonCode.ToString()),
            ExecutionDecisionDiagnostics.ResolveContinuityState(
                latencySnapshot.ReasonCode == DegradedModeReasonCode.None
                    ? null
                    : latencySnapshot.ReasonCode.ToString(),
                latencySnapshot.LatestContinuityGapCount,
                normalizedContinuityRecoveredAtUtc) ?? "Continuity OK",
            latencySnapshot.LatestContinuityGapCount,
            NormalizeUtcNullable(latencySnapshot.LatestContinuityGapStartedAtUtc),
            NormalizeUtcNullable(latencySnapshot.LatestContinuityGapLastSeenAtUtc),
            normalizedContinuityRecoveredAtUtc);
    }

    private async Task WriteDecisionTraceAsync(
        ExecutionGateRequest request,
        ExecutionDecisionDescriptor decisionDescriptor,
        DegradedModeSnapshot latencySnapshot,
        CancellationToken cancellationToken,
        TradingModeResolution? modeResolution = null,
        DemoSessionSnapshot? demoSessionSnapshot = null,
        GlobalSystemStateSnapshot? globalSystemStateSnapshot = null,
        string? administrativeOverrideReason = null)
    {
        if (traceWriter is null)
        {
            return;
        }

        await traceWriter.WriteDecisionTraceAsync(
            new DecisionTraceWriteRequest(
                UserId: ResolveDecisionUserId(request.UserId),
                Symbol: decisionDescriptor.Symbol,
                Timeframe: decisionDescriptor.Timeframe,
                StrategyVersion: NormalizeDecisionDimension(request.StrategyKey, null, "ExecutionGate"),
                SignalType: "ExecutionGate",
                DecisionOutcome: decisionDescriptor.Outcome,
                SnapshotJson: BuildDecisionTracePayload(
                    request,
                    decisionDescriptor,
                    latencySnapshot,
                    modeResolution,
                    demoSessionSnapshot,
                    globalSystemStateSnapshot,
                    administrativeOverrideReason),
                LatencyMs: 0,
                CorrelationId: request.CorrelationId,
                DecisionReasonType: decisionDescriptor.ReasonType,
                DecisionReasonCode: decisionDescriptor.ReasonCode,
                DecisionSummary: decisionDescriptor.Summary,
                DecisionAtUtc: decisionDescriptor.DecisionAtUtc,
                LastCandleAtUtc: decisionDescriptor.LastCandleAtUtc,
                DataAgeMs: decisionDescriptor.DataAgeMs,
                StaleThresholdMs: decisionDescriptor.StaleThresholdMs,
                StaleReason: decisionDescriptor.StaleReason,
                ContinuityState: decisionDescriptor.ContinuityState,
                ContinuityGapCount: decisionDescriptor.ContinuityGapCount,
                ContinuityGapStartedAtUtc: decisionDescriptor.ContinuityGapStartedAtUtc,
                ContinuityGapLastSeenAtUtc: decisionDescriptor.ContinuityGapLastSeenAtUtc,
                ContinuityRecoveredAtUtc: decisionDescriptor.ContinuityRecoveredAtUtc,
                CreatedAtUtc: decisionDescriptor.DecisionAtUtc),
            cancellationToken);
    }

    private static string BuildDecisionTracePayload(
        ExecutionGateRequest request,
        ExecutionDecisionDescriptor decisionDescriptor,
        DegradedModeSnapshot latencySnapshot,
        TradingModeResolution? modeResolution,
        DemoSessionSnapshot? demoSessionSnapshot,
        GlobalSystemStateSnapshot? globalSystemStateSnapshot,
        string? administrativeOverrideReason)
    {
        return JsonSerializer.Serialize(
            new
            {
                DecisionOutcome = decisionDescriptor.Outcome,
                DecisionReasonType = decisionDescriptor.ReasonType,
                DecisionReasonCode = decisionDescriptor.ReasonCode,
                DecisionSummary = decisionDescriptor.Summary,
                DecisionAtUtc = decisionDescriptor.DecisionAtUtc,
                request.Actor,
                request.Action,
                request.Target,
                Environment = request.Environment.ToString(),
                request.CorrelationId,
                request.UserId,
                request.BotId,
                request.StrategyKey,
                Symbol = decisionDescriptor.Symbol,
                Timeframe = decisionDescriptor.Timeframe,
                LastCandleAtUtc = decisionDescriptor.LastCandleAtUtc,
                DataAgeMs = decisionDescriptor.DataAgeMs,
                StaleThresholdMs = decisionDescriptor.StaleThresholdMs,
                StaleReason = decisionDescriptor.StaleReason,
                ContinuityState = decisionDescriptor.ContinuityState,
                ContinuityGapCount = decisionDescriptor.ContinuityGapCount,
                ContinuityGapStartedAtUtc = decisionDescriptor.ContinuityGapStartedAtUtc,
                ContinuityGapLastSeenAtUtc = decisionDescriptor.ContinuityGapLastSeenAtUtc,
                ContinuityRecoveredAtUtc = decisionDescriptor.ContinuityRecoveredAtUtc,
                LatencyState = latencySnapshot.StateCode.ToString(),
                LatencyReason = latencySnapshot.ReasonCode.ToString(),
                latencySnapshot.LatestHeartbeatSource,
                latencySnapshot.LatestExpectedOpenTimeUtc,
                latencySnapshot.LatestClockDriftMilliseconds,
                DecisionSourceLayer = ResolveDecisionSourceLayer(latencySnapshot),
                ModeResolution = modeResolution is null
                    ? null
                    : new
                    {
                        EffectiveMode = modeResolution.EffectiveMode.ToString(),
                        Source = modeResolution.ResolutionSource.ToString(),
                        modeResolution.Reason
                    },
                DemoSession = demoSessionSnapshot is null
                    ? null
                    : new
                    {
                        demoSessionSnapshot.State,
                        demoSessionSnapshot.ConsistencyStatus,
                        demoSessionSnapshot.SequenceNumber,
                        demoSessionSnapshot.LastDriftSummary
                    },
                GlobalSystem = globalSystemStateSnapshot is null
                    ? null
                    : new
                    {
                        State = globalSystemStateSnapshot.State.ToString(),
                        globalSystemStateSnapshot.ReasonCode,
                        globalSystemStateSnapshot.Version,
                        globalSystemStateSnapshot.IsManualOverride
                    },
                AdministrativeOverride = administrativeOverrideReason
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string ResolveDecisionUserId(string? userId)
    {
        return string.IsNullOrWhiteSpace(userId)
            ? "system:execution-gate"
            : userId.Trim();
    }

    private static string NormalizeDecisionDimension(string? primaryValue, string? fallbackValue, string defaultValue)
    {
        var normalizedValue = primaryValue?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedValue))
        {
            return normalizedValue;
        }

        var normalizedFallbackValue = fallbackValue?.Trim();
        return string.IsNullOrWhiteSpace(normalizedFallbackValue)
            ? defaultValue
            : normalizedFallbackValue;
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

    private static DateTime? NormalizeUtcNullable(DateTime? value)
    {
        return value.HasValue
            ? NormalizeUtc(value.Value)
            : null;
    }

    private static int? ResolveDataAgeMilliseconds(DateTime decisionAtUtc, DateTime? lastCandleAtUtc)
    {
        if (!lastCandleAtUtc.HasValue)
        {
            return null;
        }

        var deltaMilliseconds = (decisionAtUtc - lastCandleAtUtc.Value).TotalMilliseconds;
        if (deltaMilliseconds <= 0)
        {
            return 0;
        }

        return deltaMilliseconds >= int.MaxValue
            ? int.MaxValue
            : (int)Math.Round(deltaMilliseconds, MidpointRounding.AwayFromZero);
    }

    private static string? BuildAuditContext(
        string? requestContext,
        DegradedModeSnapshot latencySnapshot,
        ExecutionDecisionDescriptor decisionDescriptor,
        TradingModeResolution? modeResolution = null,
        DemoSessionSnapshot? demoSessionSnapshot = null,
        GlobalSystemStateSnapshot? globalSystemStateSnapshot = null,
        string? administrativeOverrideReason = null)
    {
        var contextParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(requestContext))
        {
            contextParts.Add(requestContext.Trim());
        }

        contextParts.Add(
            $"DecisionOutcome={decisionDescriptor.Outcome}; DecisionReasonType={decisionDescriptor.ReasonType}; DecisionReasonCode={decisionDescriptor.ReasonCode}; DecisionSummary={Truncate(decisionDescriptor.Summary, 256) ?? "none"}; DecisionAtUtc={decisionDescriptor.DecisionAtUtc:O}; Symbol={decisionDescriptor.Symbol}; Timeframe={decisionDescriptor.Timeframe}; LastCandleAtUtc={decisionDescriptor.LastCandleAtUtc?.ToString("O") ?? "missing"}; DataAgeMs={decisionDescriptor.DataAgeMs?.ToString() ?? "missing"}; StaleThresholdMs={decisionDescriptor.StaleThresholdMs}; StaleReason={decisionDescriptor.StaleReason ?? "none"}; ContinuityState={decisionDescriptor.ContinuityState}; ContinuityGapCount={decisionDescriptor.ContinuityGapCount?.ToString() ?? "missing"}; ContinuityGapStartedAtUtc={decisionDescriptor.ContinuityGapStartedAtUtc?.ToString("O") ?? "missing"}; ContinuityGapLastSeenAtUtc={decisionDescriptor.ContinuityGapLastSeenAtUtc?.ToString("O") ?? "missing"}; ContinuityRecoveredAtUtc={decisionDescriptor.ContinuityRecoveredAtUtc?.ToString("O") ?? "missing"}");

        contextParts.Add(
            $"LatencyState={latencySnapshot.StateCode}; LatencyReason={latencySnapshot.ReasonCode}; HeartbeatSource={latencySnapshot.LatestHeartbeatSource ?? "missing"}; ExpectedOpenTimeUtc={latencySnapshot.LatestExpectedOpenTimeUtc?.ToString("O") ?? "missing"}; ClockDriftMs={latencySnapshot.LatestClockDriftMilliseconds?.ToString() ?? "missing"}; DecisionSourceLayer={ResolveDecisionSourceLayer(latencySnapshot)}; DecisionMethodName={"ExecutionGate.EvaluateDataLatencyAsync"}");

        if (modeResolution is not null)
        {
            contextParts.Add(
                $"ResolvedMode={modeResolution.EffectiveMode}; Source={modeResolution.ResolutionSource}; Reason={modeResolution.Reason}");
        }

        if (demoSessionSnapshot is not null)
        {
            contextParts.Add(
                $"DemoSessionState={demoSessionSnapshot.State}; DemoConsistency={demoSessionSnapshot.ConsistencyStatus}; DemoSessionSequence={demoSessionSnapshot.SequenceNumber}; DemoDrift={Truncate(demoSessionSnapshot.LastDriftSummary, 160) ?? "none"}");
        }

        if (globalSystemStateSnapshot is not null)
        {
            contextParts.Add(
                $"GlobalSystemState={globalSystemStateSnapshot.State}; GlobalSystemReason={globalSystemStateSnapshot.ReasonCode}; GlobalSystemVersion={globalSystemStateSnapshot.Version}; GlobalSystemManual={globalSystemStateSnapshot.IsManualOverride}");
        }

        if (!string.IsNullOrWhiteSpace(administrativeOverrideReason))
        {
            contextParts.Add($"AdministrativeOverride={Truncate(administrativeOverrideReason, 256)}");
        }

        var combinedContext = string.Join(" | ", contextParts);

        return combinedContext.Length <= 2048
            ? combinedContext
            : combinedContext[..2048];
    }

    private sealed record ExecutionDecisionDescriptor(
        string Outcome,
        string ReasonType,
        string ReasonCode,
        string Summary,
        DateTime DecisionAtUtc,
        string Symbol,
        string Timeframe,
        DateTime? LastCandleAtUtc,
        int? DataAgeMs,
        int StaleThresholdMs,
        string? StaleReason,
        string ContinuityState,
        int? ContinuityGapCount,
        DateTime? ContinuityGapStartedAtUtc,
        DateTime? ContinuityGapLastSeenAtUtc,
        DateTime? ContinuityRecoveredAtUtc);

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }

    private static bool TryResolveAdministrativeOverrideReason(string? context, out string? reason)
    {
        reason = null;

        if (string.IsNullOrWhiteSpace(context))
        {
            return false;
        }

        var prefix = "AdministrativeOverride=";
        var segments = context.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            if (!segment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            reason = segment[prefix.Length..].Trim();
            return !string.IsNullOrWhiteSpace(reason);
        }

        return false;
    }

    private bool IsDevelopmentFuturesPilotOverrideAllowed(ExecutionGateRequest request)
    {
        return hostEnvironment?.IsDevelopment() == true &&
               request.Actor.StartsWith("system:", StringComparison.OrdinalIgnoreCase) &&
               TryReadBooleanFlag(request.Context, "DevelopmentFuturesTestnetPilot");
    }

    private static bool TryReadBooleanFlag(string? context, string key)
    {
        if (string.IsNullOrWhiteSpace(context))
        {
            return false;
        }

        var prefix = $"{key}=";
        var segments = context.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            if (!segment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return bool.TryParse(segment[prefix.Length..].Trim(), out var value) && value;
        }

        return false;
    }
}

