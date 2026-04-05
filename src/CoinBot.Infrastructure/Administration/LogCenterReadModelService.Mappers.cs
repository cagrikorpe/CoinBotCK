using System.Text.Json;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;

namespace CoinBot.Infrastructure.Administration;

public sealed partial class LogCenterReadModelService
{
    private static LogCenterEntrySnapshot MapDecisionTrace(DecisionTrace entity)
    {
        var tone = ResolveDecisionTone(entity);

        return new LogCenterEntrySnapshot(
            Kind: "DecisionTrace",
            Reference: Truncate(entity.DecisionId, 128),
            Status: Truncate(entity.DecisionOutcome, 64),
            Tone: tone,
            Severity: ResolveSeverity(tone),
            CorrelationId: entity.CorrelationId,
            DecisionId: entity.DecisionId,
            ExecutionAttemptId: null,
            IncidentReference: null,
            ApprovalReference: null,
            UserId: entity.UserId,
            Symbol: entity.Symbol,
            Title: SafeText($"{entity.Symbol} · {entity.Timeframe} decision", 256),
            Summary: SafeText(BuildDecisionSummary(entity), 512),
            Source: SafeText(entity.StrategyVersion, 128),
            CreatedAtUtc: entity.CreatedAtUtc,
            Tags: BuildTags(
                entity.SignalType,
                entity.Timeframe,
                entity.StrategyVersion,
                entity.DecisionReasonType,
                entity.DecisionReasonCode,
                entity.ContinuityState,
                entity.StaleReason,
                entity.VetoReasonCode,
                entity.RiskScore is null ? null : $"Risk={entity.RiskScore}"),
            RawJson: BuildRawJson(new
            {
                Kind = "DecisionTrace",
                entity.CorrelationId,
                entity.DecisionId,
                entity.UserId,
                entity.Symbol,
                entity.Timeframe,
                entity.StrategyVersion,
                entity.SignalType,
                entity.RiskScore,
                entity.DecisionOutcome,
                entity.DecisionReasonType,
                entity.DecisionReasonCode,
                entity.DecisionSummary,
                entity.DecisionAtUtc,
                entity.VetoReasonCode,
                entity.LatencyMs,
                entity.LastCandleAtUtc,
                entity.DataAgeMs,
                entity.StaleThresholdMs,
                entity.StaleReason,
                entity.ContinuityState,
                entity.ContinuityGapCount,
                entity.ContinuityGapStartedAtUtc,
                entity.ContinuityGapLastSeenAtUtc,
                entity.ContinuityRecoveredAtUtc,
                entity.CreatedAtUtc,
                entity.SnapshotJson
            }),
            DecisionReasonType: entity.DecisionReasonType,
            DecisionReasonCode: entity.DecisionReasonCode,
            DecisionSummary: entity.DecisionSummary,
            DecisionAtUtc: entity.DecisionAtUtc,
            LastCandleAtUtc: entity.LastCandleAtUtc,
            DataAgeMs: entity.DataAgeMs,
            StaleThresholdMs: entity.StaleThresholdMs,
            StaleReason: entity.StaleReason,
            ContinuityState: entity.ContinuityState,
            ContinuityGapCount: entity.ContinuityGapCount,
            ContinuityGapStartedAtUtc: entity.ContinuityGapStartedAtUtc,
            ContinuityGapLastSeenAtUtc: entity.ContinuityGapLastSeenAtUtc,
            ContinuityRecoveredAtUtc: entity.ContinuityRecoveredAtUtc);
    }

    private static LogCenterEntrySnapshot MapExecutionTrace(ExecutionTrace entity)
    {
        var tone = ResolveExecutionTone(entity);
        var status = entity.HttpStatusCode.HasValue
            ? $"HTTP {entity.HttpStatusCode.Value}"
            : "Pending";

        return new LogCenterEntrySnapshot(
            Kind: "ExecutionTrace",
            Reference: Truncate(entity.ExecutionAttemptId, 128),
            Status: status,
            Tone: tone,
            Severity: ResolveSeverity(tone),
            CorrelationId: entity.CorrelationId,
            DecisionId: null,
            ExecutionAttemptId: entity.ExecutionAttemptId,
            IncidentReference: null,
            ApprovalReference: null,
            UserId: entity.UserId,
            Symbol: null,
            Title: SafeText($"{entity.Provider} · execution attempt", 256),
            Summary: SafeText(BuildExecutionSummary(entity), 512),
            Source: SafeText(entity.Endpoint, 128),
            CreatedAtUtc: entity.CreatedAtUtc,
            Tags: BuildTags(
                entity.Provider,
                entity.ExchangeCode,
                entity.HttpStatusCode is null ? null : $"HTTP={entity.HttpStatusCode}",
                entity.LatencyMs is null ? null : $"Latency={entity.LatencyMs}ms"),
            RawJson: BuildRawJson(new
            {
                Kind = "ExecutionTrace",
                entity.CorrelationId,
                entity.ExecutionAttemptId,
                entity.CommandId,
                entity.UserId,
                entity.Provider,
                entity.Endpoint,
                entity.HttpStatusCode,
                entity.ExchangeCode,
                entity.LatencyMs,
                entity.CreatedAtUtc,
                entity.RequestMasked,
                entity.ResponseMasked
            }));
    }

