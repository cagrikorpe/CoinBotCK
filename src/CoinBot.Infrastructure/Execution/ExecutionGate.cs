using System.Diagnostics;
using System.Text.Json;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DemoPortfolio;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.DemoPortfolio;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
    IOptions<DataLatencyGuardOptions>? dataLatencyGuardOptions = null,
    ApplicationDbContext? applicationDbContext = null,
    IOptions<BinancePrivateDataOptions>? privateDataOptions = null,
    IOptions<BinanceMarketDataOptions>? marketDataOptions = null,
    IOptions<BotExecutionPilotOptions>? botExecutionPilotOptions = null,
    IOptions<ExecutionRuntimeOptions>? executionRuntimeOptions = null,
    IOptions<DemoSessionOptions>? demoSessionOptions = null) : IExecutionGate
{
    private readonly ITraceService? traceWriter = traceService;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly int staleThresholdMilliseconds = checked((dataLatencyGuardOptions?.Value ?? new DataLatencyGuardOptions()).StaleDataThresholdSeconds * 1000);
    private readonly ApplicationDbContext? dbContext = applicationDbContext;
    private readonly BinancePrivateDataOptions privateDataOptionsValue = privateDataOptions?.Value ?? new BinancePrivateDataOptions();
    private readonly BinanceMarketDataOptions marketDataOptionsValue = marketDataOptions?.Value ?? new BinanceMarketDataOptions();
    private readonly BotExecutionPilotOptions pilotOptionsValue = botExecutionPilotOptions?.Value ?? new BotExecutionPilotOptions();
    private readonly ExecutionRuntimeOptions executionRuntimeOptionsValue = executionRuntimeOptions?.Value ?? new ExecutionRuntimeOptions();
    private readonly DemoSessionOptions demoSessionOptionsValue = demoSessionOptions?.Value ?? new DemoSessionOptions();
    private readonly int privatePlaneFreshnessThresholdMilliseconds = checked((botExecutionPilotOptions?.Value ?? new BotExecutionPilotOptions()).PrivatePlaneFreshnessThresholdSeconds * 1000);

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
                summary: await CreateGlobalSystemStateMessageAsync(globalSystemStateSnapshot, cancellationToken),
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

            throw new InvalidOperationException(await CreateGlobalSystemStateMessageAsync(globalSystemStateSnapshot, cancellationToken));
        }

        DemoSessionSnapshot? demoSessionSnapshot = null;

        if (request.Environment == ExecutionEnvironment.Demo &&
            executionRuntimeOptionsValue.AllowInternalDemoExecution &&
            !string.IsNullOrWhiteSpace(request.UserId))
        {
            demoSessionSnapshot = await demoSessionService.RunConsistencyCheckAsync(request.UserId, cancellationToken);
            demoSessionSnapshot ??= await demoSessionService.ResetAsync(
                new DemoSessionResetRequest(
                    request.UserId,
                    ExecutionEnvironment.Demo,
                    request.Actor,
                    "Auto-bootstrap demo session for execution request.",
                    request.CorrelationId),
                cancellationToken);

            if (demoSessionSnapshot is not null &&
                demoSessionSnapshot.ConsistencyStatus == DemoConsistencyStatus.DriftDetected)
            {
                using var executionActivity = CoinBotActivity.StartActivity("CoinBot.Execution.Gate");
                ApplyTags(executionActivity, request);
                executionActivity.SetTag("coinbot.execution.result", ExecutionGateBlockedReason.DemoSessionDriftDetected.ToString());
                var demoSessionDriftMessage = CreateDemoSessionDriftMessage(request.Environment, latencySnapshot, demoSessionSnapshot);
                var demoSessionDecision = CreateDecisionDescriptor(
                    request,
                    isBlocked: true,
                    reasonCode: ExecutionGateBlockedReason.DemoSessionDriftDetected.ToString(),
                    summary: demoSessionDriftMessage,
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
                    demoSessionDriftMessage);
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

        var pilotSafetyEvaluation = await EvaluatePilotSafetyAsync(request, cancellationToken);

        using var finalExecutionActivity = CoinBotActivity.StartActivity("CoinBot.Execution.Gate");
        ApplyTags(finalExecutionActivity, request);
        ApplyLatencyTags(finalExecutionActivity, latencySnapshot);
        ApplyPilotSafetyTags(finalExecutionActivity, pilotSafetyEvaluation);
        var blockedReason = Evaluate(
            snapshot,
            request.Environment,
            modeResolution,
            pilotSafetyEvaluation.AllowModeOverride,
            allowPilotGlobalSwitchBypass: pilotSafetyEvaluation.IsPilotRequest &&
                                         pilotSafetyEvaluation.AllowModeOverride &&
                                         pilotOptionsValue.AllowGlobalSwitchBypass);
        ExecutionGateBlockedReason? pilotBlockedReason = pilotSafetyEvaluation.BlockedReasons.Count > 0
            ? pilotSafetyEvaluation.BlockedReasons[0]
            : null;
        var primaryBlockedReason = blockedReason ?? pilotBlockedReason;
        finalExecutionActivity.SetTag("coinbot.execution.result", primaryBlockedReason?.ToString() ?? "Allowed");
        var blockedMessage = primaryBlockedReason is null
            ? null
            : CreateBlockedMessage(primaryBlockedReason.Value, request.Environment, latencySnapshot, pilotSafetyEvaluation);
        var finalDecision = CreateDecisionDescriptor(
            request,
            isBlocked: primaryBlockedReason is not null,
            reasonCode: primaryBlockedReason?.ToString() ?? ExecutionDecisionDiagnostics.AllowedDecisionCode,
            summary: primaryBlockedReason is null
                ? "Execution decision allowed the request."
                : ExecutionDecisionDiagnostics.ExtractHumanSummary(blockedMessage) ?? blockedMessage ?? CreateMessage(primaryBlockedReason.Value, request.Environment, latencySnapshot),
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
                    globalSystemStateSnapshot,
                    null,
                    pilotSafetyEvaluation),
                request.CorrelationId,
                primaryBlockedReason is null ? "Allowed" : MapOutcome(primaryBlockedReason.Value),
                request.Environment.ToString()),
            cancellationToken);

        await WriteDecisionTraceAsync(
            request,
            finalDecision,
            latencySnapshot,
            cancellationToken,
            modeResolution,
            demoSessionSnapshot,
            globalSystemStateSnapshot,
            null,
            pilotSafetyEvaluation);

        if (primaryBlockedReason is not null)
        {
            logger.LogWarning(
                "Execution stage blocked the request with reason {BlockedReason}.",
                primaryBlockedReason);

            throw new ExecutionGateRejectedException(
                primaryBlockedReason.Value,
                request.Environment,
                blockedMessage ?? CreateMessage(primaryBlockedReason.Value, request.Environment, latencySnapshot));
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

    private void ApplyPilotSafetyTags(Activity activity, PilotSafetyEvaluation evaluation)
    {
        activity.SetTag("coinbot.pilot.request", evaluation.IsPilotRequest);

        if (!evaluation.IsPilotRequest)
        {
            return;
        }

        activity.SetTag("coinbot.pilot.allow_mode_override", evaluation.AllowModeOverride);
        activity.SetTag("coinbot.pilot.private_rest_scope", evaluation.PrivateRestEnvironmentScope);
        activity.SetTag("coinbot.pilot.private_ws_scope", evaluation.PrivateSocketEnvironmentScope);
        activity.SetTag("coinbot.pilot.market_rest_scope", evaluation.MarketDataRestEnvironmentScope);
        activity.SetTag("coinbot.pilot.market_ws_scope", evaluation.MarketDataSocketEnvironmentScope);
        activity.SetTag("coinbot.pilot.credential_status", evaluation.CredentialValidationStatus);
        activity.SetTag("coinbot.pilot.credential_environment_scope", evaluation.CredentialEnvironmentScope);
        activity.SetTag("coinbot.pilot.private_plane_freshness", evaluation.PrivatePlaneFreshness);
        activity.SetTag("coinbot.pilot.private_stream_state", evaluation.PrivateStreamConnectionState);
        activity.SetTag("coinbot.pilot.drift_status", evaluation.DriftStatus);
        activity.SetTag("coinbot.pilot.blocked_reasons", BuildPilotBlockedReasonsText(evaluation.BlockedReasons));

        if (evaluation.LastPrivateSyncAtUtc is DateTime lastPrivateSyncAtUtc)
        {
            activity.SetTag("coinbot.pilot.last_private_sync_at_utc", lastPrivateSyncAtUtc.ToString("O"));
        }

        if (evaluation.PrivatePlaneAgeMs is int privatePlaneAgeMs)
        {
            activity.SetTag("coinbot.pilot.private_plane_age_ms", privatePlaneAgeMs);
        }
    }

    private async Task<PilotSafetyEvaluation> EvaluatePilotSafetyAsync(
        ExecutionGateRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsPilotContextRequested(request.Context))
        {
            return PilotSafetyEvaluation.NotApplicable;
        }

        var blockedReasons = new List<ExecutionGateBlockedReason>();
        var privateRestEnvironmentScope = ResolveEnvironmentScope(privateDataOptionsValue.RestBaseUrl);
        var privateSocketEnvironmentScope = ResolveEnvironmentScope(privateDataOptionsValue.WebSocketBaseUrl);
        var marketDataRestEnvironmentScope = ResolveEnvironmentScope(marketDataOptionsValue.RestBaseUrl);
        var marketDataSocketEnvironmentScope = ResolveEnvironmentScope(marketDataOptionsValue.WebSocketBaseUrl);
        var credentialValidationStatus = "Missing";
        var credentialEnvironmentScope = "Unknown";
        var privatePlaneFreshness = "Unknown";
        var privateStreamConnectionState = "Missing";
        var driftStatus = "Missing";
        DateTime? lastPrivateSyncAtUtc = null;
        int? privatePlaneAgeMs = null;

        if (!IsDevelopmentPilotHostAndActor(request))
        {
            blockedReasons.Add(ExecutionGateBlockedReason.PilotRequiresDevelopment);
        }

        if (!pilotOptionsValue.Enabled ||
            request.Environment != ExecutionEnvironment.Live ||
            request.Plane != ExchangeDataPlane.Futures ||
            string.IsNullOrWhiteSpace(request.UserId) ||
            !request.BotId.HasValue ||
            !request.ExchangeAccountId.HasValue)
        {
            blockedReasons.Add(ExecutionGateBlockedReason.PilotConfigurationMissing);
        }

        if (!string.Equals(privateRestEnvironmentScope, "Testnet", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(privateSocketEnvironmentScope, "Testnet", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(marketDataRestEnvironmentScope, "Testnet", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(marketDataSocketEnvironmentScope, "Testnet", StringComparison.OrdinalIgnoreCase))
        {
            blockedReasons.Add(ExecutionGateBlockedReason.PilotTestnetEndpointMismatch);
        }

        if (dbContext is null ||
            string.IsNullOrWhiteSpace(request.UserId) ||
            !request.ExchangeAccountId.HasValue)
        {
            credentialValidationStatus = "Unavailable";
            blockedReasons.Add(ExecutionGateBlockedReason.PilotCredentialValidationUnavailable);
            privatePlaneFreshness = "Unavailable";
            blockedReasons.Add(ExecutionGateBlockedReason.PrivatePlaneUnavailable);
        }
        else
        {
            var latestValidation = await dbContext.ApiCredentialValidations
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.ExchangeAccountId == request.ExchangeAccountId.Value &&
                    entity.OwnerUserId == request.UserId &&
                    !entity.IsDeleted)
                .OrderByDescending(entity => entity.ValidatedAtUtc)
                .ThenByDescending(entity => entity.CreatedDate)
                .FirstOrDefaultAsync(cancellationToken);

            if (latestValidation is null)
            {
                blockedReasons.Add(ExecutionGateBlockedReason.PilotCredentialValidationUnavailable);
            }
            else
            {
                credentialValidationStatus = string.IsNullOrWhiteSpace(latestValidation.ValidationStatus)
                    ? "Unknown"
                    : latestValidation.ValidationStatus.Trim();
                credentialEnvironmentScope = string.IsNullOrWhiteSpace(latestValidation.EnvironmentScope)
                    ? "Unknown"
                    : latestValidation.EnvironmentScope.Trim();

                if (!latestValidation.IsKeyValid ||
                    !latestValidation.CanTrade ||
                    !latestValidation.SupportsFutures)
                {
                    blockedReasons.Add(ExecutionGateBlockedReason.PilotCredentialValidationUnavailable);
                }
                else if (!latestValidation.IsEnvironmentMatch ||
                         !string.Equals(credentialEnvironmentScope, "Testnet", StringComparison.OrdinalIgnoreCase))
                {
                    blockedReasons.Add(ExecutionGateBlockedReason.PilotCredentialEnvironmentMismatch);
                }
            }

            var syncState = await dbContext.ExchangeAccountSyncStates
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.ExchangeAccountId == request.ExchangeAccountId.Value &&
                    entity.OwnerUserId == request.UserId &&
                    entity.Plane == request.Plane &&
                    !entity.IsDeleted)
                .OrderByDescending(entity => entity.UpdatedDate)
                .ThenByDescending(entity => entity.CreatedDate)
                .FirstOrDefaultAsync(cancellationToken);

            if (syncState is null)
            {
                privatePlaneFreshness = "Unavailable";
                blockedReasons.Add(ExecutionGateBlockedReason.PrivatePlaneUnavailable);
            }
            else
            {
                privateStreamConnectionState = syncState.PrivateStreamConnectionState.ToString();
                driftStatus = syncState.DriftStatus.ToString();
                lastPrivateSyncAtUtc = ResolveLastPrivateSyncAtUtc(syncState);
                privatePlaneAgeMs = ResolveAgeMilliseconds(clock.GetUtcNow().UtcDateTime, lastPrivateSyncAtUtc);

                if (!lastPrivateSyncAtUtc.HasValue)
                {
                    privatePlaneFreshness = "Unavailable";
                    blockedReasons.Add(ExecutionGateBlockedReason.PrivatePlaneUnavailable);
                }
                else if (syncState.PrivateStreamConnectionState != ExchangePrivateStreamConnectionState.Connected ||
                         syncState.DriftStatus != ExchangeStateDriftStatus.InSync ||
                         !privatePlaneAgeMs.HasValue ||
                         privatePlaneAgeMs.Value > privatePlaneFreshnessThresholdMilliseconds)
                {
                    privatePlaneFreshness = "Stale";
                    blockedReasons.Add(ExecutionGateBlockedReason.PrivatePlaneStale);
                }
                else
                {
                    privatePlaneFreshness = "Fresh";
                }
            }
        }

        return new PilotSafetyEvaluation(
            true,
            true,
            privateRestEnvironmentScope,
            privateSocketEnvironmentScope,
            marketDataRestEnvironmentScope,
            marketDataSocketEnvironmentScope,
            credentialValidationStatus,
            credentialEnvironmentScope,
            privatePlaneFreshness,
            privateStreamConnectionState,
            driftStatus,
            lastPrivateSyncAtUtc,
            privatePlaneAgeMs,
            blockedReasons
                .Distinct()
                .ToArray());
    }

    private string CreateBlockedMessage(
        ExecutionGateBlockedReason blockedReason,
        ExecutionEnvironment requestedEnvironment,
        DegradedModeSnapshot? latencySnapshot,
        PilotSafetyEvaluation evaluation)
    {
        var baseMessage = CreateMessage(blockedReason, requestedEnvironment, latencySnapshot);
        if (!evaluation.IsPilotRequest)
        {
            return baseMessage;
        }

        return $"{baseMessage} PilotGuardSummary={BuildPilotGuardSummary(evaluation)}; PilotBlockedReasons={BuildPilotBlockedReasonsText(evaluation.BlockedReasons)}.";
    }

    private string BuildPilotGuardSummary(PilotSafetyEvaluation evaluation)
    {
        return $"PilotRequest=True; AllowModeOverride={evaluation.AllowModeOverride}; EndpointScopes=PrivateRest:{evaluation.PrivateRestEnvironmentScope}/PrivateWs:{evaluation.PrivateSocketEnvironmentScope}/MarketRest:{evaluation.MarketDataRestEnvironmentScope}/MarketWs:{evaluation.MarketDataSocketEnvironmentScope}; CredentialValidationStatus={evaluation.CredentialValidationStatus}; CredentialEnvironmentScope={evaluation.CredentialEnvironmentScope}; PrivatePlaneFreshness={evaluation.PrivatePlaneFreshness}; PrivateStreamState={evaluation.PrivateStreamConnectionState}; DriftStatus={evaluation.DriftStatus}; LastPrivateSyncAtUtc={evaluation.LastPrivateSyncAtUtc?.ToString("O") ?? "missing"}; PrivatePlaneAgeMs={evaluation.PrivatePlaneAgeMs?.ToString() ?? "missing"}; PrivatePlaneThresholdMs={privatePlaneFreshnessThresholdMilliseconds}";
    }

    private static string BuildPilotBlockedReasonsText(IReadOnlyList<ExecutionGateBlockedReason> blockedReasons)
    {
        return blockedReasons.Count == 0
            ? "none"
            : string.Join(",", blockedReasons.Select(item => item.ToString()));
    }

    private static DateTime? ResolveLastPrivateSyncAtUtc(ExchangeAccountSyncState syncState)
    {
        DateTime? latest = null;
        Consider(syncState.LastPrivateStreamEventAtUtc);
        Consider(syncState.LastBalanceSyncedAtUtc);
        Consider(syncState.LastPositionSyncedAtUtc);
        Consider(syncState.LastStateReconciledAtUtc);
        return latest;

        void Consider(DateTime? value)
        {
            var normalizedValue = NormalizeUtcNullable(value);
            if (!normalizedValue.HasValue)
            {
                return;
            }

            if (!latest.HasValue || normalizedValue.Value > latest.Value)
            {
                latest = normalizedValue.Value;
            }
        }
    }

    private static int? ResolveAgeMilliseconds(DateTime nowUtc, DateTime? observedAtUtc)
    {
        if (!observedAtUtc.HasValue)
        {
            return null;
        }

        var deltaMilliseconds = (NormalizeUtc(nowUtc) - observedAtUtc.Value).TotalMilliseconds;
        if (deltaMilliseconds <= 0)
        {
            return 0;
        }

        return deltaMilliseconds >= int.MaxValue
            ? int.MaxValue
            : (int)Math.Round(deltaMilliseconds, MidpointRounding.AwayFromZero);
    }

    private static string ResolveEnvironmentScope(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return "Unknown";
        }

        var normalizedValue = baseUrl.Trim();

        if (Uri.TryCreate(normalizedValue, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.Trim();
            if (host.Contains("testnet", StringComparison.OrdinalIgnoreCase) ||
                host.Contains("demo", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".binancefuture.com", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "binancefuture.com", StringComparison.OrdinalIgnoreCase))
            {
                return "Testnet";
            }
        }

        return normalizedValue.Contains("testnet", StringComparison.OrdinalIgnoreCase) ||
               normalizedValue.Contains("demo", StringComparison.OrdinalIgnoreCase) ||
               normalizedValue.Contains("binancefuture.com", StringComparison.OrdinalIgnoreCase)
            ? "Testnet"
            : "Live";
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
        bool allowDevelopmentFuturesPilotOverride,
        bool allowPilotGlobalSwitchBypass)
    {
        if (!snapshot.IsPersisted && !allowPilotGlobalSwitchBypass)
        {
            return ExecutionGateBlockedReason.SwitchConfigurationMissing;
        }

        if (!snapshot.IsTradeMasterArmed && !allowPilotGlobalSwitchBypass)
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
            ExecutionGateBlockedReason.PilotConfigurationMissing => "Blocked:PilotConfigurationMissing",
            ExecutionGateBlockedReason.PilotRequiresDevelopment => "Blocked:PilotRequiresDevelopment",
            ExecutionGateBlockedReason.PilotTestnetEndpointMismatch => "Blocked:PilotTestnetEndpointMismatch",
            ExecutionGateBlockedReason.PilotCredentialValidationUnavailable => "Blocked:PilotCredentialValidationUnavailable",
            ExecutionGateBlockedReason.PilotCredentialEnvironmentMismatch => "Blocked:PilotCredentialEnvironmentMismatch",
            ExecutionGateBlockedReason.PrivatePlaneUnavailable => "Blocked:PrivatePlaneUnavailable",
            ExecutionGateBlockedReason.PrivatePlaneStale => "Blocked:PrivatePlaneStale",
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
            ExecutionGateBlockedReason.PilotConfigurationMissing =>
                "Execution blocked because pilot safety configuration is missing or incomplete.",
            ExecutionGateBlockedReason.PilotRequiresDevelopment =>
                "Execution blocked because the pilot path is restricted to Development system actors.",
            ExecutionGateBlockedReason.PilotTestnetEndpointMismatch =>
                "Execution blocked because pilot execution resolved a live or unknown exchange endpoint instead of testnet.",
            ExecutionGateBlockedReason.PilotCredentialValidationUnavailable =>
                "Execution blocked because pilot credential validation is missing or insufficient for safe testnet futures trading.",
            ExecutionGateBlockedReason.PilotCredentialEnvironmentMismatch =>
                "Execution blocked because the credential validation environment does not match testnet futures execution.",
            ExecutionGateBlockedReason.PrivatePlaneUnavailable =>
                "Execution blocked because private plane freshness could not be verified.",
            ExecutionGateBlockedReason.PrivatePlaneStale =>
                "Execution blocked because private plane data is stale.",
            _ => "Execution blocked by an unknown gate decision."
        };
    }

    private string CreateDemoSessionDriftMessage(
        ExecutionEnvironment requestedEnvironment,
        DegradedModeSnapshot latencySnapshot,
        DemoSessionSnapshot demoSessionSnapshot)
    {
        return FormattableString.Invariant(
            $"{CreateMessage(ExecutionGateBlockedReason.DemoSessionDriftDetected, requestedEnvironment, latencySnapshot)} DecisionSourceLayer=DemoSessionConsistency; DemoSessionEvaluatedAtUtc={demoSessionSnapshot.LastConsistencyCheckedAtUtc?.ToString("O") ?? "missing"}; DemoSessionStartedAtUtc={demoSessionSnapshot.StartedAtUtc:O}; DemoSessionLastDriftDetectedAtUtc={demoSessionSnapshot.LastDriftDetectedAtUtc?.ToString("O") ?? "missing"}; ComparedState=LedgerSnapshotsVsDemoPortfolioProjection; ConsistencyTolerance={demoSessionOptionsValue.ConsistencyTolerance:0.##################}; ComputedDriftMs=not_applicable; ThresholdMs=not_applicable; StateReason={Truncate(demoSessionSnapshot.LastDriftSummary, 384) ?? "none"}");
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

    private async Task<string> CreateGlobalSystemStateMessageAsync(
        GlobalSystemStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var baseMessage = string.IsNullOrWhiteSpace(snapshot.Message)
            ? $"Execution blocked because global system state is {snapshot.State}."
            : $"Execution blocked because global system state is {snapshot.State}: {snapshot.Message}";

        if (snapshot.State != GlobalSystemStateKind.Degraded || dbContext is null)
        {
            return baseMessage;
        }

        var activeBreakers = await dbContext.DependencyCircuitBreakerStates
            .AsNoTracking()
            .Where(entity => !entity.IsDeleted && entity.StateCode != CircuitBreakerStateCode.Closed)
            .OrderBy(entity => entity.BreakerKind)
            .Select(entity => new
            {
                entity.BreakerKind,
                entity.StateCode,
                entity.ConsecutiveFailureCount,
                entity.CooldownUntilUtc,
                entity.LastFailureAtUtc,
                entity.LastSuccessAtUtc,
                entity.LastProbeAtUtc,
                entity.LastErrorCode,
                entity.LastErrorMessage,
                entity.CorrelationId
            })
            .ToListAsync(cancellationToken);

        if (activeBreakers.Count == 0)
        {
            return baseMessage;
        }

        var detail = string.Join(
            " | ",
            activeBreakers.Select(entity =>
                $"BreakerKind={entity.BreakerKind}; State={entity.StateCode}; FailureCount={entity.ConsecutiveFailureCount}; CooldownUntilUtc={entity.CooldownUntilUtc?.ToString("O") ?? "none"}; LastFailureAtUtc={entity.LastFailureAtUtc?.ToString("O") ?? "none"}; LastSuccessAtUtc={entity.LastSuccessAtUtc?.ToString("O") ?? "none"}; LastProbeAtUtc={entity.LastProbeAtUtc?.ToString("O") ?? "none"}; ErrorCode={entity.LastErrorCode ?? "none"}; Error={entity.LastErrorMessage ?? "none"}; CorrelationId={entity.CorrelationId ?? "none"}"));

        return $"{baseMessage} ActiveBreakerDiagnostics={detail}";
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
        string? administrativeOverrideReason = null,
        PilotSafetyEvaluation? pilotSafetyEvaluation = null)
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
                    administrativeOverrideReason,
                    pilotSafetyEvaluation),
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
    private string BuildDecisionTracePayload(
        ExecutionGateRequest request,
        ExecutionDecisionDescriptor decisionDescriptor,
        DegradedModeSnapshot latencySnapshot,
        TradingModeResolution? modeResolution,
        DemoSessionSnapshot? demoSessionSnapshot,
        GlobalSystemStateSnapshot? globalSystemStateSnapshot,
        string? administrativeOverrideReason,
        PilotSafetyEvaluation? pilotSafetyEvaluation)
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
                request.ExchangeAccountId,
                Plane = request.Plane.ToString(),
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
                        demoSessionSnapshot.StartedAtUtc,
                        demoSessionSnapshot.LastConsistencyCheckedAtUtc,
                        demoSessionSnapshot.LastDriftDetectedAtUtc,
                        SourceLayer = "DemoSessionConsistency",
                        ComparedState = "LedgerSnapshotsVsDemoPortfolioProjection",
                        ConsistencyTolerance = demoSessionOptionsValue.ConsistencyTolerance,
                        ComputedDriftMs = (int?)null,
                        ThresholdMs = (int?)null,
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
                AdministrativeOverride = administrativeOverrideReason,
                PilotSafety = pilotSafetyEvaluation is null || !pilotSafetyEvaluation.IsPilotRequest
                    ? null
                    : new
                    {
                        pilotSafetyEvaluation.AllowModeOverride,
                        pilotSafetyEvaluation.PrivateRestEnvironmentScope,
                        pilotSafetyEvaluation.PrivateSocketEnvironmentScope,
                        pilotSafetyEvaluation.MarketDataRestEnvironmentScope,
                        pilotSafetyEvaluation.MarketDataSocketEnvironmentScope,
                        pilotSafetyEvaluation.CredentialValidationStatus,
                        pilotSafetyEvaluation.CredentialEnvironmentScope,
                        pilotSafetyEvaluation.PrivatePlaneFreshness,
                        pilotSafetyEvaluation.PrivateStreamConnectionState,
                        pilotSafetyEvaluation.DriftStatus,
                        pilotSafetyEvaluation.LastPrivateSyncAtUtc,
                        pilotSafetyEvaluation.PrivatePlaneAgeMs,
                        PrivatePlaneThresholdMs = privatePlaneFreshnessThresholdMilliseconds,
                        BlockedReasons = pilotSafetyEvaluation.BlockedReasons
                    }
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

    private string? BuildAuditContext(
        string? requestContext,
        DegradedModeSnapshot latencySnapshot,
        ExecutionDecisionDescriptor decisionDescriptor,
        TradingModeResolution? modeResolution = null,
        DemoSessionSnapshot? demoSessionSnapshot = null,
        GlobalSystemStateSnapshot? globalSystemStateSnapshot = null,
        string? administrativeOverrideReason = null,
        PilotSafetyEvaluation? pilotSafetyEvaluation = null)
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
                $"DemoSessionState={demoSessionSnapshot.State}; DemoConsistency={demoSessionSnapshot.ConsistencyStatus}; DemoSessionSequence={demoSessionSnapshot.SequenceNumber}; DemoSessionStartedAtUtc={demoSessionSnapshot.StartedAtUtc:O}; DemoSessionCheckedAtUtc={demoSessionSnapshot.LastConsistencyCheckedAtUtc?.ToString("O") ?? "missing"}; DemoSessionDriftAtUtc={demoSessionSnapshot.LastDriftDetectedAtUtc?.ToString("O") ?? "missing"}; DemoDrift={Truncate(demoSessionSnapshot.LastDriftSummary, 512) ?? "none"}");
        }

        if (globalSystemStateSnapshot is not null)
        {
            contextParts.Add(
                $"GlobalSystemState={globalSystemStateSnapshot.State}; GlobalSystemReason={globalSystemStateSnapshot.ReasonCode}; GlobalSystemVersion={globalSystemStateSnapshot.Version}; GlobalSystemManual={globalSystemStateSnapshot.IsManualOverride}");
        }

        if (pilotSafetyEvaluation is not null && pilotSafetyEvaluation.IsPilotRequest)
        {
            contextParts.Add(
                $"PilotGuardSummary={Truncate(BuildPilotGuardSummary(pilotSafetyEvaluation), 512) ?? "none"}; PilotBlockedReasons={BuildPilotBlockedReasonsText(pilotSafetyEvaluation.BlockedReasons)}");
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
    private sealed record PilotSafetyEvaluation(
        bool IsPilotRequest,
        bool AllowModeOverride,
        string PrivateRestEnvironmentScope,
        string PrivateSocketEnvironmentScope,
        string MarketDataRestEnvironmentScope,
        string MarketDataSocketEnvironmentScope,
        string CredentialValidationStatus,
        string CredentialEnvironmentScope,
        string PrivatePlaneFreshness,
        string PrivateStreamConnectionState,
        string DriftStatus,
        DateTime? LastPrivateSyncAtUtc,
        int? PrivatePlaneAgeMs,
        IReadOnlyList<ExecutionGateBlockedReason> BlockedReasons)
    {
        public static PilotSafetyEvaluation NotApplicable { get; } = new(
            false,
            false,
            "n/a",
            "n/a",
            "n/a",
            "n/a",
            "n/a",
            "n/a",
            "NotEvaluated",
            "NotEvaluated",
            "NotEvaluated",
            null,
            null,
            Array.Empty<ExecutionGateBlockedReason>());
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

    private static bool IsPilotContextRequested(string? context)
    {
        return TryReadBooleanFlag(context, "DevelopmentFuturesTestnetPilot");
    }

    private bool IsDevelopmentPilotHostAndActor(ExecutionGateRequest request)
    {
        return (hostEnvironment?.IsDevelopment() == true || pilotOptionsValue.AllowNonDevelopmentHost) &&
               request.Actor.StartsWith("system:", StringComparison.OrdinalIgnoreCase);
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










