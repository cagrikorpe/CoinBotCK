using System.Security.Cryptography;
using System.Text;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Autonomy;
using CoinBot.Infrastructure.Alerts;
using CoinBot.Infrastructure.Dashboard;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Autonomy;

public sealed class DependencyCircuitBreakerStateManager(
    ApplicationDbContext dbContext,
    IGlobalSystemStateService globalSystemStateService,
    IAutonomyReviewQueueService reviewQueueService,
    IAutonomyIncidentHook incidentHook,
    IAdminAuditLogService adminAuditLogService,
    IOptions<DependencyCircuitBreakerOptions> options,
    TimeProvider timeProvider,
    ILogger<DependencyCircuitBreakerStateManager> logger,
    IAlertDispatchCoordinator? alertDispatchCoordinator = null,
    IHostEnvironment? hostEnvironment = null,
    UserOperationsStreamHub? userOperationsStreamHub = null) : IDependencyCircuitBreakerStateManager
{
    private const string BreakerSource = "Autonomy.DependencyBreaker";
    private readonly DependencyCircuitBreakerOptions optionsValue = options.Value;

    public async Task<DependencyCircuitBreakerSnapshot> GetSnapshotAsync(
        DependencyCircuitBreakerKind breakerKind,
        CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateTrackedStateAsync(breakerKind, cancellationToken);

        if (dbContext.Entry(entity).State == EntityState.Added)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Map(entity, isPersisted: true);
    }

    public async Task<IReadOnlyCollection<DependencyCircuitBreakerSnapshot>> ListSnapshotsAsync(
        CancellationToken cancellationToken = default)
    {
        var snapshots = new List<DependencyCircuitBreakerSnapshot>();

        foreach (var breakerKind in Enum.GetValues<DependencyCircuitBreakerKind>())
        {
            snapshots.Add(await GetSnapshotAsync(breakerKind, cancellationToken));
        }

        return snapshots;
    }

    public async Task<DependencyCircuitBreakerSnapshot> RecordFailureAsync(
        DependencyCircuitBreakerFailureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedActor = NormalizeRequired(request.ActorUserId, 450, nameof(request.ActorUserId));
        var normalizedErrorCode = NormalizeRequired(request.ErrorCode, 64, nameof(request.ErrorCode));
        var normalizedErrorMessage = NormalizeRequired(request.ErrorMessage, 512, nameof(request.ErrorMessage));
        var normalizedCorrelationId = NormalizeOptional(request.CorrelationId, 128);
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var entity = await GetOrCreateTrackedStateAsync(request.BreakerKind, cancellationToken);
        var previousSnapshot = Map(entity, isPersisted: dbContext.Entry(entity).State != EntityState.Added);

        entity.ConsecutiveFailureCount = Math.Max(0, entity.ConsecutiveFailureCount) + 1;
        entity.LastFailureAtUtc = nowUtc;
        entity.LastErrorCode = normalizedErrorCode;
        entity.LastErrorMessage = normalizedErrorMessage;
        entity.CorrelationId = normalizedCorrelationId;

        if (entity.StateCode == CircuitBreakerStateCode.HalfOpen ||
            entity.ConsecutiveFailureCount >= optionsValue.FailureThreshold)
        {
            entity.StateCode = CircuitBreakerStateCode.Cooldown;
            entity.CooldownUntilUtc = nowUtc.AddSeconds(optionsValue.CooldownSeconds);
            entity.HalfOpenStartedAtUtc = null;
            entity.LastProbeAtUtc = nowUtc;
        }
        else
        {
            entity.StateCode = CircuitBreakerStateCode.Retrying;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var snapshot = Map(entity, isPersisted: true);
        await WriteStateTransitionAuditAsync(normalizedActor, snapshot, previousSnapshot, cancellationToken);
        await TrySendBreakerAlertAsync(snapshot, previousSnapshot, cancellationToken);
        TryPublishOperationsUpdate(snapshot, previousSnapshot);

        if (snapshot.StateCode == CircuitBreakerStateCode.Cooldown)
        {
            await EnterDegradedModeAsync(snapshot, normalizedActor, normalizedCorrelationId, cancellationToken);
            await incidentHook.WriteIncidentAsync(
                new AutonomyIncidentHookRequest(
                    normalizedActor,
                    BuildScopeKey(snapshot.BreakerKind),
                    $"Dependency breaker {snapshot.BreakerKind} entered cooldown.",
                    BuildIncidentDetail(snapshot),
                    normalizedCorrelationId),
                cancellationToken);
            await reviewQueueService.EnqueueAsync(
                new AutonomyReviewQueueEnqueueRequest(
                    ApprovalId: BuildApprovalId(snapshot.BreakerKind, "cooldown"),
                    ScopeKey: BuildScopeKey(snapshot.BreakerKind),
                    SuggestedAction: ResolveSuggestedAction(snapshot.BreakerKind),
                    ConfidenceScore: ResolveDefaultConfidenceScore(snapshot.BreakerKind),
                    AffectedUsers: Array.Empty<string>(),
                    AffectedSymbols: Array.Empty<string>(),
                    ExpiresAtUtc: nowUtc.AddMinutes(optionsValue.ReviewQueueTtlMinutes),
                    Reason: $"Dependency breaker {snapshot.BreakerKind} requires review after cooldown.",
                    CorrelationId: normalizedCorrelationId),
                cancellationToken);

            logger.LogWarning(
                "Dependency breaker {BreakerKind} entered cooldown after {FailureCount} failures.",
                snapshot.BreakerKind,
                snapshot.ConsecutiveFailureCount);
        }

        return snapshot;
    }

    public async Task<DependencyCircuitBreakerSnapshot> RecordSuccessAsync(
        DependencyCircuitBreakerSuccessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedActor = NormalizeRequired(request.ActorUserId, 450, nameof(request.ActorUserId));
        var normalizedCorrelationId = NormalizeOptional(request.CorrelationId, 128);
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var entity = await GetOrCreateTrackedStateAsync(request.BreakerKind, cancellationToken);
        var previousSnapshot = Map(entity, isPersisted: dbContext.Entry(entity).State != EntityState.Added);

        entity.StateCode = CircuitBreakerStateCode.Closed;
        entity.ConsecutiveFailureCount = 0;
        entity.LastSuccessAtUtc = nowUtc;
        entity.CooldownUntilUtc = null;
        entity.HalfOpenStartedAtUtc = null;
        entity.LastProbeAtUtc = null;
        entity.LastErrorCode = null;
        entity.LastErrorMessage = null;
        entity.CorrelationId = normalizedCorrelationId;

        await dbContext.SaveChangesAsync(cancellationToken);

        var snapshot = Map(entity, isPersisted: true);
        await WriteStateTransitionAuditAsync(normalizedActor, snapshot, previousSnapshot, cancellationToken);
        await TrySendBreakerAlertAsync(snapshot, previousSnapshot, cancellationToken);
        TryPublishOperationsUpdate(snapshot, previousSnapshot);

        if (previousSnapshot.StateCode != CircuitBreakerStateCode.Closed)
        {
            await TryRecoverGlobalStateAsync(normalizedActor, normalizedCorrelationId, cancellationToken);
            await incidentHook.WriteRecoveryAsync(
                new AutonomyRecoveryHookRequest(
                    normalizedActor,
                    BuildScopeKey(snapshot.BreakerKind),
                    $"Dependency breaker {snapshot.BreakerKind} recovered to Closed.",
                    normalizedCorrelationId),
                cancellationToken);
        }

        return snapshot;
    }

    public async Task<DependencyCircuitBreakerSnapshot?> TryBeginHalfOpenAsync(
        DependencyCircuitBreakerHalfOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedActor = NormalizeRequired(request.ActorUserId, 450, nameof(request.ActorUserId));
        var normalizedCorrelationId = NormalizeOptional(request.CorrelationId, 128);
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var entity = await GetOrCreateTrackedStateAsync(request.BreakerKind, cancellationToken);

        if (entity.StateCode != CircuitBreakerStateCode.Cooldown ||
            entity.CooldownUntilUtc is null ||
            entity.CooldownUntilUtc > nowUtc)
        {
            return null;
        }

        var previousSnapshot = Map(entity, isPersisted: dbContext.Entry(entity).State != EntityState.Added);
        entity.StateCode = CircuitBreakerStateCode.HalfOpen;
        entity.HalfOpenStartedAtUtc = nowUtc;
        entity.LastProbeAtUtc = nowUtc;
        entity.CorrelationId = normalizedCorrelationId;

        await dbContext.SaveChangesAsync(cancellationToken);

        var snapshot = Map(entity, isPersisted: true);
        await WriteStateTransitionAuditAsync(normalizedActor, snapshot, previousSnapshot, cancellationToken);
        TryPublishOperationsUpdate(snapshot, previousSnapshot);
        return snapshot;
    }

    private async Task<DependencyCircuitBreakerState> GetOrCreateTrackedStateAsync(
        DependencyCircuitBreakerKind breakerKind,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.DependencyCircuitBreakerStates
            .SingleOrDefaultAsync(item => !item.IsDeleted && item.BreakerKind == breakerKind, cancellationToken);

        if (entity is not null)
        {
            return entity;
        }

        entity = new DependencyCircuitBreakerState
        {
            Id = Guid.NewGuid(),
            BreakerKind = breakerKind,
            StateCode = CircuitBreakerStateCode.Closed
        };
        dbContext.DependencyCircuitBreakerStates.Add(entity);
        return entity;
    }

    private async Task WriteStateTransitionAuditAsync(
        string actorUserId,
        DependencyCircuitBreakerSnapshot snapshot,
        DependencyCircuitBreakerSnapshot previousSnapshot,
        CancellationToken cancellationToken)
    {
        await adminAuditLogService.WriteAsync(
            new AdminAuditLogWriteRequest(
                actorUserId,
                $"Autonomy.Breaker.{snapshot.BreakerKind}.{snapshot.StateCode}",
                "DependencyCircuitBreaker",
                snapshot.BreakerKind.ToString(),
                OldValueSummary: BuildSummary(previousSnapshot),
                NewValueSummary: BuildSummary(snapshot),
                Reason: $"State transition from {previousSnapshot.StateCode} to {snapshot.StateCode}.",
                IpAddress: null,
                UserAgent: null,
                snapshot.CorrelationId),
            cancellationToken);
    }

    private async Task EnterDegradedModeAsync(
        DependencyCircuitBreakerSnapshot snapshot,
        string actorUserId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        await globalSystemStateService.SetStateAsync(
            new GlobalSystemStateSetRequest(
                GlobalSystemStateKind.Degraded,
                $"AUTONOMY_BREAKER_{snapshot.BreakerKind.ToString().ToUpperInvariant()}",
                $"Dependency breaker {snapshot.BreakerKind} is in cooldown.",
                BreakerSource,
                correlationId,
                IsManualOverride: false,
                ExpiresAtUtc: snapshot.CooldownUntilUtc,
                UpdatedByUserId: actorUserId,
                UpdatedFromIp: null,
                DependencyCircuitBreakerStateReference: snapshot.BreakerKind.ToString(),
                BreakerKind: snapshot.BreakerKind.ToString(),
                BreakerStateCode: snapshot.StateCode.ToString(),
                ChangeSummary: $"Breaker {snapshot.BreakerKind} entered degraded mode."),
            cancellationToken);
    }

    private async Task TryRecoverGlobalStateAsync(
        string actorUserId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var hasOpenBreaker = await dbContext.DependencyCircuitBreakerStates
            .IgnoreQueryFilters()
            .AnyAsync(
                entity =>
                    !entity.IsDeleted &&
                    entity.StateCode != CircuitBreakerStateCode.Closed,
                cancellationToken);

        if (hasOpenBreaker)
        {
            return;
        }

        var currentSystemState = await globalSystemStateService.GetSnapshotAsync(cancellationToken);

        if (currentSystemState.State != GlobalSystemStateKind.Degraded ||
            !string.Equals(currentSystemState.Source, BreakerSource, StringComparison.Ordinal))
        {
            return;
        }

        await globalSystemStateService.SetStateAsync(
            new GlobalSystemStateSetRequest(
                GlobalSystemStateKind.Active,
                "AUTONOMY_BREAKER_RECOVERED",
                "All dependency breakers recovered.",
                BreakerSource,
                correlationId,
                IsManualOverride: false,
                ExpiresAtUtc: null,
                UpdatedByUserId: actorUserId,
                UpdatedFromIp: null,
                ChangeSummary: "All dependency breakers recovered."),
            cancellationToken);
    }

    private static DependencyCircuitBreakerSnapshot Map(DependencyCircuitBreakerState entity, bool isPersisted)
    {
        return new DependencyCircuitBreakerSnapshot(
            entity.BreakerKind,
            entity.StateCode,
            entity.ConsecutiveFailureCount,
            entity.LastFailureAtUtc,
            entity.LastSuccessAtUtc,
            entity.CooldownUntilUtc,
            entity.HalfOpenStartedAtUtc,
            entity.LastProbeAtUtc,
            entity.LastErrorCode,
            entity.LastErrorMessage,
            entity.CorrelationId,
            isPersisted);
    }

    private static string BuildScopeKey(DependencyCircuitBreakerKind breakerKind)
    {
        return $"BREAKER:{breakerKind.ToString().ToUpperInvariant()}";
    }

    private static string ResolveSuggestedAction(DependencyCircuitBreakerKind breakerKind)
    {
        return breakerKind switch
        {
            DependencyCircuitBreakerKind.WebSocket => AutonomySuggestedActions.WebSocketReconnect,
            DependencyCircuitBreakerKind.RestMarketData => AutonomySuggestedActions.CacheRebuild,
            DependencyCircuitBreakerKind.OrderExecution => AutonomySuggestedActions.WorkerRetry,
            DependencyCircuitBreakerKind.AccountValidation => AutonomySuggestedActions.WorkerRetry,
            _ => AutonomySuggestedActions.WorkerRetry
        };
    }

    private static decimal ResolveDefaultConfidenceScore(DependencyCircuitBreakerKind breakerKind)
    {
        return breakerKind switch
        {
            DependencyCircuitBreakerKind.WebSocket => 0.92m,
            DependencyCircuitBreakerKind.RestMarketData => 0.86m,
            DependencyCircuitBreakerKind.OrderExecution => 0.75m,
            DependencyCircuitBreakerKind.AccountValidation => 0.80m,
            _ => 0.70m
        };
    }

    private static string BuildIncidentDetail(DependencyCircuitBreakerSnapshot snapshot)
    {
        return
            $"State={snapshot.StateCode}; FailureCount={snapshot.ConsecutiveFailureCount}; ErrorCode={snapshot.LastErrorCode ?? "none"}; Error={snapshot.LastErrorMessage ?? "none"}; CooldownUntilUtc={snapshot.CooldownUntilUtc?.ToString("O") ?? "none"}";
    }

    private static string BuildSummary(DependencyCircuitBreakerSnapshot snapshot)
    {
        var summary =
            $"State={snapshot.StateCode}; FailureCount={snapshot.ConsecutiveFailureCount}; LastFailureAtUtc={snapshot.LastFailureAtUtc?.ToString("O") ?? "none"}; LastSuccessAtUtc={snapshot.LastSuccessAtUtc?.ToString("O") ?? "none"}; CooldownUntilUtc={snapshot.CooldownUntilUtc?.ToString("O") ?? "none"}; ErrorCode={snapshot.LastErrorCode ?? "none"}";

        return summary.Length <= 2048
            ? summary
            : summary[..2048];
    }

    private static string BuildApprovalId(DependencyCircuitBreakerKind breakerKind, string suffix)
    {
        var payload = $"{breakerKind}|{suffix}|{Guid.NewGuid():N}";
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

    private async Task TrySendBreakerAlertAsync(
        DependencyCircuitBreakerSnapshot snapshot,
        DependencyCircuitBreakerSnapshot previousSnapshot,
        CancellationToken cancellationToken)
    {
        if (alertDispatchCoordinator is null ||
            snapshot.BreakerKind != DependencyCircuitBreakerKind.WebSocket ||
            snapshot.StateCode == previousSnapshot.StateCode)
        {
            return;
        }

        var isFailureState = snapshot.StateCode is CircuitBreakerStateCode.Retrying or
            CircuitBreakerStateCode.Cooldown or
            CircuitBreakerStateCode.HalfOpen or
            CircuitBreakerStateCode.Degraded;

        if (!isFailureState)
        {
            return;
        }

        await alertDispatchCoordinator.SendAsync(
            new CoinBot.Application.Abstractions.Alerts.AlertNotification(
                Code: $"WEBSOCKET_{snapshot.StateCode.ToString().ToUpperInvariant()}",
                Severity: snapshot.StateCode == CircuitBreakerStateCode.Cooldown
                    ? CoinBot.Application.Abstractions.Alerts.AlertSeverity.Critical
                    : CoinBot.Application.Abstractions.Alerts.AlertSeverity.Warning,
                Title: "WebSocketDisconnected",
                Message:
                    $"EventType=WebSocketDisconnected; State={snapshot.StateCode}; FailureCode={snapshot.LastErrorCode ?? "none"}; Reason={snapshot.LastErrorMessage ?? "none"}; TimestampUtc={DateTime.UtcNow:O}; Environment={ResolveEnvironmentLabel()}",
                CorrelationId: snapshot.CorrelationId),
            $"websocket-breaker:{snapshot.StateCode}:{snapshot.LastErrorCode ?? "none"}",
            TimeSpan.FromMinutes(5),
            cancellationToken);
    }

    private string ResolveEnvironmentLabel()
    {
        var runtimeLabel = hostEnvironment?.EnvironmentName ?? "Unknown";
        var planeLabel = hostEnvironment?.IsDevelopment() == true
            ? "Testnet"
            : "Live";

        return $"{runtimeLabel}/{planeLabel}";
    }

    private void TryPublishOperationsUpdate(
        DependencyCircuitBreakerSnapshot snapshot,
        DependencyCircuitBreakerSnapshot previousSnapshot)
    {
        if (userOperationsStreamHub is null || snapshot.StateCode == previousSnapshot.StateCode)
        {
            return;
        }

        userOperationsStreamHub.Publish(
            new UserOperationsUpdate(
                "*",
                "CircuitBreakerChanged",
                null,
                null,
                snapshot.StateCode.ToString(),
                snapshot.LastErrorCode,
                timeProvider.GetUtcNow().UtcDateTime));
    }
}