    private static LogCenterEntrySnapshot MapAdminAuditLog(AdminAuditLog entity)
    {
        var tone = ResolveAdminAuditTone(entity.ActionType);
        var reference = $"{entity.ActionType}:{entity.TargetType}:{entity.TargetId ?? entity.CorrelationId ?? entity.Id.ToString("N")}:{entity.Id:N}";

        return new LogCenterEntrySnapshot(
            Kind: "AdminAuditLog",
            Reference: Truncate(reference, 128),
            Status: Truncate(entity.ActionType, 64),
            Tone: tone,
            Severity: ResolveSeverity(tone),
            CorrelationId: entity.CorrelationId,
            DecisionId: null,
            ExecutionAttemptId: null,
            IncidentReference: null,
            ApprovalReference: null,
            UserId: entity.ActorUserId,
            Symbol: null,
            Title: SafeText($"{entity.ActionType} · {entity.TargetType}", 256),
            Summary: SafeText(BuildAdminAuditSummary(entity), 512),
            Source: SafeText(entity.TargetId ?? entity.TargetType, 128),
            CreatedAtUtc: entity.CreatedAtUtc,
            Tags: BuildTags(
                entity.TargetType,
                entity.TargetId,
                entity.ActionType,
                entity.CorrelationId),
            RawJson: BuildRawJson(new
            {
                Kind = "AdminAuditLog",
                entity.ActorUserId,
                entity.ActionType,
                entity.TargetType,
                entity.TargetId,
                entity.OldValueSummary,
                entity.NewValueSummary,
                entity.Reason,
                entity.CorrelationId,
                entity.CreatedAtUtc
            }));
    }

    private static LogCenterEntrySnapshot MapIncident(Incident entity)
    {
        var tone = ResolveIncidentTone(entity);

        return new LogCenterEntrySnapshot(
            Kind: "Incident",
            Reference: Truncate(entity.IncidentReference, 128),
            Status: Truncate(entity.Status.ToString(), 64),
            Tone: tone,
            Severity: entity.Severity.ToString(),
            CorrelationId: entity.CorrelationId,
            DecisionId: entity.DecisionId,
            ExecutionAttemptId: entity.ExecutionAttemptId,
            IncidentReference: entity.IncidentReference,
            ApprovalReference: entity.ApprovalReference,
            UserId: entity.CreatedByUserId ?? entity.ResolvedByUserId,
            Symbol: null,
            Title: SafeText(entity.Title, 256),
            Summary: SafeText(BuildIncidentSummary(entity), 512),
            Source: SafeText(entity.TargetType ?? entity.CreatedByUserId ?? "Incident", 128),
            CreatedAtUtc: entity.CreatedDate,
            Tags: BuildTags(
                entity.OperationType?.ToString(),
                entity.TargetType,
                entity.TargetId,
                entity.CommandId,
                entity.DecisionId,
                entity.ExecutionAttemptId,
                entity.ApprovalReference),
            RawJson: BuildRawJson(new
            {
                Kind = "Incident",
                entity.IncidentReference,
                entity.Severity,
                entity.Status,
                entity.OperationType,
                entity.Title,
                entity.Summary,
                entity.Detail,
                entity.TargetType,
                entity.TargetId,
                entity.CorrelationId,
                entity.CommandId,
                entity.DecisionId,
                entity.ExecutionAttemptId,
                entity.ApprovalReference,
                entity.SystemStateHistoryReference,
                entity.DependencyCircuitBreakerStateReference,
                entity.CreatedByUserId,
                entity.ResolvedByUserId,
                entity.ResolvedSummary,
                entity.CreatedDate,
                entity.ResolvedAtUtc
            }));
    }

