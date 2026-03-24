using CoinBot.Application.Abstractions.Administration;
using CoinBot.Domain.Entities;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Administration;

public sealed class AdminGovernanceReadModelService(ApplicationDbContext dbContext) : IAdminGovernanceReadModelService
{
    public async Task<IReadOnlyCollection<IncidentListItem>> ListIncidentsAsync(int take = 100, CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(take, 1, 200);
        var incidents = await dbContext.Incidents
            .AsNoTracking()
            .OrderByDescending(entity => entity.CreatedDate)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var counts = await dbContext.IncidentEvents
            .AsNoTracking()
            .GroupBy(entity => entity.IncidentId)
            .Select(group => new { IncidentId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.IncidentId, item => item.Count, cancellationToken);

        return incidents.Select(incident => new IncidentListItem(
            incident.IncidentReference,
            incident.Severity,
            incident.Status,
            incident.Title,
            incident.Summary,
            incident.OperationType,
            incident.TargetType,
            incident.TargetId,
            incident.CorrelationId,
            incident.CommandId,
            incident.ApprovalReference,
            counts.TryGetValue(incident.Id, out var count) ? count : 0,
            incident.CreatedDate,
            incident.UpdatedDate)).ToArray();
    }

    public async Task<IncidentDetailSnapshot?> GetIncidentDetailAsync(string incidentReference, CancellationToken cancellationToken = default)
    {
        var normalizedReference = incidentReference?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedReference))
        {
            throw new ArgumentException("Incident reference is required.", nameof(incidentReference));
        }

        var incident = await dbContext.Incidents
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.IncidentReference == normalizedReference, cancellationToken);

        if (incident is null)
        {
            return null;
        }

        var events = await dbContext.IncidentEvents
            .AsNoTracking()
            .Where(entity => entity.IncidentId == incident.Id)
            .OrderBy(entity => entity.CreatedDate)
            .ToListAsync(cancellationToken);

        return new IncidentDetailSnapshot(
            incident.IncidentReference,
            incident.Severity,
            incident.Status,
            incident.Title,
            incident.Summary,
            incident.Detail,
            incident.OperationType,
            incident.TargetType,
            incident.TargetId,
            incident.CorrelationId,
            incident.CommandId,
            incident.DecisionId,
            incident.ExecutionAttemptId,
            incident.ApprovalReference,
            incident.SystemStateHistoryReference,
            incident.DependencyCircuitBreakerStateReference,
            incident.CreatedByUserId,
            incident.CreatedDate,
            incident.ResolvedAtUtc,
            incident.ResolvedByUserId,
            incident.ResolvedSummary,
            events.Select(MapEvent).ToArray());
    }

    public async Task<IReadOnlyCollection<SystemStateHistoryListItem>> ListSystemStateHistoryAsync(int take = 100, CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(take, 1, 200);
        var histories = await dbContext.SystemStateHistories
            .AsNoTracking()
            .OrderByDescending(entity => entity.Version)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return histories.Select(history => new SystemStateHistoryListItem(
            history.HistoryReference,
            history.Version,
            history.State,
            history.ReasonCode,
            history.Source,
            history.IsManualOverride,
            history.ExpiresAtUtc,
            history.CorrelationId,
            history.ApprovalReference,
            history.IncidentReference,
            history.CreatedDate)).ToArray();
    }

    public async Task<SystemStateHistoryDetailSnapshot?> GetSystemStateHistoryDetailAsync(string historyReference, CancellationToken cancellationToken = default)
    {
        var normalizedReference = historyReference?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedReference))
        {
            throw new ArgumentException("History reference is required.", nameof(historyReference));
        }

        var history = await dbContext.SystemStateHistories
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.HistoryReference == normalizedReference, cancellationToken);

        return history is null
            ? null
            : new SystemStateHistoryDetailSnapshot(
                history.HistoryReference,
                history.Version,
                history.State,
                history.ReasonCode,
                history.Message,
                history.Source,
                history.IsManualOverride,
                history.ExpiresAtUtc,
                history.CorrelationId,
                history.CommandId,
                history.ApprovalReference,
                history.IncidentReference,
                history.DependencyCircuitBreakerStateReference,
                history.BreakerKind,
                history.BreakerStateCode,
                history.UpdatedByUserId,
                history.UpdatedFromIp,
                history.PreviousState,
                history.ChangeSummary,
                history.CreatedDate);
    }

    private static IncidentEventSnapshot MapEvent(IncidentEvent @event)
    {
        return new IncidentEventSnapshot(
            @event.EventType,
            @event.Message,
            @event.ActorUserId,
            @event.CorrelationId,
            @event.CommandId,
            @event.DecisionId,
            @event.ExecutionAttemptId,
            @event.ApprovalReference,
            @event.SystemStateHistoryReference,
            @event.DependencyCircuitBreakerStateReference,
            @event.PayloadJson,
            @event.CreatedDate);
    }
}
