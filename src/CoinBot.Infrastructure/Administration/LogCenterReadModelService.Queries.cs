using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;

namespace CoinBot.Infrastructure.Administration;

public sealed partial class LogCenterReadModelService
{
    private static IQueryable<DecisionTrace> ApplyDecisionFilters(
        IQueryable<DecisionTrace> query,
        NormalizedLogCenterQuery filters)
    {
        if (filters.FromUtc is not null)
        {
            query = query.Where(entity => entity.CreatedAtUtc >= filters.FromUtc);
        }

        if (filters.ToUtc is not null)
        {
            query = query.Where(entity => entity.CreatedAtUtc <= filters.ToUtc);
        }

        if (!string.IsNullOrWhiteSpace(filters.CorrelationId))
        {
            query = query.Where(entity => entity.CorrelationId == filters.CorrelationId);
        }

        if (!string.IsNullOrWhiteSpace(filters.DecisionId))
        {
            query = query.Where(entity => entity.DecisionId == filters.DecisionId);
        }

        if (!string.IsNullOrWhiteSpace(filters.UserId))
        {
            query = query.Where(entity => entity.UserId == filters.UserId);
        }

        if (!string.IsNullOrWhiteSpace(filters.Symbol))
        {
            query = query.Where(entity => entity.Symbol.ToLower() == filters.SymbolLower);
        }

        if (!string.IsNullOrWhiteSpace(filters.Status))
        {
            query = query.Where(entity =>
                entity.DecisionOutcome.ToLower() == filters.StatusLower ||
                (entity.DecisionReasonType != null && entity.DecisionReasonType.ToLower() == filters.StatusLower) ||
                (entity.DecisionReasonCode != null && entity.DecisionReasonCode.ToLower() == filters.StatusLower) ||
                (entity.ContinuityState != null && entity.ContinuityState.ToLower() == filters.StatusLower) ||
                (entity.StaleReason != null && entity.StaleReason.ToLower() == filters.StatusLower) ||
                (entity.VetoReasonCode != null && entity.VetoReasonCode.ToLower() == filters.StatusLower));
        }

        if (!string.IsNullOrWhiteSpace(filters.QueryLower))
        {
            query = query.Where(entity =>
                entity.CorrelationId.ToLower().Contains(filters.QueryLower) ||
                entity.DecisionId.ToLower().Contains(filters.QueryLower) ||
                entity.UserId.ToLower().Contains(filters.QueryLower) ||
                entity.Symbol.ToLower().Contains(filters.QueryLower) ||
                entity.Timeframe.ToLower().Contains(filters.QueryLower) ||
                entity.StrategyVersion.ToLower().Contains(filters.QueryLower) ||
                entity.SignalType.ToLower().Contains(filters.QueryLower) ||
                entity.DecisionOutcome.ToLower().Contains(filters.QueryLower) ||
                (entity.DecisionReasonType != null && entity.DecisionReasonType.ToLower().Contains(filters.QueryLower)) ||
                (entity.DecisionReasonCode != null && entity.DecisionReasonCode.ToLower().Contains(filters.QueryLower)) ||
                (entity.DecisionSummary != null && entity.DecisionSummary.ToLower().Contains(filters.QueryLower)) ||
                (entity.ContinuityState != null && entity.ContinuityState.ToLower().Contains(filters.QueryLower)) ||
                (entity.StaleReason != null && entity.StaleReason.ToLower().Contains(filters.QueryLower)) ||
                (entity.VetoReasonCode != null && entity.VetoReasonCode.ToLower().Contains(filters.QueryLower)));
        }

        return query;
    }