    private static LogCenterEntrySnapshot MapIncidentEvent(IncidentEvent entity)
    {
        var tone = ResolveIncidentEventTone(entity.EventType);

        return new LogCenterEntrySnapshot(
            Kind: "IncidentEvent",
            Reference: Truncate($"{entity.IncidentReference}:{entity.EventType}:{entity.Id:N}", 128),
            Status: Truncate(entity.EventType.ToString(), 64),
            Tone: tone,
            Severity: ResolveSeverity(tone),
            CorrelationId: entity.CorrelationId,
            DecisionId: entity.DecisionId,
            ExecutionAttemptId: entity.ExecutionAttemptId,
            IncidentReference: entity.IncidentReference,
            ApprovalReference: entity.ApprovalReference,
            UserId: entity.ActorUserId,
            Symbol: null,
            Title: SafeText($"{entity.IncidentReference} · {entity.EventType}", 256),
            Summary: SafeText(BuildIncidentEventSummary(entity), 512),
            Source: SafeText(entity.ActorUserId ?? entity.DependencyCircuitBreakerStateReference ?? entity.SystemStateHistoryReference ?? "IncidentEvent", 128),
            CreatedAtUtc: entity.CreatedDate,
            Tags: BuildTags(
                entity.EventType.ToString(),
                entity.CommandId,
                entity.DecisionId,
                entity.ExecutionAttemptId,
                entity.ApprovalReference),
            RawJson: BuildRawJson(new
            {
                Kind = "IncidentEvent",
                entity.IncidentReference,
                entity.EventType,
                entity.Message,
                entity.ActorUserId,
                entity.CorrelationId,
                entity.CommandId,
                entity.DecisionId,
                entity.ExecutionAttemptId,
                entity.ApprovalReference,
                entity.SystemStateHistoryReference,
                entity.DependencyCircuitBreakerStateReference,
                entity.PayloadJson,
                entity.CreatedDate
            }));
    }

    private static LogCenterEntrySnapshot MapApprovalQueue(ApprovalQueue entity)
    {
        var tone = ResolveApprovalQueueTone(entity.Status);

        return new LogCenterEntrySnapshot(
            Kind: "ApprovalQueue",
            Reference: Truncate(entity.ApprovalReference, 128),
            Status: Truncate(entity.Status.ToString(), 64),
            Tone: tone,
            Severity: entity.Severity.ToString(),
            CorrelationId: entity.CorrelationId,
            DecisionId: entity.DecisionId,
            ExecutionAttemptId: entity.ExecutionAttemptId,
            IncidentReference: entity.IncidentReference,
            ApprovalReference: entity.ApprovalReference,
            UserId: entity.RequestedByUserId,
            Symbol: null,
            Title: SafeText(entity.Title, 256),
            Summary: SafeText(BuildApprovalQueueSummary(entity), 512),
            Source: SafeText(entity.RequestedByUserId, 128),
            CreatedAtUtc: entity.CreatedDate,
            Tags: BuildTags(
                entity.OperationType.ToString(),
                entity.TargetType,
                entity.TargetId,
                entity.Reason,
                entity.RequiredApprovals is 0 ? null : $"Required={entity.RequiredApprovals}",
                entity.ApprovalCount is 0 ? null : $"Approved={entity.ApprovalCount}"),
            RawJson: BuildRawJson(new
            {
                Kind = "ApprovalQueue",
                entity.ApprovalReference,
                entity.OperationType,
                entity.Status,
                entity.Severity,
                entity.Title,
                entity.Summary,
                entity.TargetType,
                entity.TargetId,
                entity.RequestedByUserId,
                entity.RequiredApprovals,
                entity.ApprovalCount,
                entity.ExpiresAtUtc,
                entity.Reason,
                entity.CorrelationId,
                entity.CommandId,
                entity.DecisionId,
                entity.ExecutionAttemptId,
                entity.IncidentReference,
                entity.SystemStateHistoryReference,
                entity.DependencyCircuitBreakerStateReference,
                entity.ApprovedAtUtc,
                entity.ExecutedAtUtc,
                entity.RejectedAtUtc,
                entity.ExpiredAtUtc,
                entity.RejectReason,
                entity.ExecutionSummary,
                entity.LastActorUserId,
                entity.CreatedDate
            }));
    }

