using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Administration;

public sealed partial class ApprovalWorkflowService(
    ApplicationDbContext dbContext,
    IAdminAuditLogService adminAuditLogService,
    IAdminCommandRegistry adminCommandRegistry,
    IGlobalSystemStateService globalSystemStateService,
    IGlobalPolicyEngine globalPolicyEngine,
    TimeProvider timeProvider,
    ILogger<ApprovalWorkflowService> logger) : IApprovalWorkflowService
{
    public async Task<ApprovalQueueDetailSnapshot> EnqueueAsync(ApprovalQueueEnqueueRequest request, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeEnqueueRequest(request);
        var approvalReference = BuildReference(normalized.ApprovalReference, "APR");
        var incidentReference = BuildReference(normalized.IncidentReference, "INC");
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;

        if (normalized.ExpiresAtUtc <= utcNow)
        {
            throw new InvalidOperationException("Approval expiry must be in the future.");
        }

        if (await dbContext.ApprovalQueues.AnyAsync(entity => entity.ApprovalReference == approvalReference, cancellationToken))
        {
            throw new InvalidOperationException($"Approval reference '{approvalReference}' already exists.");
        }

        var incident = await ResolveIncidentAsync(incidentReference, normalized, cancellationToken);
        var queue = new ApprovalQueue
        {
            Id = Guid.NewGuid(),
            ApprovalReference = approvalReference,
            OperationType = normalized.OperationType,
            Status = ApprovalQueueStatus.Pending,
            Severity = normalized.Severity,
            Title = normalized.Title,
            Summary = normalized.Summary,
            TargetType = normalized.TargetType,
            TargetId = normalized.TargetId,
            RequestedByUserId = normalized.RequestedByUserId,
            RequiredApprovals = normalized.RequiredApprovals,
            ApprovalCount = 0,
            ExpiresAtUtc = normalized.ExpiresAtUtc,
            Reason = normalized.Reason,
            PayloadJson = normalized.PayloadJson,
            PayloadHash = ComputeHash(normalized.PayloadJson),
            CorrelationId = normalized.CorrelationId,
            CommandId = normalized.CommandId,
            DecisionId = normalized.DecisionId,
            ExecutionAttemptId = normalized.ExecutionAttemptId,
            IncidentId = incident.Id,
            IncidentReference = incident.IncidentReference,
            SystemStateHistoryReference = normalized.SystemStateHistoryReference,
            DependencyCircuitBreakerStateReference = normalized.DependencyCircuitBreakerStateReference,
            LastActorUserId = normalized.RequestedByUserId
        };

        incident.ApprovalQueueId = queue.Id;
        incident.ApprovalReference = queue.ApprovalReference;
        incident.OperationType = normalized.OperationType;
        incident.Severity = normalized.Severity;
        incident.Status = IncidentStatus.PendingApproval;
        incident.Title = normalized.Title;
        incident.Summary = normalized.Summary;
        incident.Detail = normalized.PayloadJson;
        incident.TargetType = normalized.TargetType;
        incident.TargetId = normalized.TargetId;
        incident.CorrelationId = normalized.CorrelationId;
        incident.CommandId = normalized.CommandId;
        incident.DecisionId = normalized.DecisionId;
        incident.ExecutionAttemptId = normalized.ExecutionAttemptId;
        incident.SystemStateHistoryReference = normalized.SystemStateHistoryReference;
        incident.DependencyCircuitBreakerStateReference = normalized.DependencyCircuitBreakerStateReference;
        incident.CreatedByUserId = normalized.RequestedByUserId;

        dbContext.ApprovalQueues.Add(queue);
        dbContext.IncidentEvents.AddRange(
            new IncidentEvent
            {
                Id = Guid.NewGuid(),
                IncidentId = incident.Id,
                IncidentReference = incident.IncidentReference,
                EventType = IncidentEventType.IncidentCreated,
                Message = normalized.Title,
                ActorUserId = normalized.RequestedByUserId,
                CorrelationId = normalized.CorrelationId,
                CommandId = normalized.CommandId,
                DecisionId = normalized.DecisionId,
                ExecutionAttemptId = normalized.ExecutionAttemptId,
                ApprovalReference = queue.ApprovalReference,
                SystemStateHistoryReference = normalized.SystemStateHistoryReference,
                DependencyCircuitBreakerStateReference = normalized.DependencyCircuitBreakerStateReference,
                PayloadJson = normalized.PayloadJson
            },
            new IncidentEvent
            {
                Id = Guid.NewGuid(),
                IncidentId = incident.Id,
                IncidentReference = incident.IncidentReference,
                EventType = IncidentEventType.ApprovalQueued,
                Message = $"Queued {normalized.OperationType} for approval ({normalized.RequiredApprovals} required).",
                ActorUserId = normalized.RequestedByUserId,
                CorrelationId = normalized.CorrelationId,
                CommandId = normalized.CommandId,
                DecisionId = normalized.DecisionId,
                ExecutionAttemptId = normalized.ExecutionAttemptId,
                ApprovalReference = queue.ApprovalReference,
                SystemStateHistoryReference = normalized.SystemStateHistoryReference,
                DependencyCircuitBreakerStateReference = normalized.DependencyCircuitBreakerStateReference,
                PayloadJson = normalized.PayloadJson
            });

        await dbContext.SaveChangesAsync(cancellationToken);

        await WriteAuditAsync(
            normalized.RequestedByUserId,
            "Admin.Approval.Queue.Enqueue",
            queue,
            oldValueSummary: null,
            newValueSummary: BuildQueueSummary(queue),
            normalized.Reason,
            normalized.CorrelationId,
            cancellationToken);

        return await BuildDetailAsync(queue, cancellationToken);
    }

    public async Task<IReadOnlyCollection<ApprovalQueueListItem>> ListPendingAsync(int take = 100, CancellationToken cancellationToken = default)
    {
        await ExpirePendingAsync(cancellationToken);
        var queues = await dbContext.ApprovalQueues.AsNoTracking()
            .Where(entity => entity.Status == ApprovalQueueStatus.Pending)
            .OrderByDescending(entity => entity.CreatedDate)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(cancellationToken);
        return queues.Select(MapListItem).ToArray();
    }

    public async Task<ApprovalQueueDetailSnapshot?> GetDetailAsync(string approvalReference, CancellationToken cancellationToken = default)
    {
        await ExpirePendingAsync(cancellationToken);
        var normalizedApprovalReference = NormalizeRequired(approvalReference, 128, nameof(approvalReference));
        var queue = await dbContext.ApprovalQueues.AsNoTracking().SingleOrDefaultAsync(entity => entity.ApprovalReference == normalizedApprovalReference, cancellationToken);
        return queue is null ? null : await BuildDetailAsync(queue, cancellationToken);
    }

    public async Task<ApprovalQueueDetailSnapshot> ApproveAsync(ApprovalQueueDecisionRequest request, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeDecisionRequest(request, requireReason: false);
        await ExpirePendingAsync(cancellationToken);
        var queue = await LoadQueueAsync(normalized.ApprovalReference, cancellationToken);
        EnsureActionable(queue);
        EnsureNotExpired(queue);
        EnsureCanAct(queue, normalized.ActorUserId);

        if (await dbContext.ApprovalActions.AnyAsync(entity => entity.ApprovalQueueId == queue.Id && entity.ActorUserId == normalized.ActorUserId, cancellationToken))
        {
            throw new InvalidOperationException("A different approver is required for each approval step.");
        }

        var finalApproval = queue.ApprovalCount + 1 >= queue.RequiredApprovals;
        queue.ApprovalCount += 1;
        queue.LastActorUserId = normalized.ActorUserId;
        if (finalApproval)
        {
            queue.Status = ApprovalQueueStatus.Approved;
            queue.ApprovedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        }

        dbContext.ApprovalActions.Add(CreateAction(queue, ApprovalActionType.Approved, normalized.ActorUserId, normalized.Reason, normalized.CorrelationId));
        var incident = await LoadIncidentAsync(queue, cancellationToken);
        dbContext.IncidentEvents.Add(new IncidentEvent
        {
            Id = Guid.NewGuid(),
            IncidentId = incident.Id,
            IncidentReference = incident.IncidentReference,
            EventType = finalApproval ? IncidentEventType.IncidentEscalated : IncidentEventType.ApprovalRecorded,
            Message = finalApproval
                ? $"Final approval recorded by {normalized.ActorUserId}."
                : $"Approval recorded by {normalized.ActorUserId}.",
            ActorUserId = normalized.ActorUserId,
            CorrelationId = normalized.CorrelationId ?? queue.CorrelationId,
            CommandId = queue.CommandId,
            DecisionId = queue.DecisionId,
            ExecutionAttemptId = queue.ExecutionAttemptId,
            ApprovalReference = queue.ApprovalReference,
            SystemStateHistoryReference = queue.SystemStateHistoryReference,
            DependencyCircuitBreakerStateReference = queue.DependencyCircuitBreakerStateReference,
            PayloadJson = queue.PayloadJson
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(normalized.ActorUserId, "Admin.Approval.Queue.Approved", queue, null, BuildQueueSummary(queue), normalized.Reason ?? "Approval granted", normalized.CorrelationId, cancellationToken);

        if (!finalApproval)
        {
            return await BuildDetailAsync(queue, cancellationToken);
        }

        try
        {
            await ExecuteApprovedOperationAsync(queue, normalized.ActorUserId, cancellationToken);
            await FinalizeOutcomeAsync(queue, normalized.ActorUserId, queue.Reason, normalized.CorrelationId, isSuccess: true, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Approval queue {ApprovalReference} execution failed after approval.", queue.ApprovalReference);
            await FinalizeOutcomeAsync(queue, normalized.ActorUserId, Truncate(exception.Message, 2048), normalized.CorrelationId, isSuccess: false, cancellationToken);
        }

        return await BuildDetailAsync(queue, cancellationToken);
    }

    public async Task<ApprovalQueueDetailSnapshot> RejectAsync(ApprovalQueueDecisionRequest request, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeDecisionRequest(request, requireReason: true);
        await ExpirePendingAsync(cancellationToken);
        var queue = await LoadQueueAsync(normalized.ApprovalReference, cancellationToken);
        EnsureActionable(queue);
        EnsureNotExpired(queue);
        EnsureCanAct(queue, normalized.ActorUserId);

        if (await dbContext.ApprovalActions.AnyAsync(entity => entity.ApprovalQueueId == queue.Id && entity.ActorUserId == normalized.ActorUserId, cancellationToken))
        {
            throw new InvalidOperationException("A different approver is required for each approval step.");
        }

        queue.Status = ApprovalQueueStatus.Rejected;
        queue.RejectedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        queue.RejectReason = normalized.Reason;
        queue.LastActorUserId = normalized.ActorUserId;
        dbContext.ApprovalActions.Add(CreateAction(queue, ApprovalActionType.Rejected, normalized.ActorUserId, normalized.Reason, normalized.CorrelationId));

        var incident = await LoadIncidentAsync(queue, cancellationToken);
        incident.Status = IncidentStatus.Rejected;
        incident.ResolvedAtUtc = queue.RejectedAtUtc;
        incident.ResolvedByUserId = normalized.ActorUserId;
        incident.ResolvedSummary = normalized.Reason;
        dbContext.IncidentEvents.Add(new IncidentEvent
        {
            Id = Guid.NewGuid(),
            IncidentId = incident.Id,
            IncidentReference = incident.IncidentReference,
            EventType = IncidentEventType.ApprovalRejected,
            Message = normalized.Reason,
            ActorUserId = normalized.ActorUserId,
            CorrelationId = normalized.CorrelationId ?? queue.CorrelationId,
            CommandId = queue.CommandId,
            DecisionId = queue.DecisionId,
            ExecutionAttemptId = queue.ExecutionAttemptId,
            ApprovalReference = queue.ApprovalReference,
            SystemStateHistoryReference = queue.SystemStateHistoryReference,
            DependencyCircuitBreakerStateReference = queue.DependencyCircuitBreakerStateReference,
            PayloadJson = queue.PayloadJson
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await CompleteCommandAsync(queue, AdminCommandStatus.Failed, normalized.Reason, normalized.CorrelationId, cancellationToken);
        await WriteAuditAsync(normalized.ActorUserId, "Admin.Approval.Queue.Rejected", queue, null, BuildQueueSummary(queue), normalized.Reason, normalized.CorrelationId, cancellationToken);
        return await BuildDetailAsync(queue, cancellationToken);
    }

    public async Task<ApprovalQueueDetailSnapshot> MarkExecutedAsync(string approvalReference, string actorUserId, string? summary, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        var queue = await LoadQueueAsync(approvalReference, cancellationToken);
        if (queue.Status != ApprovalQueueStatus.Approved)
        {
            throw new InvalidOperationException($"Approval queue '{queue.ApprovalReference}' must be approved before it can be executed.");
        }
        await FinalizeOutcomeAsync(queue, actorUserId, summary, correlationId, isSuccess: true, cancellationToken);
        return await BuildDetailAsync(queue, cancellationToken);
    }

    public async Task<ApprovalQueueDetailSnapshot> MarkFailedAsync(string approvalReference, string actorUserId, string summary, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        var queue = await LoadQueueAsync(approvalReference, cancellationToken);
        if (queue.Status != ApprovalQueueStatus.Approved)
        {
            throw new InvalidOperationException($"Approval queue '{queue.ApprovalReference}' must be approved before it can be marked failed.");
        }
        await FinalizeOutcomeAsync(queue, actorUserId, summary, correlationId, isSuccess: false, cancellationToken);
        return await BuildDetailAsync(queue, cancellationToken);
    }

    public async Task<int> ExpirePendingAsync(CancellationToken cancellationToken = default)
    {
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var expiredQueues = await dbContext.ApprovalQueues.Where(entity => entity.Status == ApprovalQueueStatus.Pending && entity.ExpiresAtUtc <= utcNow).ToListAsync(cancellationToken);
        if (expiredQueues.Count == 0)
        {
            return 0;
        }

        foreach (var queue in expiredQueues)
        {
            queue.Status = ApprovalQueueStatus.Expired;
            queue.ExpiredAtUtc = utcNow;
            queue.LastActorUserId = "system";
            var incident = await LoadIncidentAsync(queue, cancellationToken);
            incident.Status = IncidentStatus.Expired;
            incident.ResolvedAtUtc = utcNow;
            incident.ResolvedByUserId = "system";
            incident.ResolvedSummary = "Approval expired.";
            dbContext.IncidentEvents.Add(new IncidentEvent
            {
                Id = Guid.NewGuid(),
                IncidentId = incident.Id,
                IncidentReference = incident.IncidentReference,
                EventType = IncidentEventType.ApprovalExpired,
                Message = "Approval expired.",
                ActorUserId = "system",
                CorrelationId = queue.CorrelationId,
                CommandId = queue.CommandId,
                DecisionId = queue.DecisionId,
                ExecutionAttemptId = queue.ExecutionAttemptId,
                ApprovalReference = queue.ApprovalReference,
                SystemStateHistoryReference = queue.SystemStateHistoryReference,
                DependencyCircuitBreakerStateReference = queue.DependencyCircuitBreakerStateReference,
                PayloadJson = queue.PayloadJson
            });
            await CompleteCommandAsync(queue, AdminCommandStatus.Failed, "Approval expired.", queue.CorrelationId, cancellationToken);
            await WriteAuditAsync("system", "Admin.Approval.Queue.Expired", queue, null, BuildQueueSummary(queue), "Approval expired.", queue.CorrelationId, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return expiredQueues.Count;
    }
}
