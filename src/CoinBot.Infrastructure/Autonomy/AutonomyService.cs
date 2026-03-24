using System.Security.Cryptography;
using System.Text;
using CoinBot.Application.Abstractions.Autonomy;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Autonomy;

public sealed class AutonomyService(
    ApplicationDbContext dbContext,
    IGlobalPolicyEngine globalPolicyEngine,
    IMarketDataService marketDataService,
    IMonitoringTelemetryCollector monitoringTelemetryCollector,
    IAutonomyReviewQueueService reviewQueueService,
    ISelfHealingExecutor selfHealingExecutor,
    IAutonomyIncidentHook incidentHook,
    IOptions<AutonomyOptions> options,
    TimeProvider timeProvider,
    ILogger<AutonomyService> logger) : IAutonomyService
{
    private static readonly IReadOnlySet<string> AutoExecutableActions = new HashSet<string>(StringComparer.Ordinal)
    {
        AutonomySuggestedActions.WebSocketReconnect,
        AutonomySuggestedActions.SignalRReconnect,
        AutonomySuggestedActions.WorkerRetry,
        AutonomySuggestedActions.CacheRebuild
    };

    private readonly AutonomyOptions optionsValue = options.Value;

    public async Task<PreFlightSimulationResult> SimulateAsync(
        PreFlightSimulationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedAction = NormalizeRequired(request.SuggestedAction, 128, nameof(request.SuggestedAction)).ToUpperInvariant();
        var normalizedUserId = NormalizeOptional(request.UserId, 450);
        var normalizedSymbol = NormalizeOptional(request.Symbol, 32)?.ToUpperInvariant();
        var normalizedConfidenceScore = NormalizeConfidenceScore(request.ConfidenceScore);
        var scope = await ResolveScopeAsync(normalizedUserId, normalizedSymbol, cancellationToken);
        var latestPrice = normalizedSymbol is null
            ? null
            : await marketDataService.GetLatestPriceAsync(normalizedSymbol, cancellationToken);
        var requestedPrice = request.Price ?? latestPrice?.Price;
        var requestedQuantity = request.Quantity;
        var telemetry = monitoringTelemetryCollector.CaptureSnapshot(timeProvider.GetUtcNow().UtcDateTime);

        var riskImpact = "None";
        var isGlobalPolicyCompliant = true;
        var hasRestrictionConflict = false;

        if (!string.IsNullOrWhiteSpace(normalizedSymbol) &&
            request.Environment.HasValue &&
            request.Side.HasValue &&
            requestedQuantity.HasValue &&
            requestedPrice.HasValue)
        {
            var policyEvaluation = await globalPolicyEngine.EvaluateAsync(
                new GlobalPolicyEvaluationRequest(
                    UserId: normalizedUserId ?? "autonomy:system",
                    Symbol: normalizedSymbol,
                    Environment: request.Environment.Value,
                    Side: request.Side.Value,
                    Quantity: requestedQuantity.Value,
                    Price: requestedPrice.Value),
                cancellationToken);

            isGlobalPolicyCompliant = !policyEvaluation.IsBlocked;
            hasRestrictionConflict = policyEvaluation.MatchedRestrictionState.HasValue;

            riskImpact = policyEvaluation.IsBlocked
                ? policyEvaluation.BlockCode ?? "Blocked"
                : policyEvaluation.IsAdvisory
                    ? policyEvaluation.AdvisoryCode ?? "Advisory"
                    : "None";
        }
        else
        {
            var policySnapshot = await globalPolicyEngine.GetSnapshotAsync(cancellationToken);
            isGlobalPolicyCompliant = policySnapshot.Policy.AutonomyPolicy.Mode == AutonomyPolicyMode.LowRiskAutoAct;
        }

        return new PreFlightSimulationResult(
            AffectedOpenPositionCount: scope.OpenPositionCount,
            EstimatedOpenPositionExposure: scope.EstimatedExposure,
            RiskLimitImpact: riskImpact,
            LiquidityImpact: ResolveLiquidityImpact(normalizedAction, requestedQuantity, requestedPrice),
            RateLimitImpact: ResolveRateLimitImpact(normalizedAction, telemetry.RateLimitUsage),
            FalsePositiveProbability: Math.Clamp(1m - normalizedConfidenceScore, 0m, 1m),
            IsGlobalPolicyCompliant: isGlobalPolicyCompliant,
            HasRestrictionConflict: hasRestrictionConflict,
            AffectedUsers: scope.AffectedUsers,
            AffectedSymbols: scope.AffectedSymbols);
    }

    public async Task<AutonomyDecisionResult> EvaluateAsync(
        AutonomyDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedActorUserId = NormalizeRequired(request.ActorUserId, 450, nameof(request.ActorUserId));
        var normalizedAction = NormalizeRequired(request.SuggestedAction, 128, nameof(request.SuggestedAction)).ToUpperInvariant();
        var normalizedReason = NormalizeRequired(request.Reason, 512, nameof(request.Reason));
        var normalizedCorrelationId = NormalizeOptional(request.CorrelationId, 128);
        var confidenceScore = NormalizeConfidenceScore(request.ConfidenceScore ?? optionsValue.DefaultConfidenceScore);
        var simulation = await SimulateAsync(
            new PreFlightSimulationRequest(
                SuggestedAction: normalizedAction,
                ConfidenceScore: confidenceScore,
                ScopeKey: request.ScopeKey,
                UserId: request.UserId,
                Symbol: request.Symbol,
                Environment: request.Environment,
                Side: request.Side,
                Quantity: request.Quantity,
                Price: request.Price,
                BreakerKind: request.BreakerKind),
            cancellationToken);
        var policySnapshot = await globalPolicyEngine.GetSnapshotAsync(cancellationToken);
        var isSafeAction = AutoExecutableActions.Contains(normalizedAction);
        var canAutoExecute =
            isSafeAction &&
            policySnapshot.Policy.AutonomyPolicy.Mode == AutonomyPolicyMode.LowRiskAutoAct &&
            simulation.IsGlobalPolicyCompliant &&
            !simulation.HasRestrictionConflict &&
            simulation.FalsePositiveProbability <= optionsValue.MaxFalsePositiveProbability;

        if (!canAutoExecute)
        {
            var queuedReview = await QueueReviewAsync(
                normalizedAction,
                normalizedReason,
                simulation,
                request.ScopeKey,
                confidenceScore,
                normalizedCorrelationId,
                request.UserId,
                request.Symbol,
                request.BreakerKind,
                cancellationToken);

            await incidentHook.WriteIncidentAsync(
                new AutonomyIncidentHookRequest(
                    normalizedActorUserId,
                    queuedReview.ScopeKey,
                    "Autonomy action queued for manual review.",
                    $"Action={normalizedAction}; PolicyMode={policySnapshot.Policy.AutonomyPolicy.Mode}; GlobalPolicyCompliant={simulation.IsGlobalPolicyCompliant}; RestrictionConflict={simulation.HasRestrictionConflict}; FalsePositiveProbability={simulation.FalsePositiveProbability:0.####}",
                    normalizedCorrelationId),
                cancellationToken);

            return new AutonomyDecisionResult(
                simulation,
                AutoExecuted: false,
                ReviewQueued: true,
                ApprovalId: queuedReview.ApprovalId,
                Outcome: "ReviewQueued",
                Detail: $"Action={normalizedAction}; ApprovalId={queuedReview.ApprovalId}");
        }

        var execution = await selfHealingExecutor.ExecuteAsync(
            new SelfHealingActionRequest(
                normalizedActorUserId,
                normalizedAction,
                normalizedReason,
                normalizedCorrelationId,
                request.JobKey,
                request.Symbol,
                request.BreakerKind),
            cancellationToken);

        if (!execution.IsExecuted)
        {
            var queuedReview = await QueueReviewAsync(
                normalizedAction,
                normalizedReason,
                simulation,
                request.ScopeKey,
                confidenceScore,
                normalizedCorrelationId,
                request.UserId,
                request.Symbol,
                request.BreakerKind,
                cancellationToken);

            await incidentHook.WriteIncidentAsync(
                new AutonomyIncidentHookRequest(
                    normalizedActorUserId,
                    queuedReview.ScopeKey,
                    "Autonomy action execution failed and was queued for review.",
                    execution.Detail ?? execution.Outcome,
                    normalizedCorrelationId),
                cancellationToken);

            return new AutonomyDecisionResult(
                simulation,
                AutoExecuted: false,
                ReviewQueued: true,
                ApprovalId: queuedReview.ApprovalId,
                Outcome: execution.Outcome,
                Detail: execution.Detail);
        }

        if (request.BreakerKind is DependencyCircuitBreakerKind breakerKind &&
            breakerKind != DependencyCircuitBreakerKind.WebSocket)
        {
            var probe = await selfHealingExecutor.ProbeAsync(
                breakerKind,
                normalizedActorUserId,
                normalizedCorrelationId,
                request.JobKey,
                request.Symbol,
                cancellationToken);

            if (!probe.IsExecuted)
            {
                var queuedReview = await QueueReviewAsync(
                    normalizedAction,
                    normalizedReason,
                    simulation,
                    request.ScopeKey,
                    confidenceScore,
                    normalizedCorrelationId,
                    request.UserId,
                    request.Symbol,
                    request.BreakerKind,
                    cancellationToken);

                await incidentHook.WriteIncidentAsync(
                    new AutonomyIncidentHookRequest(
                        normalizedActorUserId,
                        queuedReview.ScopeKey,
                        "Autonomy probe failed after self-healing action.",
                        probe.Detail ?? probe.Outcome,
                        normalizedCorrelationId),
                    cancellationToken);

                return new AutonomyDecisionResult(
                    simulation,
                    AutoExecuted: true,
                    ReviewQueued: true,
                    ApprovalId: queuedReview.ApprovalId,
                    Outcome: probe.Outcome,
                    Detail: probe.Detail);
            }
        }

        await incidentHook.WriteRecoveryAsync(
            new AutonomyRecoveryHookRequest(
                normalizedActorUserId,
                ResolveScopeKey(request.ScopeKey, request.UserId, request.Symbol, request.BreakerKind),
                $"Autonomy action {normalizedAction} executed successfully.",
                normalizedCorrelationId),
            cancellationToken);

        logger.LogInformation(
            "Autonomy action {SuggestedAction} executed successfully. CorrelationId={CorrelationId}.",
            normalizedAction,
            normalizedCorrelationId ?? "none");

        return new AutonomyDecisionResult(
            simulation,
            AutoExecuted: true,
            ReviewQueued: false,
            ApprovalId: null,
            Outcome: execution.Outcome,
            Detail: execution.Detail);
    }

    private async Task<AutonomyReviewQueueItem> QueueReviewAsync(
        string suggestedAction,
        string reason,
        PreFlightSimulationResult simulation,
        string? scopeKey,
        decimal confidenceScore,
        string? correlationId,
        string? userId,
        string? symbol,
        DependencyCircuitBreakerKind? breakerKind,
        CancellationToken cancellationToken)
    {
        return await reviewQueueService.EnqueueAsync(
            new AutonomyReviewQueueEnqueueRequest(
                ApprovalId: BuildApprovalId(suggestedAction, scopeKey, reason),
                ScopeKey: ResolveScopeKey(scopeKey, userId, symbol, breakerKind),
                SuggestedAction: suggestedAction,
                ConfidenceScore: confidenceScore,
                AffectedUsers: simulation.AffectedUsers,
                AffectedSymbols: simulation.AffectedSymbols,
                ExpiresAtUtc: timeProvider.GetUtcNow().UtcDateTime.AddMinutes(optionsValue.ReviewQueueTtlMinutes),
                Reason: reason,
                CorrelationId: correlationId),
            cancellationToken);
    }

    private async Task<(int OpenPositionCount, decimal EstimatedExposure, IReadOnlyCollection<string> AffectedUsers, IReadOnlyCollection<string> AffectedSymbols)> ResolveScopeAsync(
        string? userId,
        string? symbol,
        CancellationToken cancellationToken)
    {
        var demoPositions = dbContext.DemoPositions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity => !entity.IsDeleted && entity.Quantity != 0m);
        var exchangePositions = dbContext.ExchangePositions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity => !entity.IsDeleted && entity.Quantity != 0m);
        var openOrders = dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity =>
                !entity.IsDeleted &&
                (entity.State == ExecutionOrderState.Received ||
                 entity.State == ExecutionOrderState.GatePassed ||
                 entity.State == ExecutionOrderState.Dispatching ||
                 entity.State == ExecutionOrderState.Submitted ||
                 entity.State == ExecutionOrderState.PartiallyFilled));

        if (!string.IsNullOrWhiteSpace(userId))
        {
            demoPositions = demoPositions.Where(entity => entity.OwnerUserId == userId);
            exchangePositions = exchangePositions.Where(entity => entity.OwnerUserId == userId);
            openOrders = openOrders.Where(entity => entity.OwnerUserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            demoPositions = demoPositions.Where(entity => entity.Symbol == symbol);
            exchangePositions = exchangePositions.Where(entity => entity.Symbol == symbol);
            openOrders = openOrders.Where(entity => entity.Symbol == symbol);
        }

        var demoRows = await demoPositions
            .Select(entity => new { entity.OwnerUserId, entity.Symbol, Exposure = CalculateDemoExposure(entity) })
            .ToListAsync(cancellationToken);
        var exchangeRows = await exchangePositions
            .Select(entity => new { entity.OwnerUserId, entity.Symbol, Exposure = CalculateExchangeExposure(entity) })
            .ToListAsync(cancellationToken);
        var orderRows = await openOrders
            .Select(entity => new { entity.OwnerUserId, entity.Symbol })
            .ToListAsync(cancellationToken);
        var affectedUsers = new HashSet<string>(StringComparer.Ordinal);
        var affectedSymbols = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in demoRows)
        {
            affectedUsers.Add(row.OwnerUserId);
            affectedSymbols.Add(row.Symbol);
        }

        foreach (var row in exchangeRows)
        {
            affectedUsers.Add(row.OwnerUserId);
            affectedSymbols.Add(row.Symbol);
        }

        foreach (var row in orderRows)
        {
            affectedUsers.Add(row.OwnerUserId);
            affectedSymbols.Add(row.Symbol);
        }

        return (
            OpenPositionCount: demoRows.Count + exchangeRows.Count,
            EstimatedExposure: demoRows.Sum(row => row.Exposure) + exchangeRows.Sum(row => row.Exposure),
            AffectedUsers: affectedUsers.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            AffectedSymbols: affectedSymbols.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    private static decimal CalculateDemoExposure(DemoPosition position)
    {
        if (position.LastMarkPrice is decimal markPrice && markPrice > 0m)
        {
            return Math.Abs(markPrice * position.Quantity);
        }

        if (position.AverageEntryPrice > 0m)
        {
            return Math.Abs(position.AverageEntryPrice * position.Quantity);
        }

        return Math.Abs(position.CostBasis + position.UnrealizedPnl);
    }

    private static decimal CalculateExchangeExposure(ExchangePosition position)
    {
        var referencePrice = position.EntryPrice > 0m
            ? position.EntryPrice
            : position.BreakEvenPrice > 0m
                ? position.BreakEvenPrice
                : 0m;

        return Math.Abs(referencePrice * position.Quantity);
    }

    private static string ResolveLiquidityImpact(string suggestedAction, decimal? quantity, decimal? price)
    {
        if (suggestedAction is AutonomySuggestedActions.WebSocketReconnect or
            AutonomySuggestedActions.SignalRReconnect or
            AutonomySuggestedActions.WorkerRetry)
        {
            return "None";
        }

        if (suggestedAction == AutonomySuggestedActions.CacheRebuild)
        {
            return "Low";
        }

        var notional = (quantity ?? 0m) * (price ?? 0m);

        if (notional >= 250_000m)
        {
            return "High";
        }

        if (notional >= 25_000m)
        {
            return "Medium";
        }

        return notional > 0m ? "Low" : "None";
    }

    private static string ResolveRateLimitImpact(string suggestedAction, int? rateLimitUsage)
    {
        if (suggestedAction == AutonomySuggestedActions.CacheRebuild)
        {
            if ((rateLimitUsage ?? 0) >= 1000)
            {
                return "High";
            }

            if ((rateLimitUsage ?? 0) >= 800)
            {
                return "Medium";
            }

            return "Low";
        }

        return (rateLimitUsage ?? 0) >= 1000
            ? "High"
            : "None";
    }

    private static decimal NormalizeConfidenceScore(decimal value)
    {
        return Math.Clamp(value, 0m, 1m);
    }

    private static string ResolveScopeKey(
        string? explicitScopeKey,
        string? userId,
        string? symbol,
        DependencyCircuitBreakerKind? breakerKind)
    {
        if (!string.IsNullOrWhiteSpace(explicitScopeKey))
        {
            return explicitScopeKey.Trim();
        }

        if (breakerKind.HasValue)
        {
            return $"BREAKER:{breakerKind.Value.ToString().ToUpperInvariant()}";
        }

        if (!string.IsNullOrWhiteSpace(userId))
        {
            return $"USER:{userId.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            return $"SYMBOL:{symbol.Trim().ToUpperInvariant()}";
        }

        return "GLOBAL";
    }

    private static string BuildApprovalId(string suggestedAction, string? scopeKey, string reason)
    {
        var payload = string.Join("|", suggestedAction, scopeKey ?? "GLOBAL", reason, Guid.NewGuid().ToString("N"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return $"autonomy-{Convert.ToHexStringLower(hash)[..20]}";
    }

    private static string NormalizeRequired(string? value, int maxLength, string parameterName)
    {
        var normalizedValue = NormalizeOptional(value, maxLength);

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        return normalizedValue;
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
}