    private static LogCenterEntrySnapshot MapApprovalAction(ApprovalAction entity)
    {
        var tone = ResolveApprovalActionTone(entity.ActionType);

        return new LogCenterEntrySnapshot(
            Kind: "ApprovalAction",
            Reference: Truncate($"{entity.ApprovalReference}:{entity.Sequence}:{entity.Id:N}", 128),
            Status: Truncate(entity.ActionType.ToString(), 64),
            Tone: tone,
            Severity: ResolveSeverity(tone),
            CorrelationId: entity.CorrelationId,
            DecisionId: entity.DecisionId,
            ExecutionAttemptId: entity.ExecutionAttemptId,
            IncidentReference: entity.IncidentReference,
            ApprovalReference: entity.ApprovalReference,
            UserId: entity.ActorUserId,
            Symbol: null,
            Title: SafeText($"{entity.ApprovalReference} · action #{entity.Sequence}", 256),
            Summary: SafeText(BuildApprovalActionSummary(entity), 512),
            Source: SafeText(entity.ActorUserId, 128),
            CreatedAtUtc: entity.CreatedDate,
            Tags: BuildTags(
                entity.ActionType.ToString(),
                $"Sequence={entity.Sequence}",
                entity.CommandId,
                entity.DecisionId,
                entity.ExecutionAttemptId,
                entity.IncidentReference),
            RawJson: BuildRawJson(new
            {
                Kind = "ApprovalAction",
                entity.ApprovalReference,
                entity.ActionType,
                entity.Sequence,
                entity.ActorUserId,
                entity.Reason,
                entity.CorrelationId,
                entity.CommandId,
                entity.DecisionId,
                entity.ExecutionAttemptId,
                entity.IncidentReference,
                entity.SystemStateHistoryReference,
                entity.DependencyCircuitBreakerStateReference,
                entity.CreatedDate
            }));
    }

    private static string SafeText(string? value, int maxLength)
    {
        var masked = SensitivePayloadMasker.Mask(value, maxLength);
        return string.IsNullOrWhiteSpace(masked)
            ? string.Empty
            : masked;
    }

    private static string Truncate(string? value, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }

    private static string ResolveSeverity(string tone)
    {
        return tone switch
        {
            "critical" => "Critical",
            "warning" => "Warning",
            "degraded" => "Warning",
            "healthy" => "Info",
            "info" => "Info",
            _ => "Info"
        };
    }