    private static IQueryable<ExecutionTrace> ApplyExecutionFilters(
        IQueryable<ExecutionTrace> query,
        NormalizedLogCenterQuery filters)
    {
        if (filters.FromUtc is not null)
        {
            query = query.Where(entity => entity.CreatedAtUtc >= filters.FromUtc);
        }

        if (filters.ToUtc is not null)
        {
            query = query.Where(entity => entity.CreatedAtUtc <= filters.ToUtc);
        }

        if (!string.IsNullOrWhiteSpace(filters.CorrelationId))
        {
            query = query.Where(entity => entity.CorrelationId == filters.CorrelationId);
        }

        if (!string.IsNullOrWhiteSpace(filters.ExecutionAttemptId))
        {
            query = query.Where(entity => entity.ExecutionAttemptId == filters.ExecutionAttemptId);
        }

        if (!string.IsNullOrWhiteSpace(filters.UserId))
        {
            query = query.Where(entity => entity.UserId == filters.UserId);
        }

        if (!string.IsNullOrWhiteSpace(filters.Symbol))
        {
            query = query.Where(entity =>
                entity.Endpoint.ToLower().Contains(filters.SymbolLower!) ||
                (entity.RequestMasked != null && entity.RequestMasked.ToLower().Contains(filters.SymbolLower!)) ||
                (entity.ResponseMasked != null && entity.ResponseMasked.ToLower().Contains(filters.SymbolLower!)));
        }

        if (!string.IsNullOrWhiteSpace(filters.Status))
        {
            if (int.TryParse(filters.Status, out var httpStatusCode))
            {
                query = query.Where(entity => entity.HttpStatusCode == httpStatusCode);
            }
            else if (string.Equals(filters.StatusLower, "success", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(filters.StatusLower, "healthy", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(entity =>
                    entity.HttpStatusCode.HasValue &&
                    entity.HttpStatusCode >= 200 &&
                    entity.HttpStatusCode < 300);
            }
            else if (string.Equals(filters.StatusLower, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(filters.StatusLower, "critical", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(filters.StatusLower, "error", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(entity =>
                    !entity.HttpStatusCode.HasValue ||
                    entity.HttpStatusCode < 200 ||
                    entity.HttpStatusCode >= 300);
            }
        }

        if (!string.IsNullOrWhiteSpace(filters.QueryLower))
        {
            query = query.Where(entity =>
                entity.CorrelationId.ToLower().Contains(filters.QueryLower) ||
                entity.ExecutionAttemptId.ToLower().Contains(filters.QueryLower) ||
                entity.CommandId.ToLower().Contains(filters.QueryLower) ||
                entity.UserId.ToLower().Contains(filters.QueryLower) ||
                entity.Provider.ToLower().Contains(filters.QueryLower) ||
                entity.Endpoint.ToLower().Contains(filters.QueryLower) ||
                (entity.ExchangeCode != null && entity.ExchangeCode.ToLower().Contains(filters.QueryLower)));
        }

        return query;
    }

    private static IQueryable<AdminAuditLog> ApplyAdminAuditFilters(
        IQueryable<AdminAuditLog> query,
        NormalizedLogCenterQuery filters)
    {
        if (filters.FromUtc is not null)
        {
            query = query.Where(entity => entity.CreatedAtUtc >= filters.FromUtc);
        }

        if (filters.ToUtc is not null)
        {
            query = query.Where(entity => entity.CreatedAtUtc <= filters.ToUtc);
        }

        if (!string.IsNullOrWhiteSpace(filters.CorrelationId))
        {
            query = query.Where(entity => entity.CorrelationId == filters.CorrelationId);
        }

        if (!string.IsNullOrWhiteSpace(filters.UserId))
        {
            query = query.Where(entity => entity.ActorUserId == filters.UserId);
        }

        if (!string.IsNullOrWhiteSpace(filters.DecisionId))
        {
            query = query.Where(entity =>
                (entity.TargetId != null && entity.TargetId.ToLower().Contains(filters.DecisionIdLower!)) ||
                (entity.OldValueSummary != null && entity.OldValueSummary.ToLower().Contains(filters.DecisionIdLower!)) ||
                (entity.NewValueSummary != null && entity.NewValueSummary.ToLower().Contains(filters.DecisionIdLower!)) ||
                entity.Reason.ToLower().Contains(filters.DecisionIdLower!));
        }

        if (!string.IsNullOrWhiteSpace(filters.ExecutionAttemptId))
        {
            query = query.Where(entity =>
                (entity.TargetId != null && entity.TargetId.ToLower().Contains(filters.ExecutionAttemptIdLower!)) ||
                (entity.OldValueSummary != null && entity.OldValueSummary.ToLower().Contains(filters.ExecutionAttemptIdLower!)) ||
                (entity.NewValueSummary != null && entity.NewValueSummary.ToLower().Contains(filters.ExecutionAttemptIdLower!)) ||
                entity.Reason.ToLower().Contains(filters.ExecutionAttemptIdLower!));
        }

        if (!string.IsNullOrWhiteSpace(filters.Symbol))
        {
            query = query.Where(entity =>
                entity.TargetType.ToLower().Contains(filters.SymbolLower!) ||
                (entity.TargetId != null && entity.TargetId.ToLower().Contains(filters.SymbolLower!)) ||
                (entity.OldValueSummary != null && entity.OldValueSummary.ToLower().Contains(filters.SymbolLower!)) ||
                (entity.NewValueSummary != null && entity.NewValueSummary.ToLower().Contains(filters.SymbolLower!)) ||
                entity.Reason.ToLower().Contains(filters.SymbolLower!));
        }

        if (!string.IsNullOrWhiteSpace(filters.Status))
        {
            query = query.Where(entity => entity.ActionType.ToLower() == filters.StatusLower);
        }

        if (!string.IsNullOrWhiteSpace(filters.QueryLower))
        {
            query = query.Where(entity =>
                entity.ActorUserId.ToLower().Contains(filters.QueryLower) ||
                entity.ActionType.ToLower().Contains(filters.QueryLower) ||
                entity.TargetType.ToLower().Contains(filters.QueryLower) ||
                (entity.TargetId != null && entity.TargetId.ToLower().Contains(filters.QueryLower)) ||
                entity.Reason.ToLower().Contains(filters.QueryLower) ||
                (entity.CorrelationId != null && entity.CorrelationId.ToLower().Contains(filters.QueryLower)));
        }

        return query;
    }

    private static IQueryable<Incident> ApplyIncidentFilters(
        IQueryable<Incident> query,
        NormalizedLogCenterQuery filters)
    {
        if (filters.FromUtc is not null)
        {
            query = query.Where(entity => entity.CreatedDate >= filters.FromUtc);
        }

        if (filters.ToUtc is not null)
        {
            query = query.Where(entity => entity.CreatedDate <= filters.ToUtc);
        }

        if (!string.IsNullOrWhiteSpace(filters.CorrelationId))
        {
            query = query.Where(entity => entity.CorrelationId == filters.CorrelationId);
        }

        if (!string.IsNullOrWhiteSpace(filters.UserId))
        {
            query = query.Where(entity =>
                entity.CreatedByUserId == filters.UserId ||
                entity.ResolvedByUserId == filters.UserId);
        }

        if (!string.IsNullOrWhiteSpace(filters.Symbol))
        {
            query = query.Where(entity =>
                entity.Title.ToLower().Contains(filters.SymbolLower!) ||
                entity.Summary.ToLower().Contains(filters.SymbolLower!) ||
                entity.Detail.ToLower().Contains(filters.SymbolLower!) ||
                (entity.TargetId != null && entity.TargetId.ToLower().Contains(filters.SymbolLower!)));
        }

        if (!string.IsNullOrWhiteSpace(filters.Status))
        {
            query = query.Where(entity => entity.Status.ToString().ToLower() == filters.StatusLower);
        }

        if (!string.IsNullOrWhiteSpace(filters.QueryLower))
        {
            query = query.Where(entity =>
                entity.IncidentReference.ToLower().Contains(filters.QueryLower) ||
                entity.Title.ToLower().Contains(filters.QueryLower) ||
                entity.Summary.ToLower().Contains(filters.QueryLower) ||
                entity.Detail.ToLower().Contains(filters.QueryLower) ||
                (entity.TargetType != null && entity.TargetType.ToLower().Contains(filters.QueryLower)) ||
                (entity.TargetId != null && entity.TargetId.ToLower().Contains(filters.QueryLower)) ||
                (entity.CorrelationId != null && entity.CorrelationId.ToLower().Contains(filters.QueryLower)) ||
                (entity.CommandId != null && entity.CommandId.ToLower().Contains(filters.QueryLower)) ||
                (entity.DecisionId != null && entity.DecisionId.ToLower().Contains(filters.QueryLower)) ||
                (entity.ExecutionAttemptId != null && entity.ExecutionAttemptId.ToLower().Contains(filters.QueryLower)));
        }

        return query;
    }

    private static IQueryable<IncidentEvent> ApplyIncidentEventFilters(
        IQueryable<IncidentEvent> query,
        NormalizedLogCenterQuery filters)
    {
        if (filters.FromUtc is not null)
        {
            query = query.Where(entity => entity.CreatedDate >= filters.FromUtc);
        }

        if (filters.ToUtc is not null)
        {
            query = query.Where(entity => entity.CreatedDate <= filters.ToUtc);
        }

        if (!string.IsNullOrWhiteSpace(filters.CorrelationId))
        {
            query = query.Where(entity => entity.CorrelationId == filters.CorrelationId);
        }

        if (!string.IsNullOrWhiteSpace(filters.UserId))
        {
            query = query.Where(entity => entity.ActorUserId == filters.UserId);
        }

        if (!string.IsNullOrWhiteSpace(filters.Symbol))
        {
            query = query.Where(entity =>
                entity.Message.ToLower().Contains(filters.SymbolLower!) ||
                (entity.PayloadJson != null && entity.PayloadJson.ToLower().Contains(filters.SymbolLower!)));
        }

        if (!string.IsNullOrWhiteSpace(filters.Status))
        {
            query = query.Where(entity => entity.EventType.ToString().ToLower() == filters.StatusLower);
        }

        if (!string.IsNullOrWhiteSpace(filters.QueryLower))
        {
            query = query.Where(entity =>
                entity.IncidentReference.ToLower().Contains(filters.QueryLower) ||
                entity.EventType.ToString().ToLower().Contains(filters.QueryLower) ||
                entity.Message.ToLower().Contains(filters.QueryLower) ||
                (entity.ActorUserId != null && entity.ActorUserId.ToLower().Contains(filters.QueryLower)) ||
                (entity.CorrelationId != null && entity.CorrelationId.ToLower().Contains(filters.QueryLower)) ||
                (entity.CommandId != null && entity.CommandId.ToLower().Contains(filters.QueryLower)) ||
                (entity.DecisionId != null && entity.DecisionId.ToLower().Contains(filters.QueryLower)) ||
                (entity.ExecutionAttemptId != null && entity.ExecutionAttemptId.ToLower().Contains(filters.QueryLower)) ||
                (entity.ApprovalReference != null && entity.ApprovalReference.ToLower().Contains(filters.QueryLower)));
        }

        return query;
    }

    private static IQueryable<ApprovalQueue> ApplyApprovalQueueFilters(
        IQueryable<ApprovalQueue> query,
        NormalizedLogCenterQuery filters)
    {
        if (filters.FromUtc is not null)
        {
            query = query.Where(entity => entity.CreatedDate >= filters.FromUtc);
        }

        if (filters.ToUtc is not null)
        {
            query = query.Where(entity => entity.CreatedDate <= filters.ToUtc);
        }

        if (!string.IsNullOrWhiteSpace(filters.CorrelationId))
        {
            query = query.Where(entity => entity.CorrelationId == filters.CorrelationId);
        }

        if (!string.IsNullOrWhiteSpace(filters.UserId))
        {
            query = query.Where(entity => entity.RequestedByUserId == filters.UserId || entity.LastActorUserId == filters.UserId);
        }

        if (!string.IsNullOrWhiteSpace(filters.Symbol))
        {
            query = query.Where(entity =>
                entity.Title.ToLower().Contains(filters.SymbolLower!) ||
                entity.Summary.ToLower().Contains(filters.SymbolLower!) ||
                entity.Reason.ToLower().Contains(filters.SymbolLower!) ||
                (entity.TargetId != null && entity.TargetId.ToLower().Contains(filters.SymbolLower!)) ||
                entity.PayloadJson.ToLower().Contains(filters.SymbolLower!) ||
                (entity.RejectReason != null && entity.RejectReason.ToLower().Contains(filters.SymbolLower!)) ||
                (entity.ExecutionSummary != null && entity.ExecutionSummary.ToLower().Contains(filters.SymbolLower!)));
        }

        if (!string.IsNullOrWhiteSpace(filters.Status))
        {
            query = query.Where(entity => entity.Status.ToString().ToLower() == filters.StatusLower);
        }

        if (!string.IsNullOrWhiteSpace(filters.QueryLower))
        {
            query = query.Where(entity =>
                entity.ApprovalReference.ToLower().Contains(filters.QueryLower) ||
                entity.Title.ToLower().Contains(filters.QueryLower) ||
                entity.Summary.ToLower().Contains(filters.QueryLower) ||
                entity.RequestedByUserId.ToLower().Contains(filters.QueryLower) ||
                (entity.TargetType != null && entity.TargetType.ToLower().Contains(filters.QueryLower)) ||
                (entity.TargetId != null && entity.TargetId.ToLower().Contains(filters.QueryLower)) ||
                (entity.CorrelationId != null && entity.CorrelationId.ToLower().Contains(filters.QueryLower)) ||
                (entity.CommandId != null && entity.CommandId.ToLower().Contains(filters.QueryLower)) ||
                (entity.DecisionId != null && entity.DecisionId.ToLower().Contains(filters.QueryLower)) ||
                (entity.ExecutionAttemptId != null && entity.ExecutionAttemptId.ToLower().Contains(filters.QueryLower)));
        }

        return query;
    }

    private static IQueryable<ApprovalAction> ApplyApprovalActionFilters(
        IQueryable<ApprovalAction> query,
        NormalizedLogCenterQuery filters)
    {
        if (filters.FromUtc is not null)
        {
            query = query.Where(entity => entity.CreatedDate >= filters.FromUtc);
        }

        if (filters.ToUtc is not null)
        {
            query = query.Where(entity => entity.CreatedDate <= filters.ToUtc);
        }

        if (!string.IsNullOrWhiteSpace(filters.CorrelationId))
        {
            query = query.Where(entity => entity.CorrelationId == filters.CorrelationId);
        }

        if (!string.IsNullOrWhiteSpace(filters.UserId))
        {
            query = query.Where(entity => entity.ActorUserId == filters.UserId);
        }

        if (!string.IsNullOrWhiteSpace(filters.Symbol))
        {
            query = query.Where(entity =>
                (entity.Reason != null && entity.Reason.ToLower().Contains(filters.SymbolLower!)) ||
                (entity.IncidentReference != null && entity.IncidentReference.ToLower().Contains(filters.SymbolLower!)));
        }

        if (!string.IsNullOrWhiteSpace(filters.Status))
        {
            query = query.Where(entity => entity.ActionType.ToString().ToLower() == filters.StatusLower);
        }

        if (!string.IsNullOrWhiteSpace(filters.QueryLower))
        {
            query = query.Where(entity =>
                entity.ApprovalReference.ToLower().Contains(filters.QueryLower) ||
                entity.ActionType.ToString().ToLower().Contains(filters.QueryLower) ||
                entity.ActorUserId.ToLower().Contains(filters.QueryLower) ||
                (entity.Reason != null && entity.Reason.ToLower().Contains(filters.QueryLower)) ||
                (entity.CorrelationId != null && entity.CorrelationId.ToLower().Contains(filters.QueryLower)) ||
                (entity.CommandId != null && entity.CommandId.ToLower().Contains(filters.QueryLower)) ||
                (entity.DecisionId != null && entity.DecisionId.ToLower().Contains(filters.QueryLower)) ||
                (entity.ExecutionAttemptId != null && entity.ExecutionAttemptId.ToLower().Contains(filters.QueryLower)));
        }

        return query;
    }
}
