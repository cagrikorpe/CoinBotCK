using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Administration;

public sealed partial class ApprovalWorkflowService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static string BuildReference(string? reference, string prefix)
    {
        var normalized = reference?.Trim();
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized.Length <= 128
                ? normalized
                : throw new ArgumentOutOfRangeException(nameof(reference), "Reference cannot exceed 128 characters.");
        }

        return $"{prefix}-{Guid.NewGuid():N}";
    }

    private static string ComputeHash(string payloadJson)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson));
        return Convert.ToHexString(hash);
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

    private static T? Deserialize<T>(string payloadJson)
    {
        return JsonSerializer.Deserialize<T>(payloadJson, JsonOptions);
    }

    private static string BuildQueueSummary(ApprovalQueue queue)
    {
        var summary = string.Join(
            " | ",
            $"Operation={queue.OperationType}",
            $"Status={queue.Status}",
            $"Approvals={queue.ApprovalCount}/{queue.RequiredApprovals}",
            $"Severity={queue.Severity}",
            $"ExpiresAtUtc={queue.ExpiresAtUtc:O}",
            $"Target={(queue.TargetType ?? "none")}/{(queue.TargetId ?? "none")}");

        return summary.Length <= 512 ? summary : summary[..512];
    }

    private static ApprovalQueueListItem MapListItem(ApprovalQueue queue)
    {
        return new ApprovalQueueListItem(
            queue.ApprovalReference,
            queue.OperationType,
            queue.Status,
            queue.Severity,
            queue.Title,
            queue.Summary,
            queue.TargetType,
            queue.TargetId,
            queue.RequestedByUserId,
            queue.RequiredApprovals,
            queue.ApprovalCount,
            queue.ExpiresAtUtc,
            queue.CorrelationId,
            queue.CommandId,
            queue.IncidentReference,
            queue.CreatedDate,
            queue.UpdatedDate);
    }

    private static ApprovalActionSnapshot MapAction(ApprovalAction action)
    {
        return new ApprovalActionSnapshot(
            action.Sequence,
            action.ActionType,
            action.ActorUserId,
            action.Reason,
            action.CorrelationId,
            action.CommandId,
            action.DecisionId,
            action.ExecutionAttemptId,
            action.CreatedDate);
    }

    private static ApprovalAction CreateAction(
        ApprovalQueue queue,
        ApprovalActionType actionType,
        string actorUserId,
        string? reason,
        string? correlationId)
    {
        return new ApprovalAction
        {
            Id = Guid.NewGuid(),
            ApprovalQueueId = queue.Id,
            ApprovalReference = queue.ApprovalReference,
            ActionType = actionType,
            Sequence = queue.ApprovalCount + 1,
            ActorUserId = actorUserId,
            Reason = reason,
            CorrelationId = correlationId ?? queue.CorrelationId,
            CommandId = queue.CommandId,
            DecisionId = queue.DecisionId,
            ExecutionAttemptId = queue.ExecutionAttemptId,
            IncidentReference = queue.IncidentReference,
            SystemStateHistoryReference = queue.SystemStateHistoryReference,
            DependencyCircuitBreakerStateReference = queue.DependencyCircuitBreakerStateReference
        };
    }

    private static void EnsureActionable(ApprovalQueue queue, bool allowApproved = false)
    {
        if (queue.Status == ApprovalQueueStatus.Pending || (allowApproved && queue.Status == ApprovalQueueStatus.Approved))
        {
            return;
        }

        throw new InvalidOperationException($"Approval queue '{queue.ApprovalReference}' is not actionable in status '{queue.Status}'.");
    }

    private static void EnsureCanAct(ApprovalQueue queue, string actorUserId)
    {
        if (string.Equals(queue.RequestedByUserId, actorUserId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Requestor cannot approve or reject their own approval item.");
        }
    }

    private static void EnsureNotExpired(ApprovalQueue queue)
    {
        if (queue.Status == ApprovalQueueStatus.Expired || queue.ExpiresAtUtc <= DateTime.UtcNow)
        {
            throw new InvalidOperationException($"Approval queue '{queue.ApprovalReference}' is expired.");
        }
    }

    private static NormalizedEnqueueRequest NormalizeEnqueueRequest(ApprovalQueueEnqueueRequest request)
    {
        return new NormalizedEnqueueRequest(
            request.OperationType,
            request.Severity,
            NormalizeRequired(request.Title, 256, nameof(request.Title)),
            NormalizeRequired(request.Summary, 512, nameof(request.Summary)),
            NormalizeRequired(request.RequestedByUserId, 450, nameof(request.RequestedByUserId)),
            NormalizeRequired(request.Reason, 512, nameof(request.Reason)),
            NormalizeRequired(request.PayloadJson, 8192, nameof(request.PayloadJson)),
            request.RequiredApprovals > 0
                ? request.RequiredApprovals
                : throw new ArgumentOutOfRangeException(nameof(request.RequiredApprovals), "Required approvals must be positive."),
            request.ExpiresAtUtc.ToUniversalTime(),
            NormalizeOptional(request.TargetType, 128),
            NormalizeOptional(request.TargetId, 256),
            NormalizeOptional(request.CorrelationId, 128),
            NormalizeOptional(request.CommandId, 128),
            NormalizeOptional(request.DecisionId, 64),
            NormalizeOptional(request.ExecutionAttemptId, 64),
            NormalizeOptional(request.ApprovalReference, 128),
            NormalizeOptional(request.IncidentReference, 128),
            NormalizeOptional(request.SystemStateHistoryReference, 128),
            NormalizeOptional(request.DependencyCircuitBreakerStateReference, 128));
    }

    private static ApprovalQueueDecisionRequest NormalizeDecisionRequest(
        ApprovalQueueDecisionRequest request,
        bool requireReason)
    {
        return new ApprovalQueueDecisionRequest(
            NormalizeRequired(request.ApprovalReference, 128, nameof(request.ApprovalReference)),
            NormalizeRequired(request.ActorUserId, 450, nameof(request.ActorUserId)),
            requireReason
                ? NormalizeRequired(request.Reason, 512, nameof(request.Reason))
                : NormalizeOptional(request.Reason, 512),
            NormalizeOptional(request.CorrelationId, 128));
    }

    private static string? NormalizeReason(string? reason)
    {
        return NormalizeOptional(reason, 512);
    }

    private async Task<ApprovalQueue> LoadQueueAsync(string approvalReference, CancellationToken cancellationToken)
    {
        var normalizedApprovalReference = NormalizeRequired(approvalReference, 128, nameof(approvalReference));
        var queue = await dbContext.ApprovalQueues.SingleOrDefaultAsync(entity => entity.ApprovalReference == normalizedApprovalReference, cancellationToken);
        return queue ?? throw new InvalidOperationException($"Approval queue '{normalizedApprovalReference}' was not found.");
    }

    private async Task<Incident> LoadIncidentAsync(ApprovalQueue queue, CancellationToken cancellationToken)
    {
        if (queue.IncidentId.HasValue)
        {
            var incident = await dbContext.Incidents.SingleOrDefaultAsync(entity => entity.Id == queue.IncidentId.Value, cancellationToken);
            if (incident is not null)
            {
                return incident;
            }
        }

        if (!string.IsNullOrWhiteSpace(queue.IncidentReference))
        {
            var incident = await dbContext.Incidents.SingleOrDefaultAsync(entity => entity.IncidentReference == queue.IncidentReference, cancellationToken);
            if (incident is not null)
            {
                return incident;
            }
        }

        var created = new Incident
        {
            Id = Guid.NewGuid(),
            IncidentReference = queue.IncidentReference ?? BuildReference(null, "INC"),
            Severity = queue.Severity,
            Status = queue.Status == ApprovalQueueStatus.Pending ? IncidentStatus.PendingApproval : IncidentStatus.Open,
            OperationType = queue.OperationType,
            Title = queue.Title,
            Summary = queue.Summary,
            Detail = queue.PayloadJson,
            TargetType = queue.TargetType,
            TargetId = queue.TargetId,
            CorrelationId = queue.CorrelationId,
            CommandId = queue.CommandId,
            DecisionId = queue.DecisionId,
            ExecutionAttemptId = queue.ExecutionAttemptId,
            ApprovalReference = queue.ApprovalReference,
            SystemStateHistoryReference = queue.SystemStateHistoryReference,
            DependencyCircuitBreakerStateReference = queue.DependencyCircuitBreakerStateReference,
            CreatedByUserId = queue.RequestedByUserId
        };

        created.ApprovalQueueId = queue.Id;

        dbContext.Incidents.Add(created);
        return created;
    }

    private async Task<Incident> ResolveIncidentAsync(string incidentReference, NormalizedEnqueueRequest request, CancellationToken cancellationToken)
    {
        var incident = await dbContext.Incidents.SingleOrDefaultAsync(entity => entity.IncidentReference == incidentReference, cancellationToken);
        if (incident is not null)
        {
            return incident;
        }

        incident = new Incident
        {
            Id = Guid.NewGuid(),
            IncidentReference = incidentReference,
            Severity = request.Severity,
            Status = IncidentStatus.PendingApproval,
            OperationType = request.OperationType,
            Title = request.Title,
            Summary = request.Summary,
            Detail = request.PayloadJson,
            TargetType = request.TargetType,
            TargetId = request.TargetId,
            CorrelationId = request.CorrelationId,
            CommandId = request.CommandId,
            DecisionId = request.DecisionId,
            ExecutionAttemptId = request.ExecutionAttemptId,
            ApprovalReference = null,
            SystemStateHistoryReference = request.SystemStateHistoryReference,
            DependencyCircuitBreakerStateReference = request.DependencyCircuitBreakerStateReference,
            CreatedByUserId = request.RequestedByUserId
        };

        incident.ApprovalQueueId = null;

        dbContext.Incidents.Add(incident);
        return incident;
    }

    private async Task WriteAuditAsync(
        string actorUserId,
        string actionType,
        ApprovalQueue queue,
        string? oldValueSummary,
        string? newValueSummary,
        string reason,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        await adminAuditLogService.WriteAsync(
            new AdminAuditLogWriteRequest(
                actorUserId,
                actionType,
                "ApprovalQueue",
                queue.ApprovalReference,
                Truncate(oldValueSummary, 2048),
                Truncate(newValueSummary, 2048),
                Truncate(reason, 512) ?? "Approval queue event",
                IpAddress: null,
                UserAgent: null,
                correlationId),
            cancellationToken);
    }

    private async Task CompleteCommandAsync(
        ApprovalQueue queue,
        AdminCommandStatus status,
        string resultSummary,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(queue.CommandId))
        {
            return;
        }

        try
        {
            await adminCommandRegistry.CompleteAsync(
                new AdminCommandCompletionRequest(
                    queue.CommandId,
                    queue.PayloadHash,
                    status,
                    Truncate(resultSummary, 512),
                    correlationId),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Approval queue {ApprovalReference} could not complete command {CommandId}.", queue.ApprovalReference, queue.CommandId);
        }
    }

    private async Task ExecuteApprovedOperationAsync(ApprovalQueue queue, string actorUserId, CancellationToken cancellationToken)
    {
        switch (queue.OperationType)
        {
            case ApprovalQueueOperationType.GlobalSystemStateUpdate:
            {
                var payload = Deserialize<GlobalSystemStateSetRequest>(queue.PayloadJson)
                    ?? throw new InvalidOperationException("Global system state payload could not be parsed.");

                await globalSystemStateService.SetStateAsync(
                    payload with
                    {
                        UpdatedByUserId = actorUserId,
                        CommandId = queue.CommandId,
                        ApprovalReference = queue.ApprovalReference,
                        IncidentReference = queue.IncidentReference,
                        DependencyCircuitBreakerStateReference = queue.DependencyCircuitBreakerStateReference,
                        ChangeSummary = queue.Reason
                    },
                    cancellationToken);
                return;
            }
            case ApprovalQueueOperationType.GlobalPolicyUpdate:
            {
                var payload = Deserialize<GlobalPolicyUpdateRequest>(queue.PayloadJson)
                    ?? throw new InvalidOperationException("Global policy update payload could not be parsed.");

                await globalPolicyEngine.UpdateAsync(
                    payload with
                    {
                        ActorUserId = actorUserId,
                        CorrelationId = queue.CorrelationId ?? payload.CorrelationId,
                        Source = "AdminApproval.Queue"
                    },
                    cancellationToken);
                return;
            }
            case ApprovalQueueOperationType.GlobalPolicyRollback:
            {
                var payload = Deserialize<GlobalPolicyRollbackRequest>(queue.PayloadJson)
                    ?? throw new InvalidOperationException("Global policy rollback payload could not be parsed.");

                await globalPolicyEngine.RollbackAsync(
                    payload with
                    {
                        ActorUserId = actorUserId,
                        CorrelationId = queue.CorrelationId ?? payload.CorrelationId,
                        Source = "AdminApproval.Queue"
                    },
                    cancellationToken);
                return;
            }
            case ApprovalQueueOperationType.CrisisEscalationExecute:
                throw new NotSupportedException("Crisis escalation execution is handled by the dedicated crisis workflow.");
            default:
                throw new InvalidOperationException($"Unsupported approval queue operation '{queue.OperationType}'.");
        }
    }

    private async Task<LinkableState?> LinkSystemStateHistoryAsync(
        ApprovalQueue queue,
        string actorUserId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (queue.OperationType != ApprovalQueueOperationType.GlobalSystemStateUpdate)
        {
            return null;
        }

        var history = await dbContext.SystemStateHistories
            .AsNoTracking()
            .Where(entity =>
                entity.GlobalSystemStateId == GlobalSystemStateDefaults.SingletonId &&
                entity.CommandId == queue.CommandId &&
                entity.CorrelationId == (correlationId ?? queue.CorrelationId))
            .OrderByDescending(entity => entity.Version)
            .FirstOrDefaultAsync(cancellationToken);

        if (history is null)
        {
            return null;
        }

        queue.SystemStateHistoryId = history.Id;
        queue.SystemStateHistoryReference = history.HistoryReference;

        var incident = await LoadIncidentAsync(queue, cancellationToken);
        incident.SystemStateHistoryId = history.Id;
        incident.SystemStateHistoryReference = history.HistoryReference;

        dbContext.IncidentEvents.Add(new IncidentEvent
        {
            Id = Guid.NewGuid(),
            IncidentId = incident.Id,
            IncidentReference = incident.IncidentReference,
            EventType = IncidentEventType.StateLinked,
            Message = $"System state history {history.HistoryReference} linked.",
            ActorUserId = actorUserId,
            CorrelationId = correlationId ?? queue.CorrelationId,
            CommandId = queue.CommandId,
            ApprovalReference = queue.ApprovalReference,
            SystemStateHistoryReference = history.HistoryReference,
            PayloadJson = JsonSerializer.Serialize(history, JsonOptions)
        });

        return new LinkableState(history.Id, history.HistoryReference);
    }

    private async Task FinalizeOutcomeAsync(
        ApprovalQueue queue,
        string actorUserId,
        string? summary,
        string? correlationId,
        bool isSuccess,
        CancellationToken cancellationToken)
    {
        if (isSuccess)
        {
            await LinkSystemStateHistoryAsync(queue, actorUserId, correlationId, cancellationToken);
        }

        var incident = await LoadIncidentAsync(queue, cancellationToken);
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var normalizedSummary = Truncate(summary, 2048) ?? queue.Summary;

        queue.Status = isSuccess ? ApprovalQueueStatus.Executed : ApprovalQueueStatus.Failed;
        queue.ExecutionSummary = normalizedSummary;
        queue.ExecutedAtUtc = isSuccess ? utcNow : queue.ExecutedAtUtc;
        queue.LastActorUserId = actorUserId;

        incident.Status = isSuccess ? IncidentStatus.Resolved : IncidentStatus.Failed;
        incident.ResolvedAtUtc = utcNow;
        incident.ResolvedByUserId = actorUserId;
        incident.ResolvedSummary = normalizedSummary;

        dbContext.IncidentEvents.Add(new IncidentEvent
        {
            Id = Guid.NewGuid(),
            IncidentId = incident.Id,
            IncidentReference = incident.IncidentReference,
            EventType = isSuccess ? IncidentEventType.ApprovalExecuted : IncidentEventType.ExecutionFailed,
            Message = normalizedSummary,
            ActorUserId = actorUserId,
            CorrelationId = correlationId ?? queue.CorrelationId,
            CommandId = queue.CommandId,
            DecisionId = queue.DecisionId,
            ExecutionAttemptId = queue.ExecutionAttemptId,
            ApprovalReference = queue.ApprovalReference,
            SystemStateHistoryReference = queue.SystemStateHistoryReference,
            DependencyCircuitBreakerStateReference = queue.DependencyCircuitBreakerStateReference,
            PayloadJson = queue.PayloadJson
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await CompleteCommandAsync(queue, isSuccess ? AdminCommandStatus.Completed : AdminCommandStatus.Failed, normalizedSummary, correlationId ?? queue.CorrelationId, cancellationToken);
        await WriteAuditAsync(
            actorUserId,
            isSuccess ? "Admin.Approval.Queue.Executed" : "Admin.Approval.Queue.Failed",
            queue,
            null,
            BuildQueueSummary(queue),
            normalizedSummary,
            correlationId ?? queue.CorrelationId,
            cancellationToken);
    }

    private async Task<ApprovalQueueDetailSnapshot> BuildDetailAsync(ApprovalQueue queue, CancellationToken cancellationToken)
    {
        var actions = await dbContext.ApprovalActions
            .AsNoTracking()
            .Where(entity => entity.ApprovalQueueId == queue.Id)
            .OrderBy(entity => entity.Sequence)
            .ToListAsync(cancellationToken);

        return new ApprovalQueueDetailSnapshot(
            queue.ApprovalReference,
            queue.OperationType,
            queue.Status,
            queue.Severity,
            queue.Title,
            queue.Summary,
            queue.TargetType,
            queue.TargetId,
            queue.RequestedByUserId,
            queue.Reason,
            queue.PayloadJson,
            queue.RequiredApprovals,
            queue.ApprovalCount,
            queue.ExpiresAtUtc,
            queue.CorrelationId,
            queue.CommandId,
            queue.DecisionId,
            queue.ExecutionAttemptId,
            queue.IncidentReference,
            queue.SystemStateHistoryReference,
            queue.DependencyCircuitBreakerStateReference,
            queue.CreatedDate,
            queue.UpdatedDate,
            queue.ApprovedAtUtc,
            queue.ExecutedAtUtc,
            queue.RejectedAtUtc,
            queue.ExpiredAtUtc,
            queue.RejectReason,
            queue.ExecutionSummary,
            actions.Select(MapAction).ToArray());
    }

    private sealed record LinkableState(Guid Id, string Reference);

    private sealed record NormalizedEnqueueRequest(
        ApprovalQueueOperationType OperationType,
        IncidentSeverity Severity,
        string Title,
        string Summary,
        string RequestedByUserId,
        string Reason,
        string PayloadJson,
        int RequiredApprovals,
        DateTime ExpiresAtUtc,
        string? TargetType,
        string? TargetId,
        string? CorrelationId,
        string? CommandId,
        string? DecisionId,
        string? ExecutionAttemptId,
        string? ApprovalReference,
        string? IncidentReference,
        string? SystemStateHistoryReference,
        string? DependencyCircuitBreakerStateReference);
}
