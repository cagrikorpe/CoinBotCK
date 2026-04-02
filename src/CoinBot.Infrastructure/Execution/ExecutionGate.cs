using System.Diagnostics;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DemoPortfolio;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Observability;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Execution;

public sealed class ExecutionGate(
    IDemoSessionService demoSessionService,
    IGlobalSystemStateService globalSystemStateService,
    IGlobalExecutionSwitchService globalExecutionSwitchService,
    IDataLatencyCircuitBreaker dataLatencyCircuitBreaker,
    ITradingModeResolver tradingModeResolver,
    IAuditLogService auditLogService,
    ILogger<ExecutionGate> logger,
    IHostEnvironment? hostEnvironment = null) : IExecutionGate
{
    public async Task<GlobalExecutionSwitchSnapshot> EnsureExecutionAllowedAsync(
        ExecutionGateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (TryResolveAdministrativeOverrideReason(request.Context, out var administrativeOverrideReason))
        {
            var switchSnapshot = await globalExecutionSwitchService.GetSnapshotAsync(cancellationToken);

            await auditLogService.WriteAsync(
                new AuditLogWriteRequest(
                    request.Actor,
                    request.Action,
                    request.Target,
                    BuildAuditContext(
                        request.Context,
                        new DegradedModeSnapshot(
                            DegradedModeStateCode.Normal,
                            DegradedModeReasonCode.None,
                            SignalFlowBlocked: false,
                            ExecutionFlowBlocked: false,
                            LatestDataTimestampAtUtc: null,
                            LatestHeartbeatReceivedAtUtc: null,
                            LatestDataAgeMilliseconds: null,
                            LatestClockDriftMilliseconds: null,
                            LastStateChangedAtUtc: null,
                            IsPersisted: false),
                        administrativeOverrideReason: administrativeOverrideReason),
                    request.CorrelationId,
                    "Allowed:AdministrativeOverride",
                    request.Environment.ToString()),
                cancellationToken);

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

            await auditLogService.WriteAsync(
                new AuditLogWriteRequest(
                    request.Actor,
                    request.Action,
                    request.Target,
                    BuildAuditContext(request.Context, latencySnapshot),
                    request.CorrelationId,
                    MapOutcome(latencyBlockedReason.Value),
                    request.Environment.ToString()),
                cancellationToken);

            logger.LogWarning(
                "Signal stage blocked the request with reason {BlockedReason}.",
                latencyBlockedReason);

            throw new ExecutionGateRejectedException(
                latencyBlockedReason.Value,
                request.Environment,
                CreateMessage(latencyBlockedReason.Value, request.Environment));
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

            await auditLogService.WriteAsync(
                new AuditLogWriteRequest(
                    request.Actor,
                    request.Action,
                    request.Target,
                    BuildAuditContext(request.Context, latencySnapshot),
                    request.CorrelationId,
                    "Blocked:GlobalSystemStateUnavailable",
                    request.Environment.ToString()),
                cancellationToken);

            logger.LogWarning(exception, "Execution stage failed closed because global system state could not be evaluated.");

            throw new InvalidOperationException("Execution blocked because global system state could not be evaluated.", exception);
        }

        if (globalSystemStateSnapshot.IsExecutionBlocked)
        {
            using var executionActivity = CoinBotActivity.StartActivity("CoinBot.Execution.Gate");
            ApplyTags(executionActivity, request);
            executionActivity.SetTag("coinbot.execution.result", $"GlobalSystem:{globalSystemStateSnapshot.State}");

            await auditLogService.WriteAsync(
                new AuditLogWriteRequest(
                    request.Actor,
                    request.Action,
                    request.Target,
                    BuildAuditContext(request.Context, latencySnapshot, globalSystemStateSnapshot: globalSystemStateSnapshot),
                    request.CorrelationId,
                    BuildGlobalSystemOutcome(globalSystemStateSnapshot.State),
                    request.Environment.ToString()),
                cancellationToken);

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

                await auditLogService.WriteAsync(
                new AuditLogWriteRequest(
                    request.Actor,
                    request.Action,
                    request.Target,
                    BuildAuditContext(request.Context, latencySnapshot, demoSessionSnapshot: demoSessionSnapshot, globalSystemStateSnapshot: globalSystemStateSnapshot),
                    request.CorrelationId,
                    MapOutcome(ExecutionGateBlockedReason.DemoSessionDriftDetected),
                    request.Environment.ToString()),
                    cancellationToken);

                logger.LogWarning("Execution stage blocked the request because the active demo session drifted.");

                throw new ExecutionGateRejectedException(
                    ExecutionGateBlockedReason.DemoSessionDriftDetected,
                    request.Environment,
                    CreateMessage(ExecutionGateBlockedReason.DemoSessionDriftDetected, request.Environment));
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

        await auditLogService.WriteAsync(
            new AuditLogWriteRequest(
                request.Actor,
                request.Action,
                request.Target,
                BuildAuditContext(request.Context, latencySnapshot, modeResolution, demoSessionSnapshot, globalSystemStateSnapshot),
                request.CorrelationId,
                blockedReason is null ? "Allowed" : MapOutcome(blockedReason.Value),
                request.Environment.ToString()),
            cancellationToken);

        if (blockedReason is not null)
        {
            logger.LogWarning(
                "Execution stage blocked the request with reason {BlockedReason}.",
                blockedReason);

            throw new ExecutionGateRejectedException(
                blockedReason.Value,
                request.Environment,
                CreateMessage(blockedReason.Value, request.Environment));
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
            var snapshot = await dataLatencyCircuitBreaker.GetSnapshotAsync(request.CorrelationId, cancellationToken);
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
            DegradedModeReasonCode.CandleDataGapDetected => ExecutionGateBlockedReason.StaleMarketData,
            DegradedModeReasonCode.CandleDataDuplicateDetected => ExecutionGateBlockedReason.StaleMarketData,
            DegradedModeReasonCode.CandleDataOutOfOrderDetected => ExecutionGateBlockedReason.StaleMarketData,
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
            ExecutionGateBlockedReason.ClockDriftExceeded => "Blocked:ClockDriftExceeded",
            ExecutionGateBlockedReason.DataLatencyGuardUnavailable => "Blocked:DataLatencyGuardUnavailable",
            ExecutionGateBlockedReason.DemoSessionDriftDetected => "Blocked:DemoSessionDriftDetected",
            _ => "Blocked:Unknown"
        };
    }

    private static string CreateMessage(ExecutionGateBlockedReason blockedReason, ExecutionEnvironment requestedEnvironment)
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
                "Execution blocked because market data heartbeat is unavailable.",
            ExecutionGateBlockedReason.StaleMarketData =>
                "Execution blocked because market data is stale or the candle continuity guard is active.",
            ExecutionGateBlockedReason.ClockDriftExceeded =>
                "Execution blocked because clock drift exceeded the safety threshold.",
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

    private static string CreateGlobalSystemStateMessage(GlobalSystemStateSnapshot snapshot)
    {
        return string.IsNullOrWhiteSpace(snapshot.Message)
            ? $"Execution blocked because global system state is {snapshot.State}."
            : $"Execution blocked because global system state is {snapshot.State}: {snapshot.Message}";
    }

    private static string? BuildAuditContext(
        string? requestContext,
        DegradedModeSnapshot latencySnapshot,
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
            $"LatencyState={latencySnapshot.StateCode}; LatencyReason={latencySnapshot.ReasonCode}; DataAgeMs={latencySnapshot.LatestDataAgeMilliseconds?.ToString() ?? "missing"}; ClockDriftMs={latencySnapshot.LatestClockDriftMilliseconds?.ToString() ?? "missing"}");

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