    private static string ResolveDecisionTone(DecisionTrace entity)
    {
        if (!string.IsNullOrWhiteSpace(entity.VetoReasonCode))
        {
            return "critical";
        }

        if (string.Equals(entity.DecisionReasonType, "RiskVeto", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entity.DecisionReasonType, "GlobalExecutionOff", StringComparison.OrdinalIgnoreCase))
        {
            return "critical";
        }

        if (string.Equals(entity.DecisionReasonType, "StaleData", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entity.DecisionReasonType, "ContinuityGap", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entity.DecisionReasonType, "TradingModeMismatch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entity.DecisionReasonType, "MissingPrivatePlaneOrConfig", StringComparison.OrdinalIgnoreCase))
        {
            return "warning";
        }

        if (entity.DecisionOutcome.Contains("reject", StringComparison.OrdinalIgnoreCase) ||
            entity.DecisionOutcome.Contains("block", StringComparison.OrdinalIgnoreCase) ||
            entity.DecisionOutcome.Contains("veto", StringComparison.OrdinalIgnoreCase) ||
            entity.DecisionOutcome.Contains("deny", StringComparison.OrdinalIgnoreCase) ||
            entity.DecisionOutcome.Contains("fail", StringComparison.OrdinalIgnoreCase))
        {
            return "critical";
        }

        if (entity.RiskScore is >= 80)
        {
            return "warning";
        }

        return "healthy";
    }

    private static string ResolveExecutionTone(ExecutionTrace entity)
    {
        if (!entity.HttpStatusCode.HasValue)
        {
            return "degraded";
        }

        if (entity.HttpStatusCode >= 500)
        {
            return "critical";
        }

        if (entity.HttpStatusCode >= 400)
        {
            return "warning";
        }

        return "healthy";
    }

    private static string ResolveAdminAuditTone(string actionType)
    {
        if (actionType.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
            actionType.Contains("Rejected", StringComparison.OrdinalIgnoreCase) ||
            actionType.Contains("Expired", StringComparison.OrdinalIgnoreCase) ||
            actionType.Contains("Delete", StringComparison.OrdinalIgnoreCase) ||
            actionType.Contains("Rollback", StringComparison.OrdinalIgnoreCase))
        {
            return "critical";
        }

        if (actionType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
            actionType.Contains("Export", StringComparison.OrdinalIgnoreCase) ||
            actionType.Contains("Retention", StringComparison.OrdinalIgnoreCase))
        {
            return "warning";
        }

        return "info";
    }

    private static string ResolveIncidentTone(Incident entity)
    {
        return entity.Severity switch
        {
            IncidentSeverity.Critical => "critical",
            IncidentSeverity.Warning => entity.Status is IncidentStatus.Resolved ? "healthy" : "warning",
            _ => entity.Status is IncidentStatus.Resolved ? "healthy" : "info"
        };
    }

    private static string ResolveIncidentEventTone(IncidentEventType eventType)
    {
        return eventType switch
        {
            IncidentEventType.IncidentResolved => "healthy",
            IncidentEventType.IncidentEscalated => "warning",
            IncidentEventType.ApprovalRejected or IncidentEventType.ApprovalExpired or IncidentEventType.ExecutionFailed => "critical",
            IncidentEventType.ApprovalQueued or IncidentEventType.ApprovalRecorded or IncidentEventType.TraceLinked or IncidentEventType.StateLinked or IncidentEventType.BreakerLinked or IncidentEventType.RecoveryRecorded => "info",
            _ => "info"
        };
    }

    private static string ResolveApprovalQueueTone(ApprovalQueueStatus status)
    {
        return status switch
        {
            ApprovalQueueStatus.Pending => "warning",
            ApprovalQueueStatus.Approved or ApprovalQueueStatus.Executed => "healthy",
            ApprovalQueueStatus.Rejected or ApprovalQueueStatus.Expired or ApprovalQueueStatus.Failed => "critical",
            ApprovalQueueStatus.Cancelled => "info",
            _ => "info"
        };
    }

    private static string ResolveApprovalActionTone(ApprovalActionType actionType)
    {
        return actionType switch
        {
            ApprovalActionType.Approved => "healthy",
            ApprovalActionType.Rejected => "critical",
            ApprovalActionType.Expired => "warning",
            ApprovalActionType.Cancelled => "info",
            _ => "info"
        };
    }

    private static string BuildDecisionSummary(DecisionTrace entity)
    {
        var decisionAtLabel = entity.DecisionAtUtc.HasValue
            ? entity.DecisionAtUtc.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") + " UTC"
            : "n/a";

        return
            $"Outcome={entity.DecisionOutcome}; ReasonType={entity.DecisionReasonType ?? "n/a"}; ReasonCode={entity.DecisionReasonCode ?? entity.VetoReasonCode ?? "none"}; Summary={entity.DecisionSummary ?? "n/a"}; DecisionAt={decisionAtLabel}; StaleReason={entity.StaleReason ?? "none"}; ContinuityState={entity.ContinuityState ?? "n/a"}; GapCount={entity.ContinuityGapCount?.ToString() ?? "n/a"}; Latency={entity.LatencyMs}ms";
    }

    private static string BuildExecutionSummary(ExecutionTrace entity)
    {
        return
            $"Provider={entity.Provider}; Endpoint={entity.Endpoint}; Status={entity.HttpStatusCode?.ToString() ?? "pending"}; ExchangeCode={entity.ExchangeCode ?? "none"}; Latency={entity.LatencyMs?.ToString() ?? "n/a"}ms";
    }

    private static string BuildAdminAuditSummary(AdminAuditLog entity)
    {
        var primary = entity.NewValueSummary ?? entity.OldValueSummary ?? entity.Reason;
        return
            $"Actor={entity.ActorUserId}; Target={entity.TargetType}/{entity.TargetId ?? "n/a"}; Reason={entity.Reason}; Payload={primary}";
    }

    private static string BuildIncidentSummary(Incident entity)
    {
        var parts = new List<string>
        {
            $"Status={entity.Status}",
            $"Severity={entity.Severity}",
            $"Title={entity.Title}"
        };

        if (!string.IsNullOrWhiteSpace(entity.ResolvedSummary))
        {
            parts.Add($"Resolution={entity.ResolvedSummary}");
        }

        return string.Join("; ", parts);
    }

    private static string BuildIncidentEventSummary(IncidentEvent entity)
    {
        return
            $"Event={entity.EventType}; Actor={entity.ActorUserId ?? "system"}; Message={entity.Message}";
    }

    private static string BuildApprovalQueueSummary(ApprovalQueue entity)
    {
        return
            $"Operation={entity.OperationType}; Status={entity.Status}; RequiredApprovals={entity.RequiredApprovals}; ApprovalCount={entity.ApprovalCount}; ExpiresAtUtc={entity.ExpiresAtUtc:O}";
    }

    private static string BuildApprovalActionSummary(ApprovalAction entity)
    {
        return
            $"Action={entity.ActionType}; Sequence={entity.Sequence}; Reason={entity.Reason ?? "n/a"}";
    }

    private static IReadOnlyCollection<string> BuildTags(params string?[] values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Truncate(value, 64))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
    }

    private static string? BuildRawJson(object payload)
    {
        var json = JsonSerializer.Serialize(
            payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            });

        return SensitivePayloadMasker.Mask(json, 8192);
    }
}
