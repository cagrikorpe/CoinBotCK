using System.Text.Json;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Administration;

public sealed class CrisisIncidentHook(
    ApplicationDbContext dbContext,
    IAdminAuditLogService adminAuditLogService,
    TimeProvider timeProvider) : ICrisisIncidentHook
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task WriteIncidentAsync(
        CrisisIncidentHookRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequest = Normalize(
            request.ActorUserId,
            request.Level,
            request.Scope,
            request.Summary,
            request.Detail,
            request.CorrelationId,
            request.CommandId);

        var (incident, isNew) = await ResolveIncidentAsync(normalizedRequest, IncidentStatus.Open, cancellationToken);
        incident.Severity = ResolveSeverity(normalizedRequest.Level);
        incident.Status = IncidentStatus.Open;
        incident.OperationType = ApprovalQueueOperationType.CrisisEscalationExecute;
        incident.Title = normalizedRequest.Summary;
        incident.Summary = normalizedRequest.Summary;
        incident.Detail = normalizedRequest.Detail;
        incident.TargetType = "CrisisEscalation";
        incident.TargetId = normalizedRequest.Scope;
        incident.CorrelationId = normalizedRequest.CorrelationId;
        incident.CommandId = normalizedRequest.CommandId;
        incident.CreatedByUserId = normalizedRequest.ActorUserId;
        incident.ResolvedAtUtc = null;
        incident.ResolvedByUserId = null;
        incident.ResolvedSummary = null;

        dbContext.IncidentEvents.Add(new IncidentEvent
        {
            Id = Guid.NewGuid(),
            IncidentId = incident.Id,
            IncidentReference = incident.IncidentReference,
            EventType = isNew ? IncidentEventType.IncidentCreated : IncidentEventType.IncidentEscalated,
            Message = normalizedRequest.Summary,
            ActorUserId = normalizedRequest.ActorUserId,
            CorrelationId = normalizedRequest.CorrelationId,
            CommandId = normalizedRequest.CommandId,
            PayloadJson = BuildPayloadJson(normalizedRequest)
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        await adminAuditLogService.WriteAsync(
            new AdminAuditLogWriteRequest(
                normalizedRequest.ActorUserId,
                "Admin.Crisis.Incident",
                "CrisisEscalation",
                normalizedRequest.Scope,
                OldValueSummary: null,
                NewValueSummary: Truncate(normalizedRequest.Detail, 2048),
                Reason: Truncate(normalizedRequest.Summary, 512) ?? "Crisis incident",
                IpAddress: null,
                UserAgent: null,
                normalizedRequest.CorrelationId),
            cancellationToken);
    }

    public async Task WriteRecoveryAsync(
        CrisisRecoveryHookRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequest = Normalize(
            request.ActorUserId,
            request.Level,
            request.Scope,
            request.Summary,
            detail: null,
            request.CorrelationId,
            request.CommandId);

        var (incident, isNew) = await ResolveIncidentAsync(normalizedRequest, IncidentStatus.Resolved, cancellationToken);
        incident.Severity = ResolveSeverity(normalizedRequest.Level);
        incident.Status = IncidentStatus.Resolved;
        incident.OperationType = ApprovalQueueOperationType.CrisisEscalationExecute;
        incident.Title = string.IsNullOrWhiteSpace(incident.Title) ? normalizedRequest.Summary : incident.Title;
        incident.Summary = string.IsNullOrWhiteSpace(incident.Summary) ? normalizedRequest.Summary : incident.Summary;
        incident.CommandId = normalizedRequest.CommandId ?? incident.CommandId;
        incident.ResolvedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        incident.ResolvedByUserId = normalizedRequest.ActorUserId;
        incident.ResolvedSummary = normalizedRequest.Summary;

        dbContext.IncidentEvents.Add(new IncidentEvent
        {
            Id = Guid.NewGuid(),
            IncidentId = incident.Id,
            IncidentReference = incident.IncidentReference,
            EventType = isNew ? IncidentEventType.RecoveryRecorded : IncidentEventType.IncidentResolved,
            Message = normalizedRequest.Summary,
            ActorUserId = normalizedRequest.ActorUserId,
            CorrelationId = normalizedRequest.CorrelationId,
            CommandId = normalizedRequest.CommandId,
            PayloadJson = BuildPayloadJson(normalizedRequest)
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        await adminAuditLogService.WriteAsync(
            new AdminAuditLogWriteRequest(
                normalizedRequest.ActorUserId,
                "Admin.Crisis.Recovery",
                "CrisisEscalation",
                normalizedRequest.Scope,
                OldValueSummary: null,
                NewValueSummary: Truncate(normalizedRequest.Summary, 2048),
                Reason: "Crisis recovery summary",
                IpAddress: null,
                UserAgent: null,
                normalizedRequest.CorrelationId),
            cancellationToken);
    }

    private async Task<(Incident Incident, bool IsNew)> ResolveIncidentAsync(
        NormalizedCrisisRequest request,
        IncidentStatus status,
        CancellationToken cancellationToken)
    {
        var incident = await dbContext.Incidents
            .SingleOrDefaultAsync(
                entity =>
                    entity.TargetType == "CrisisEscalation" &&
                    entity.TargetId == request.Scope &&
                    entity.Status != IncidentStatus.Resolved &&
                    entity.Status != IncidentStatus.Rejected &&
                    entity.Status != IncidentStatus.Expired &&
                    entity.Status != IncidentStatus.Cancelled &&
                    entity.Status != IncidentStatus.Failed &&
                    (
                        (!string.IsNullOrWhiteSpace(request.CommandId) && entity.CommandId == request.CommandId) ||
                        entity.CorrelationId == request.CorrelationId
                    ),
                cancellationToken);

        if (incident is not null)
        {
            return (incident, false);
        }

        incident = new Incident
        {
            Id = Guid.NewGuid(),
            IncidentReference = BuildIncidentReference(),
            Severity = ResolveSeverity(request.Level),
            Status = status,
            OperationType = ApprovalQueueOperationType.CrisisEscalationExecute,
            Title = request.Summary,
            Summary = request.Summary,
            Detail = request.Detail ?? request.Summary,
            TargetType = "CrisisEscalation",
            TargetId = request.Scope,
            CorrelationId = request.CorrelationId,
            CommandId = request.CommandId,
            CreatedByUserId = request.ActorUserId
        };

        dbContext.Incidents.Add(incident);
        return (incident, true);
    }

    private static IncidentSeverity ResolveSeverity(CrisisEscalationLevel level)
    {
        return level switch
        {
            CrisisEscalationLevel.SoftHalt => IncidentSeverity.Warning,
            CrisisEscalationLevel.OrderPurge => IncidentSeverity.Critical,
            CrisisEscalationLevel.EmergencyFlatten => IncidentSeverity.Critical,
            _ => IncidentSeverity.Warning
        };
    }

    private static string BuildIncidentReference()
    {
        return $"INC-{Guid.NewGuid():N}";
    }

    private static string BuildPayloadJson(NormalizedCrisisRequest request)
    {
        return JsonSerializer.Serialize(request, SerializerOptions);
    }

    private static NormalizedCrisisRequest Normalize(
        string actorUserId,
        CrisisEscalationLevel level,
        string scope,
        string summary,
        string? detail,
        string? correlationId,
        string? commandId)
    {
        return new NormalizedCrisisRequest(
            actorUserId.Trim(),
            level,
            scope.Trim(),
            Truncate(summary, 512) ?? "Crisis incident",
            Truncate(detail, 8192),
            Truncate(correlationId, 128),
            Truncate(commandId, 128));
    }

    private static string? Truncate(string? value, int maxLength)
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

    private sealed record NormalizedCrisisRequest(
        string ActorUserId,
        CrisisEscalationLevel Level,
        string Scope,
        string Summary,
        string? Detail,
        string? CorrelationId,
        string? CommandId);
}
