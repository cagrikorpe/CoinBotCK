using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Administration;

public sealed class GlobalSystemStateService(
    ApplicationDbContext dbContext,
    IAuditLogService auditLogService,
    TimeProvider timeProvider) : IGlobalSystemStateService
{
    public async Task<GlobalSystemStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.GlobalSystemStates
            .SingleOrDefaultAsync(entry => entry.Id == GlobalSystemStateDefaults.SingletonId, cancellationToken);

        if (entity is null)
        {
            return GlobalSystemStateDefaults.CreateSnapshot();
        }

        if (ShouldExpireAutomatically(entity))
        {
            return await SetStateAsync(
                new GlobalSystemStateSetRequest(
                    GlobalSystemStateKind.Active,
                    GlobalSystemStateDefaults.DefaultReasonCode,
                    Message: null,
                    GlobalSystemStateDefaults.DefaultSource,
                    CorrelationId: null,
                    IsManualOverride: false,
                    ExpiresAtUtc: null,
                    UpdatedByUserId: "system:global-state-expiry",
                    UpdatedFromIp: null,
                    ChangeSummary: "Automatic expiration of non-manual global system state."),
                cancellationToken);
        }

        return MapSnapshot(entity, isPersisted: true);
    }


    private bool ShouldExpireAutomatically(GlobalSystemState entity)
    {
        return !entity.IsManualOverride &&
               entity.ExpiresAtUtc.HasValue &&
               entity.ExpiresAtUtc.Value <= timeProvider.GetUtcNow().UtcDateTime;
    }

    public async Task<GlobalSystemStateSnapshot> SetStateAsync(
        GlobalSystemStateSetRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequest = NormalizeRequest(request);
        var entity = await GetOrCreateTrackedEntityAsync(cancellationToken);
        var previousState = entity.State;
        var previousReasonCode = entity.ReasonCode;
        var previousMessage = entity.Message;
        var previousSource = entity.Source;
        var previousCorrelationId = entity.CorrelationId;
        var previousIsManualOverride = entity.IsManualOverride;
        var previousExpiresAtUtc = entity.ExpiresAtUtc;
        var hasChanged =
            entity.State != normalizedRequest.State ||
            !string.Equals(entity.ReasonCode, normalizedRequest.ReasonCode, StringComparison.Ordinal) ||
            !string.Equals(entity.Message, normalizedRequest.Message, StringComparison.Ordinal) ||
            !string.Equals(entity.Source, normalizedRequest.Source, StringComparison.Ordinal) ||
            !string.Equals(entity.CorrelationId, normalizedRequest.CorrelationId, StringComparison.Ordinal) ||
            entity.IsManualOverride != normalizedRequest.IsManualOverride ||
            entity.ExpiresAtUtc != normalizedRequest.ExpiresAtUtc ||
            !string.Equals(entity.UpdatedByUserId, normalizedRequest.UpdatedByUserId, StringComparison.Ordinal) ||
            !string.Equals(entity.UpdatedFromIp, normalizedRequest.UpdatedFromIp, StringComparison.Ordinal);

        entity.State = normalizedRequest.State;
        entity.ReasonCode = normalizedRequest.ReasonCode;
        entity.Message = normalizedRequest.Message;
        entity.Source = normalizedRequest.Source;
        entity.CorrelationId = normalizedRequest.CorrelationId;
        entity.IsManualOverride = normalizedRequest.IsManualOverride;
        entity.ExpiresAtUtc = normalizedRequest.ExpiresAtUtc;
        entity.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        entity.UpdatedByUserId = normalizedRequest.UpdatedByUserId;
        entity.UpdatedFromIp = normalizedRequest.UpdatedFromIp;

        if (hasChanged)
        {
            entity.Version = entity.Version == 0
                ? 1
                : entity.Version + 1;

            dbContext.SystemStateHistories.Add(
                new SystemStateHistory
                {
                    Id = Guid.NewGuid(),
                    GlobalSystemStateId = entity.Id,
                    HistoryReference = BuildHistoryReference(entity.Version),
                    Version = entity.Version,
                    State = entity.State,
                    ReasonCode = entity.ReasonCode,
                    Message = entity.Message,
                    Source = entity.Source,
                    IsManualOverride = entity.IsManualOverride,
                    ExpiresAtUtc = entity.ExpiresAtUtc,
                    CorrelationId = entity.CorrelationId,
                    CommandId = normalizedRequest.CommandId,
                    ApprovalReference = normalizedRequest.ApprovalReference,
                    IncidentReference = normalizedRequest.IncidentReference,
                    DependencyCircuitBreakerStateReference = normalizedRequest.DependencyCircuitBreakerStateReference,
                    BreakerKind = normalizedRequest.BreakerKind,
                    BreakerStateCode = normalizedRequest.BreakerStateCode,
                    UpdatedByUserId = normalizedRequest.UpdatedByUserId,
                    UpdatedFromIp = normalizedRequest.UpdatedFromIp,
                    PreviousState = previousState.ToString(),
                    ChangeSummary = NormalizeOptional(normalizedRequest.ChangeSummary, 512) ?? BuildChangeSummary(
                        previousState,
                        previousReasonCode,
                        previousMessage,
                        previousSource,
                        previousCorrelationId,
                        previousIsManualOverride,
                        previousExpiresAtUtc,
                        normalizedRequest)
                });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var snapshot = MapSnapshot(entity, isPersisted: true);

        await auditLogService.WriteAsync(
            new AuditLogWriteRequest(
                normalizedRequest.UpdatedByUserId,
                $"GlobalSystemState.{normalizedRequest.State}",
                "GlobalSystemState",
                BuildAuditContext(snapshot, normalizedRequest),
                normalizedRequest.CorrelationId,
                hasChanged ? "Applied" : "NoChange",
                "Admin"),
            cancellationToken);

        return snapshot;
    }

    private async Task<GlobalSystemState> GetOrCreateTrackedEntityAsync(CancellationToken cancellationToken)
    {
        var entity = await dbContext.GlobalSystemStates
            .SingleOrDefaultAsync(entry => entry.Id == GlobalSystemStateDefaults.SingletonId, cancellationToken);

        if (entity is not null)
        {
            return entity;
        }

        entity = GlobalSystemStateDefaults.CreateEntity();
        dbContext.GlobalSystemStates.Add(entity);
        return entity;
    }

    private static GlobalSystemStateSetRequest NormalizeRequest(GlobalSystemStateSetRequest request)
    {
        return request with
        {
            ReasonCode = NormalizeRequired(request.ReasonCode, 64, nameof(request.ReasonCode)),
            Message = NormalizeOptional(request.Message, 512),
            Source = NormalizeRequired(request.Source, 128, nameof(request.Source)),
            CorrelationId = NormalizeOptional(request.CorrelationId, 128),
            UpdatedByUserId = NormalizeRequired(request.UpdatedByUserId, 450, nameof(request.UpdatedByUserId)),
            UpdatedFromIp = NormalizeOptional(request.UpdatedFromIp, 128),
            CommandId = NormalizeOptional(request.CommandId, 128),
            ApprovalReference = NormalizeOptional(request.ApprovalReference, 128),
            IncidentReference = NormalizeOptional(request.IncidentReference, 128),
            DependencyCircuitBreakerStateReference = NormalizeOptional(request.DependencyCircuitBreakerStateReference, 128),
            BreakerKind = NormalizeOptional(request.BreakerKind, 64),
            BreakerStateCode = NormalizeOptional(request.BreakerStateCode, 32),
            ChangeSummary = NormalizeOptional(request.ChangeSummary, 512),
            ExpiresAtUtc = request.ExpiresAtUtc?.ToUniversalTime()
        };
    }

    private static GlobalSystemStateSnapshot MapSnapshot(GlobalSystemState entity, bool isPersisted)
    {
        return new GlobalSystemStateSnapshot(
            entity.State,
            entity.ReasonCode,
            entity.Message,
            entity.Source,
            entity.CorrelationId,
            entity.IsManualOverride,
            entity.ExpiresAtUtc,
            entity.UpdatedAtUtc,
            entity.UpdatedByUserId,
            entity.UpdatedFromIp,
            entity.Version,
            isPersisted);
    }

    private static string BuildAuditContext(GlobalSystemStateSnapshot snapshot, GlobalSystemStateSetRequest request)
    {
        var context = string.Join(
            " | ",
            $"ReasonCode={snapshot.ReasonCode}",
            $"Source={snapshot.Source}",
            $"Version={snapshot.Version}",
            $"Manual={snapshot.IsManualOverride}",
            $"ExpiresAtUtc={snapshot.ExpiresAtUtc?.ToString("O") ?? "none"}",
            string.IsNullOrWhiteSpace(request.CommandId) ? "Command=none" : $"Command={request.CommandId}",
            string.IsNullOrWhiteSpace(request.ApprovalReference) ? "Approval=none" : $"Approval={request.ApprovalReference}",
            string.IsNullOrWhiteSpace(request.IncidentReference) ? "Incident=none" : $"Incident={request.IncidentReference}");

        return context.Length <= 2048
            ? context
            : context[..2048];
    }

    private static string BuildHistoryReference(long version)
    {
        return $"GST-{version:000000}";
    }

    private static string BuildChangeSummary(
        GlobalSystemStateKind previousState,
        string previousReasonCode,
        string? previousMessage,
        string previousSource,
        string? previousCorrelationId,
        bool previousIsManualOverride,
        DateTime? previousExpiresAtUtc,
        GlobalSystemStateSetRequest request)
    {
        var summary = string.Join(
            " | ",
            $"State {previousState} -> {request.State}",
            $"Reason {previousReasonCode} -> {request.ReasonCode}",
            $"Source {previousSource} -> {request.Source}",
            $"Manual {previousIsManualOverride} -> {request.IsManualOverride}",
            $"PrevMessage={(previousMessage is null ? "none" : Truncate(previousMessage, 64))}",
            $"NewMessage={(request.Message is null ? "none" : Truncate(request.Message, 64))}",
            $"PrevCorrelation={(previousCorrelationId is null ? "none" : previousCorrelationId)}",
            $"NewCorrelation={(request.CorrelationId is null ? "none" : request.CorrelationId)}",
            $"PrevExpiresAtUtc={previousExpiresAtUtc?.ToString("O") ?? "none"}",
            $"NewExpiresAtUtc={request.ExpiresAtUtc?.ToUniversalTime().ToString("O") ?? "none"}",
            $"Command={(request.CommandId is null ? "none" : request.CommandId)}",
            $"Approval={(request.ApprovalReference is null ? "none" : request.ApprovalReference)}",
            $"Incident={(request.IncidentReference is null ? "none" : request.IncidentReference)}",
            $"Breaker={(request.DependencyCircuitBreakerStateReference is null ? "none" : request.DependencyCircuitBreakerStateReference)}");

        return summary.Length <= 512
            ? summary
            : summary[..512];
    }

    private static string NormalizeRequired(string? value, int maxLength, string parameterName)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : throw new ArgumentOutOfRangeException(parameterName, $"The value cannot exceed {maxLength} characters.");
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
            : throw new ArgumentOutOfRangeException(nameof(value), $"The value cannot exceed {maxLength} characters.");
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        var normalizedValue = value.Trim();
        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : normalizedValue[..maxLength];
    }
}
