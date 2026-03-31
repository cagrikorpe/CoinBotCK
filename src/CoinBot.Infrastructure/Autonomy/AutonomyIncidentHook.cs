using System.Text.Json;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Autonomy;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Autonomy;

public sealed class AutonomyIncidentHook(
    ApplicationDbContext dbContext,
    IAdminAuditLogService adminAuditLogService,
    TimeProvider timeProvider) : IAutonomyIncidentHook
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task WriteIncidentAsync(
        AutonomyIncidentHookRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequest = Normalize(request);
        var (incident, isNew) = await ResolveIncidentAsync(normalizedRequest, cancellationToken);
        incident.Severity = normalizedRequest.Severity;
        incident.Status = normalizedRequest.Severity == IncidentSeverity.Critical
            ? IncidentStatus.Open
            : IncidentStatus.Monitoring;
        incident.OperationType = null;
        incident.Title = normalizedRequest.Summary;
        incident.Summary = normalizedRequest.Summary;
        incident.Detail = normalizedRequest.Detail;
        incident.TargetType = "Autonomy";
        incident.TargetId = normalizedRequest.Scope;
        incident.CorrelationId = normalizedRequest.CorrelationId;
        incident.CreatedByUserId = normalizedRequest.ActorUserId;
        incident.ResolvedAtUtc = null;
        incident.ResolvedByUserId = null;
        incident.ResolvedSummary = null;

        if (!await HasDuplicateIncidentEventAsync(
                incident.Id,
                isNew ? IncidentEventType.IncidentCreated : IncidentEventType.IncidentEscalated,
                normalizedRequest.Summary,
                cancellationToken))
        {
            dbContext.IncidentEvents.Add(new IncidentEvent
            {
                Id = Guid.NewGuid(),
                IncidentId = incident.Id,
                IncidentReference = incident.IncidentReference,
                EventType = isNew ? IncidentEventType.IncidentCreated : IncidentEventType.IncidentEscalated,
                Message = normalizedRequest.Summary,
                ActorUserId = normalizedRequest.ActorUserId,
                CorrelationId = normalizedRequest.CorrelationId,
                PayloadJson = JsonSerializer.Serialize(normalizedRequest, SerializerOptions)
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await adminAuditLogService.WriteAsync(
            new AdminAuditLogWriteRequest(
                normalizedRequest.ActorUserId,
                "Admin.Autonomy.Incident",
                "Autonomy",
                normalizedRequest.Scope,
                OldValueSummary: null,
                NewValueSummary: Truncate(normalizedRequest.Detail, 2048),
                Reason: Truncate(normalizedRequest.Summary, 512) ?? "Autonomy incident",
                IpAddress: null,
                UserAgent: null,
                normalizedRequest.CorrelationId),
            cancellationToken);
    }

    public async Task WriteRecoveryAsync(
        AutonomyRecoveryHookRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequest = Normalize(request);
        var (incident, isNew) = await ResolveRecoveryIncidentAsync(normalizedRequest, cancellationToken);
        incident.Status = IncidentStatus.Resolved;
        incident.ResolvedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        incident.ResolvedByUserId = normalizedRequest.ActorUserId;
        incident.ResolvedSummary = normalizedRequest.Summary;
        incident.Title = string.IsNullOrWhiteSpace(incident.Title) ? normalizedRequest.Summary : incident.Title;
        incident.Summary = string.IsNullOrWhiteSpace(incident.Summary) ? normalizedRequest.Summary : incident.Summary;

        if (!await HasDuplicateIncidentEventAsync(
                incident.Id,
                isNew ? IncidentEventType.RecoveryRecorded : IncidentEventType.IncidentResolved,
                normalizedRequest.Summary,
                cancellationToken))
        {
            dbContext.IncidentEvents.Add(new IncidentEvent
            {
                Id = Guid.NewGuid(),
                IncidentId = incident.Id,
                IncidentReference = incident.IncidentReference,
                EventType = isNew ? IncidentEventType.RecoveryRecorded : IncidentEventType.IncidentResolved,
                Message = normalizedRequest.Summary,
                ActorUserId = normalizedRequest.ActorUserId,
                CorrelationId = normalizedRequest.CorrelationId,
                PayloadJson = JsonSerializer.Serialize(normalizedRequest, SerializerOptions)
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await adminAuditLogService.WriteAsync(
            new AdminAuditLogWriteRequest(
                normalizedRequest.ActorUserId,
                "Admin.Autonomy.Recovery",
                "Autonomy",
                normalizedRequest.Scope,
                OldValueSummary: null,
                NewValueSummary: Truncate(normalizedRequest.Summary, 2048),
                Reason: "Autonomy recovery summary",
                IpAddress: null,
                UserAgent: null,
                normalizedRequest.CorrelationId),
            cancellationToken);
    }

    private async Task<(Incident Incident, bool IsNew)> ResolveIncidentAsync(
        NormalizedIncidentRequest request,
        CancellationToken cancellationToken)
    {
        var incident = await dbContext.Incidents
            .SingleOrDefaultAsync(
                entity =>
                    entity.TargetType == "Autonomy" &&
                    entity.TargetId == request.Scope &&
                    entity.Status != IncidentStatus.Resolved &&
                    entity.Status != IncidentStatus.Rejected &&
                    entity.Status != IncidentStatus.Expired &&
                    entity.Status != IncidentStatus.Cancelled &&
                    entity.Status != IncidentStatus.Failed,
                cancellationToken);

        if (incident is not null)
        {
            return (incident, false);
        }

        incident = new Incident
        {
            Id = Guid.NewGuid(),
            IncidentReference = BuildIncidentReference(),
            Severity = request.Severity,
            Status = request.Severity == IncidentSeverity.Critical ? IncidentStatus.Open : IncidentStatus.Monitoring,
            Title = request.Summary,
            Summary = request.Summary,
            Detail = request.Detail,
            TargetType = "Autonomy",
            TargetId = request.Scope,
            CorrelationId = request.CorrelationId,
            CreatedByUserId = request.ActorUserId
        };

        dbContext.Incidents.Add(incident);
        return (incident, true);
    }

    private async Task<(Incident Incident, bool IsNew)> ResolveRecoveryIncidentAsync(
        NormalizedRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        var incident = await dbContext.Incidents
            .SingleOrDefaultAsync(
                entity =>
                    entity.TargetType == "Autonomy" &&
                    entity.TargetId == request.Scope &&
                    entity.Status != IncidentStatus.Resolved &&
                    entity.Status != IncidentStatus.Rejected &&
                    entity.Status != IncidentStatus.Expired &&
                    entity.Status != IncidentStatus.Cancelled &&
                    entity.Status != IncidentStatus.Failed,
                cancellationToken);

        if (incident is not null)
        {
            return (incident, false);
        }

        incident = new Incident
        {
            Id = Guid.NewGuid(),
            IncidentReference = BuildIncidentReference(),
            Severity = IncidentSeverity.Info,
            Status = IncidentStatus.Resolved,
            Title = request.Summary,
            Summary = request.Summary,
            Detail = request.Summary,
            TargetType = "Autonomy",
            TargetId = request.Scope,
            CorrelationId = request.CorrelationId,
            CreatedByUserId = request.ActorUserId
        };

        dbContext.Incidents.Add(incident);
        return (incident, true);
    }

    private async Task<bool> HasDuplicateIncidentEventAsync(
        Guid incidentId,
        IncidentEventType eventType,
        string summary,
        CancellationToken cancellationToken)
    {
        return await dbContext.IncidentEvents
            .AsNoTracking()
            .AnyAsync(
                entity =>
                    entity.IncidentId == incidentId &&
                    entity.EventType == eventType &&
                    entity.Message == summary,
                cancellationToken);
    }

    private static string BuildIncidentReference()
    {
        return $"INC-{Guid.NewGuid():N}";
    }

    private static NormalizedIncidentRequest Normalize(AutonomyIncidentHookRequest request)
    {
        return new NormalizedIncidentRequest(
            request.ActorUserId.Trim(),
            request.Scope.Trim(),
            Truncate(request.Summary, 512) ?? "Autonomy incident",
            Truncate(request.Detail, 8192) ?? "Autonomy incident detail",
            Truncate(request.CorrelationId, 128),
            request.Severity);
    }

    private static NormalizedRecoveryRequest Normalize(AutonomyRecoveryHookRequest request)
    {
        return new NormalizedRecoveryRequest(
            request.ActorUserId.Trim(),
            request.Scope.Trim(),
            Truncate(request.Summary, 512) ?? "Autonomy recovery",
            Truncate(request.CorrelationId, 128));
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

    private sealed record NormalizedIncidentRequest(
        string ActorUserId,
        string Scope,
        string Summary,
        string Detail,
        string? CorrelationId,
        IncidentSeverity Severity);

    private sealed record NormalizedRecoveryRequest(
        string ActorUserId,
        string Scope,
        string Summary,
        string? CorrelationId);
}
