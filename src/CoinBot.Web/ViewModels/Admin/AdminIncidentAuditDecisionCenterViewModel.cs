using System.Text.Json;
using CoinBot.Application.Abstractions.Administration;

namespace CoinBot.Web.ViewModels.Admin;

public sealed record AdminIncidentAuditDecisionCenterViewModel(
    string RefreshedAtUtcLabel,
    bool HasError,
    string? ErrorMessage,
    AdminIncidentAuditDecisionFilterViewModel Filters,
    IReadOnlyCollection<AdminOperationsSummaryCardViewModel> SummaryCards,
    IReadOnlyCollection<AdminIncidentAuditDecisionRowViewModel> Rows,
    AdminIncidentAuditDecisionDetailViewModel Detail,
    string EmptyStateMessage,
    AdminUltraDebugLogViewModel? UltraDebugLog = null,
    AdminIncidentAuditDecisionPaginationViewModel? Pagination = null);

public sealed record AdminUltraDebugLogViewModel(
    bool IsEnabled,
    string StatusLabel,
    string StatusTone,
    string StatusMessage,
    string StartedByAdmin,
    string StartedAtUtcLabel,
    string ExpiresAtUtcLabel,
    string RemainingLabel,
    string DurationLabel,
    string NormalLogsLimitLabel,
    string UltraLogsLimitLabel,
    string NormalLogsUsageLabel,
    string UltraLogsUsageLabel,
    string DiskFreeSpaceLabel,
    string SafetyModeLabel,
    string AutoDisabledReasonCode,
    IReadOnlyCollection<AdminUltraDebugLogDurationOptionViewModel> DurationOptions,
    IReadOnlyCollection<AdminUltraDebugLogSizeLimitOptionViewModel> NormalLogsLimitOptions,
    IReadOnlyCollection<AdminUltraDebugLogSizeLimitOptionViewModel> UltraLogsLimitOptions,
    AdminUltraDebugStructuredEventViewModel? LatestStructuredEvent = null,
    IReadOnlyCollection<AdminUltraDebugStructuredEventViewModel>? LatestCategoryEvents = null,
    AdminUltraDebugTailViewModel? NormalLogsTail = null,
    AdminUltraDebugTailViewModel? UltraLogsTail = null,
    AdminUltraDebugLogSearchViewModel? SearchView = null);

public sealed record AdminUltraDebugStructuredEventViewModel(
    string CategoryLabel,
    string EventName,
    string Summary,
    string OccurredAtUtcLabel,
    string SymbolLabel,
    string TimeframeLabel,
    string SourceLayerLabel,
    string DecisionReasonCodeLabel,
    string BlockerCodeLabel,
    string LatencyBreakdownLabel);

public sealed record AdminUltraDebugTailViewModel(
    string BucketLabel,
    string SummaryLabel,
    bool IsTruncated,
    IReadOnlyCollection<AdminUltraDebugTailLineViewModel> Lines);

public sealed record AdminUltraDebugTailLineViewModel(
    string OccurredAtUtcLabel,
    string CategoryLabel,
    string BucketLabel,
    string SourceLabel,
    string EventName,
    string Summary,
    string SymbolLabel,
    string DetailPreview,
    string CorrelationIdLabel,
    string SourceFileLabel);

public sealed record AdminUltraDebugLogSearchViewModel(
    string SelectedBucketValue,
    string SelectedCategoryValue,
    string SelectedTimeWindowValue,
    int SelectedTake,
    string SourceFilter,
    string SearchTerm,
    bool HasActiveFilters,
    string PerformanceGuardLabel,
    IReadOnlyCollection<AdminUltraDebugLogFilterOptionViewModel> BucketOptions,
    IReadOnlyCollection<AdminUltraDebugLogFilterOptionViewModel> CategoryOptions,
    IReadOnlyCollection<AdminUltraDebugLogFilterOptionViewModel> TimeWindowOptions,
    IReadOnlyCollection<AdminUltraDebugLogTakeOptionViewModel> TakeOptions,
    AdminUltraDebugTailViewModel SearchResult);

public sealed record AdminUltraDebugLogFilterOptionViewModel(
    string Value,
    string Label,
    bool IsSelected);

public sealed record AdminUltraDebugLogTakeOptionViewModel(
    int Value,
    string Label,
    bool IsSelected);

public sealed record AdminUltraDebugLogDurationOptionViewModel(
    string Key,
    string Label,
    bool IsSelected);

public sealed record AdminUltraDebugLogSizeLimitOptionViewModel(
    int ValueMb,
    string Label,
    bool IsSelected);

public sealed record AdminIncidentAuditDecisionFilterViewModel(
    string? Query,
    string? CorrelationId,
    string? DecisionId,
    string? ExecutionAttemptId,
    string? UserId,
    string? Symbol,
    string? Outcome,
    string? ReasonCode,
    string? FocusReference,
    int Take,
    int Page,
    int PageSize,
    bool HasActiveFilters);

public sealed record AdminIncidentAuditDecisionPaginationViewModel(
    int Page,
    int PageSize,
    bool HasPreviousPage,
    bool HasNextPage);

public sealed record AdminIncidentAuditDecisionRowViewModel(
    string Reference,
    string Kind,
    string OutcomeLabel,
    string OutcomeTone,
    string StatusLabel,
    string ReasonCode,
    string ReasonType,
    string Summary,
    string ChangedBy,
    string ScopeLabel,
    DateTime OccurredAtUtc,
    string OccurredAtUtcLabel,
    string? CorrelationId,
    string? DecisionId,
    string? ExecutionAttemptId,
    string? IncidentReference,
    string? ApprovalReference,
    string? RawJson);

public sealed record AdminIncidentAuditDecisionDetailViewModel(
    bool HasSelection,
    string Reference,
    string Kind,
    string OutcomeLabel,
    string OutcomeTone,
    string StatusLabel,
    string ReasonCode,
    string ReasonType,
    string Summary,
    string ChangedBy,
    string OccurredAtUtcLabel,
    string ScopeLabel,
    string BeforeSummary,
    string AfterSummary,
    string CorrelationId,
    string DecisionId,
    string ExecutionAttemptId,
    string IncidentReference,
    string ApprovalReference,
    string RawJson,
    IReadOnlyCollection<AdminIncidentAuditDecisionTimelineItemViewModel> DecisionExecutionTrace,
    IReadOnlyCollection<AdminIncidentAuditDecisionTimelineItemViewModel> ApprovalHistory,
    IReadOnlyCollection<AdminIncidentAuditDecisionTimelineItemViewModel> IncidentTimeline,
    IReadOnlyCollection<AdminIncidentAuditDecisionTimelineItemViewModel> AdminAuditTrail);

public sealed record AdminIncidentAuditDecisionTimelineItemViewModel(
    string Kind,
    string Tone,
    string Title,
    string Summary,
    string ChangedBy,
    DateTime OccurredAtUtc,
    string OccurredAtUtcLabel,
    string Meta);

public static class AdminIncidentAuditDecisionCenterComposer
{
    public static AdminIncidentAuditDecisionCenterViewModel Compose(
        LogCenterPageSnapshot snapshot,
        string? outcome,
        string? reasonCode,
        string? focusReference,
        AdminTraceDetailSnapshot? traceDetail,
        ApprovalQueueDetailSnapshot? approvalDetail,
        IncidentDetailSnapshot? incidentDetail,
        DateTime evaluatedAtUtc,
        bool allowImplicitSelection = true)
    {
        var filters = BuildFilters(snapshot.Filters, outcome, reasonCode, focusReference);
        var filteredRows = snapshot.Entries
            .Select(MapRow)
            .Where(row => MatchesOutcome(row, filters.Outcome) && MatchesReasonCode(row, filters.ReasonCode))
            .OrderByDescending(row => row.OccurredAtUtc)
            .ThenBy(row => row.Kind, StringComparer.Ordinal)
            .ToArray();
        var pagination = BuildPagination(filteredRows, filters);
        var rows = filteredRows
            .Skip((pagination.Page - 1) * pagination.PageSize)
            .Take(pagination.PageSize)
            .ToArray();
        var selectedRow = ResolveSelectedRow(rows, filters.FocusReference, allowImplicitSelection);
        var detail = BuildDetail(selectedRow, filteredRows, traceDetail, approvalDetail, incidentDetail);

        return new AdminIncidentAuditDecisionCenterViewModel(
            RefreshedAtUtcLabel: FormatUtc(evaluatedAtUtc),
            HasError: snapshot.HasError,
            ErrorMessage: snapshot.ErrorMessage,
            Filters: filters,
            SummaryCards: BuildSummaryCards(filteredRows, snapshot),
            Rows: rows,
            Detail: detail,
            EmptyStateMessage: ResolveEmptyStateMessage(snapshot, filteredRows, rows, filters),
            Pagination: pagination);
    }

    private static AdminIncidentAuditDecisionFilterViewModel BuildFilters(
        LogCenterQueryRequest request,
        string? outcome,
        string? reasonCode,
        string? focusReference)
    {
        var normalizedOutcome = NormalizeOptional(outcome, 32);
        var normalizedReasonCode = NormalizeOptional(reasonCode, 128);
        var normalizedFocusReference = NormalizeOptional(focusReference, 128);

        return new AdminIncidentAuditDecisionFilterViewModel(
            Query: request.Query,
            CorrelationId: request.CorrelationId,
            DecisionId: request.DecisionId,
            ExecutionAttemptId: request.ExecutionAttemptId,
            UserId: request.UserId,
            Symbol: request.Symbol,
            Outcome: normalizedOutcome,
            ReasonCode: normalizedReasonCode,
            FocusReference: normalizedFocusReference,
            Take: request.Take,
            Page: request.Page,
            PageSize: request.PageSize,
            HasActiveFilters:
                !string.IsNullOrWhiteSpace(request.Query) ||
                !string.IsNullOrWhiteSpace(request.CorrelationId) ||
                !string.IsNullOrWhiteSpace(request.DecisionId) ||
                !string.IsNullOrWhiteSpace(request.ExecutionAttemptId) ||
                !string.IsNullOrWhiteSpace(request.UserId) ||
                !string.IsNullOrWhiteSpace(request.Symbol) ||
                !string.IsNullOrWhiteSpace(normalizedOutcome) ||
                !string.IsNullOrWhiteSpace(normalizedReasonCode));
    }

    private static IReadOnlyCollection<AdminOperationsSummaryCardViewModel> BuildSummaryCards(
        IReadOnlyCollection<AdminIncidentAuditDecisionRowViewModel> rows,
        LogCenterPageSnapshot snapshot)
    {
        var allowCount = rows.Count(item => item.OutcomeLabel == "Allow");
        var blockCount = rows.Count(item => item.OutcomeLabel == "Block");
        var retryCount = rows.Count(item => item.OutcomeLabel == "Retry");
        var skipCount = rows.Count(item => item.OutcomeLabel == "Skip");
        var adminChangeCount = rows.Count(item => string.Equals(item.Kind, "AdminAuditLog", StringComparison.Ordinal));
        var retentionSummary = snapshot.Retention.LastRunSummary ?? "Retention run unavailable.";

        return
        [
            new AdminOperationsSummaryCardViewModel(
                "Kayit",
                rows.Count.ToString(),
                rows.Count > 0 ? "healthy" : "warning",
                "Filtrelenmis incident, audit ve decision kayitlari tek listede gorunur."),
            new AdminOperationsSummaryCardViewModel(
                "Allow",
                allowCount.ToString(),
                allowCount > 0 ? "healthy" : "neutral",
                "Allow outcome normalize edilmis kararlar."),
            new AdminOperationsSummaryCardViewModel(
                "Block",
                blockCount.ToString(),
                blockCount > 0 ? "critical" : "neutral",
                "Block, veto ve reject outcome kayitlari."),
            new AdminOperationsSummaryCardViewModel(
                "Retry / Skip",
                $"{retryCount}/{skipCount}",
                retryCount > 0 ? "warning" : skipCount > 0 ? "info" : "neutral",
                "Retry ve skip outcome ayri filtrelenebilir."),
            new AdminOperationsSummaryCardViewModel(
                "Admin degisikligi",
                adminChangeCount.ToString(),
                adminChangeCount > 0 ? "warning" : "neutral",
                "ChangedBy / Before / After zinciri audit kayitlarindan gelir."),
            new AdminOperationsSummaryCardViewModel(
                "Retention",
                snapshot.Retention.Enabled ? "Enabled" : "Disabled",
                snapshot.Retention.Enabled ? "info" : "warning",
                retentionSummary)
        ];
    }

    private static AdminIncidentAuditDecisionRowViewModel MapRow(LogCenterEntrySnapshot entry)
    {
        var outcome = ResolveOutcome(entry);
        var rawPayload = entry.RawJson ?? "{}";

        return new AdminIncidentAuditDecisionRowViewModel(
            Reference: entry.Reference,
            Kind: entry.Kind,
            OutcomeLabel: outcome.Label,
            OutcomeTone: outcome.Tone,
            StatusLabel: NormalizeRequiredValue(entry.Status, "Unavailable"),
            ReasonCode: ResolveReasonCode(entry, rawPayload),
            ReasonType: NormalizeRequiredValue(entry.DecisionReasonType ?? TryReadString(rawPayload, "DecisionReasonType"), "Unavailable"),
            Summary: NormalizeRequiredValue(entry.DecisionSummary ?? entry.Summary, "Summary unavailable."),
            ChangedBy: ResolveChangedBy(entry, rawPayload),
            ScopeLabel: ResolveScopeLabel(entry, rawPayload),
            OccurredAtUtc: entry.DecisionAtUtc ?? entry.CreatedAtUtc,
            OccurredAtUtcLabel: FormatUtc(entry.DecisionAtUtc ?? entry.CreatedAtUtc),
            CorrelationId: NormalizeOptional(entry.CorrelationId, 128),
            DecisionId: NormalizeOptional(entry.DecisionId, 64),
            ExecutionAttemptId: NormalizeOptional(entry.ExecutionAttemptId, 64),
            IncidentReference: NormalizeOptional(entry.IncidentReference, 128),
            ApprovalReference: NormalizeOptional(entry.ApprovalReference, 128),
            RawJson: rawPayload);
    }

    private static AdminIncidentAuditDecisionPaginationViewModel BuildPagination(
        IReadOnlyCollection<AdminIncidentAuditDecisionRowViewModel> rows,
        AdminIncidentAuditDecisionFilterViewModel filters)
    {
        var page = filters.Page >= 1 ? filters.Page : 1;
        var pageSize = filters.PageSize is 10 or 25 or 50 or 100 ? filters.PageSize : 25;
        var skip = (page - 1) * pageSize;

        return new AdminIncidentAuditDecisionPaginationViewModel(
            Page: page,
            PageSize: pageSize,
            HasPreviousPage: page > 1,
            HasNextPage: rows.Count > skip + pageSize);
    }

    private static AdminIncidentAuditDecisionDetailViewModel BuildDetail(
        AdminIncidentAuditDecisionRowViewModel? selectedRow,
        IReadOnlyCollection<AdminIncidentAuditDecisionRowViewModel> rows,
        AdminTraceDetailSnapshot? traceDetail,
        ApprovalQueueDetailSnapshot? approvalDetail,
        IncidentDetailSnapshot? incidentDetail)
    {
        if (selectedRow is null)
        {
            return new AdminIncidentAuditDecisionDetailViewModel(
                HasSelection: false,
                Reference: "-",
                Kind: "NoSelection",
                OutcomeLabel: "Unavailable",
                OutcomeTone: "warning",
                StatusLabel: "Unavailable",
                ReasonCode: "Unavailable",
                ReasonType: "Unavailable",
                Summary: "Filtre sonucunda kayit yok. Query veya outcome/reason filtresini gevsetip tekrar deneyin.",
                ChangedBy: "Unavailable",
                OccurredAtUtcLabel: "n/a",
                ScopeLabel: "Unavailable",
                BeforeSummary: "Unavailable",
                AfterSummary: "Unavailable",
                CorrelationId: "Unavailable",
                DecisionId: "Unavailable",
                ExecutionAttemptId: "Unavailable",
                IncidentReference: "Unavailable",
                ApprovalReference: "Unavailable",
                RawJson: "{}",
                DecisionExecutionTrace: Array.Empty<AdminIncidentAuditDecisionTimelineItemViewModel>(),
                ApprovalHistory: Array.Empty<AdminIncidentAuditDecisionTimelineItemViewModel>(),
                IncidentTimeline: Array.Empty<AdminIncidentAuditDecisionTimelineItemViewModel>(),
                AdminAuditTrail: Array.Empty<AdminIncidentAuditDecisionTimelineItemViewModel>());
        }

        var beforeAfter = ResolveBeforeAfter(selectedRow, approvalDetail, incidentDetail);
        var decisionExecutionTrace = BuildDecisionExecutionTrace(traceDetail);
        var approvalHistory = BuildApprovalHistory(approvalDetail);
        var incidentTimeline = BuildIncidentTimeline(incidentDetail);
        var adminAuditTrail = BuildAdminAuditTrail(rows, selectedRow);

        return new AdminIncidentAuditDecisionDetailViewModel(
            HasSelection: true,
            Reference: selectedRow.Reference,
            Kind: selectedRow.Kind,
            OutcomeLabel: selectedRow.OutcomeLabel,
            OutcomeTone: selectedRow.OutcomeTone,
            StatusLabel: selectedRow.StatusLabel,
            ReasonCode: selectedRow.ReasonCode,
            ReasonType: selectedRow.ReasonType,
            Summary: selectedRow.Summary,
            ChangedBy: selectedRow.ChangedBy,
            OccurredAtUtcLabel: selectedRow.OccurredAtUtcLabel,
            ScopeLabel: selectedRow.ScopeLabel,
            BeforeSummary: beforeAfter.BeforeSummary,
            AfterSummary: beforeAfter.AfterSummary,
            CorrelationId: NormalizeRequiredValue(selectedRow.CorrelationId, "Unavailable"),
            DecisionId: NormalizeRequiredValue(selectedRow.DecisionId, "Unavailable"),
            ExecutionAttemptId: NormalizeRequiredValue(selectedRow.ExecutionAttemptId, "Unavailable"),
            IncidentReference: NormalizeRequiredValue(selectedRow.IncidentReference, "Unavailable"),
            ApprovalReference: NormalizeRequiredValue(selectedRow.ApprovalReference, "Unavailable"),
            RawJson: NormalizeRequiredValue(selectedRow.RawJson, "{}"),
            DecisionExecutionTrace: decisionExecutionTrace,
            ApprovalHistory: approvalHistory,
            IncidentTimeline: incidentTimeline,
            AdminAuditTrail: adminAuditTrail);
    }

    private static IReadOnlyCollection<AdminIncidentAuditDecisionTimelineItemViewModel> BuildDecisionExecutionTrace(AdminTraceDetailSnapshot? traceDetail)
    {
        if (traceDetail is null)
        {
            return Array.Empty<AdminIncidentAuditDecisionTimelineItemViewModel>();
        }

        var handoffAttempts = traceDetail.HandoffAttempts ?? Array.Empty<AdminTraceHandoffAttemptSnapshot>();
        var executionTransitions = traceDetail.ExecutionTransitions ?? Array.Empty<AdminTraceExecutionTransitionSnapshot>();

        return traceDetail.DecisionTraces
            .Select(decision => new AdminIncidentAuditDecisionTimelineItemViewModel(
                Kind: "Decision",
                Tone: ResolveOutcomeTone(decision.DecisionOutcome, decision.DecisionReasonType, decision.DecisionReasonCode),
                Title: $"{decision.DecisionId} · {decision.Symbol}/{decision.Timeframe}",
                Summary: NormalizeRequiredValue(decision.DecisionSummary, decision.DecisionOutcome),
                ChangedBy: decision.UserId,
                OccurredAtUtc: decision.DecisionAtUtc ?? decision.CreatedAtUtc,
                OccurredAtUtcLabel: FormatUtc(decision.DecisionAtUtc ?? decision.CreatedAtUtc),
                Meta: $"ReasonCode={NormalizeRequiredValue(decision.DecisionReasonCode, "Unavailable")} · ReasonType={NormalizeRequiredValue(decision.DecisionReasonType, "Unavailable")}"))
            .Concat(handoffAttempts.Select(handoff => new AdminIncidentAuditDecisionTimelineItemViewModel(
                Kind: "Handoff",
                Tone: ResolveHandoffTone(handoff.ExecutionRequestStatus, handoff.BlockerCode),
                Title: $"{handoff.HandoffAttemptId:N} · {NormalizeRequiredValue(handoff.Symbol, "n/a")}/{NormalizeRequiredValue(handoff.Timeframe, "n/a")}",
                Summary: NormalizeRequiredValue(handoff.BlockerSummary, $"{handoff.ExecutionRequestStatus} · {handoff.StrategyDecisionOutcome}"),
                ChangedBy: NormalizeRequiredValue(handoff.OwnerUserId, "system"),
                OccurredAtUtc: handoff.CompletedAtUtc,
                OccurredAtUtcLabel: FormatUtc(handoff.CompletedAtUtc),
                Meta: $"ScanCycle={handoff.ScanCycleId:N} · StrategySignal={NormalizeOptional(handoff.StrategySignalId?.ToString("N"), 64) ?? "Unavailable"} · Bot={NormalizeOptional(handoff.BotId?.ToString("N"), 64) ?? "Unavailable"} · Env={NormalizeRequiredValue(handoff.ExecutionEnvironment, "Unavailable")} · Side={NormalizeRequiredValue(handoff.ExecutionSide, "Unavailable")} · Blocker={NormalizeRequiredValue(handoff.BlockerCode, "Unavailable")}")))
            .Concat(traceDetail.ExecutionTraces.Select(execution => new AdminIncidentAuditDecisionTimelineItemViewModel(
                Kind: "Execution",
                Tone: ResolveExecutionTone(execution.HttpStatusCode),
                Title: $"{execution.ExecutionAttemptId} · {execution.Provider}",
                Summary: $"Status={(execution.HttpStatusCode?.ToString() ?? "Pending")} · ExchangeCode={NormalizeRequiredValue(execution.ExchangeCode, "Unavailable")}",
                ChangedBy: execution.UserId,
                OccurredAtUtc: execution.CreatedAtUtc,
                OccurredAtUtcLabel: FormatUtc(execution.CreatedAtUtc),
                Meta: $"Endpoint={NormalizeRequiredValue(execution.Endpoint, "Unavailable")} · Latency={(execution.LatencyMs?.ToString() ?? "n/a")} ms")))
            .Concat(executionTransitions.Select(transition => new AdminIncidentAuditDecisionTimelineItemViewModel(
                Kind: "Execution transition",
                Tone: ResolveTransitionTone(transition.State),
                Title: $"{transition.State} · {transition.EventCode}",
                Summary: NormalizeRequiredValue(transition.Detail, $"Transition sequence {transition.SequenceNumber}."),
                ChangedBy: "system",
                OccurredAtUtc: transition.OccurredAtUtc,
                OccurredAtUtcLabel: FormatUtc(transition.OccurredAtUtc),
                Meta: $"OrderId={transition.ExecutionOrderId:N} · Sequence={transition.SequenceNumber} · Correlation={transition.CorrelationId}")))
            .OrderBy(item => item.OccurredAtUtc)
            .ToArray();
    }

    private static string ResolveHandoffTone(string executionRequestStatus, string? blockerCode)
    {
        if (string.Equals(executionRequestStatus, "Prepared", StringComparison.OrdinalIgnoreCase))
        {
            return "healthy";
        }

        return string.IsNullOrWhiteSpace(blockerCode)
            ? "warning"
            : "critical";
    }

    private static string ResolveTransitionTone(string state)
    {
        return state switch
        {
            "Filled" => "healthy",
            "Submitted" or "Dispatching" or "GatePassed" or "Received" => "info",
            "Rejected" or "Failed" => "critical",
            "Cancelled" or "CancelRequested" => "warning",
            _ => "neutral"
        };
    }

    private static IReadOnlyCollection<AdminIncidentAuditDecisionTimelineItemViewModel> BuildApprovalHistory(ApprovalQueueDetailSnapshot? approvalDetail)
    {
        if (approvalDetail is null)
        {
            return Array.Empty<AdminIncidentAuditDecisionTimelineItemViewModel>();
        }

        return approvalDetail.Actions
            .OrderBy(item => item.CreatedAtUtc)
            .Select(item => new AdminIncidentAuditDecisionTimelineItemViewModel(
                Kind: "Approval",
                Tone: ResolveApprovalTone(item.ActionType.ToString()),
                Title: $"{approvalDetail.ApprovalReference} · {item.ActionType}",
                Summary: NormalizeRequiredValue(item.Reason, "Approval action recorded."),
                ChangedBy: item.ActorUserId,
                OccurredAtUtc: item.CreatedAtUtc,
                OccurredAtUtcLabel: FormatUtc(item.CreatedAtUtc),
                Meta: $"Status={approvalDetail.Status} · Sequence={item.Sequence}"))
            .ToArray();
    }

    private static IReadOnlyCollection<AdminIncidentAuditDecisionTimelineItemViewModel> BuildIncidentTimeline(IncidentDetailSnapshot? incidentDetail)
    {
        if (incidentDetail is null)
        {
            return Array.Empty<AdminIncidentAuditDecisionTimelineItemViewModel>();
        }

        return incidentDetail.Timeline
            .OrderBy(item => item.CreatedAtUtc)
            .Select(item => new AdminIncidentAuditDecisionTimelineItemViewModel(
                Kind: "Incident",
                Tone: ResolveIncidentTone(item.EventType.ToString()),
                Title: $"{incidentDetail.IncidentReference} · {item.EventType}",
                Summary: NormalizeRequiredValue(item.Message, incidentDetail.Summary),
                ChangedBy: NormalizeRequiredValue(item.ActorUserId, incidentDetail.CreatedByUserId ?? "Unavailable"),
                OccurredAtUtc: item.CreatedAtUtc,
                OccurredAtUtcLabel: FormatUtc(item.CreatedAtUtc),
                Meta: $"Status={incidentDetail.Status} · Severity={incidentDetail.Severity}"))
            .ToArray();
    }

    private static IReadOnlyCollection<AdminIncidentAuditDecisionTimelineItemViewModel> BuildAdminAuditTrail(
        IReadOnlyCollection<AdminIncidentAuditDecisionRowViewModel> rows,
        AdminIncidentAuditDecisionRowViewModel selectedRow)
    {
        return rows
            .Where(row =>
                string.Equals(row.Kind, "AdminAuditLog", StringComparison.Ordinal) &&
                !string.Equals(row.Reference, selectedRow.Reference, StringComparison.Ordinal) &&
                IsRelated(row, selectedRow))
            .OrderBy(item => item.OccurredAtUtc)
            .Take(12)
            .Select(item =>
            {
                var beforeAfter = ResolveBeforeAfter(item, null, null);
                return new AdminIncidentAuditDecisionTimelineItemViewModel(
                    Kind: "Admin change",
                    Tone: item.OutcomeTone,
                    Title: item.Reference,
                    Summary: $"{beforeAfter.BeforeSummary} -> {beforeAfter.AfterSummary}",
                    ChangedBy: item.ChangedBy,
                    OccurredAtUtc: item.OccurredAtUtc,
                    OccurredAtUtcLabel: item.OccurredAtUtcLabel,
                    Meta: item.Summary);
            })
            .ToArray();
    }

    private static (string BeforeSummary, string AfterSummary) ResolveBeforeAfter(
        AdminIncidentAuditDecisionRowViewModel row,
        ApprovalQueueDetailSnapshot? approvalDetail,
        IncidentDetailSnapshot? incidentDetail)
    {
        var rawPayload = row.RawJson;

        if (string.Equals(row.Kind, "AdminAuditLog", StringComparison.Ordinal))
        {
            return (
                NormalizeRequiredValue(TryReadString(rawPayload, "OldValueSummary"), "Unavailable"),
                NormalizeRequiredValue(TryReadString(rawPayload, "NewValueSummary"), "Unavailable"));
        }

        if (string.Equals(row.Kind, "ApprovalQueue", StringComparison.Ordinal) && approvalDetail is not null)
        {
            return (
                $"Approvals={approvalDetail.ApprovalCount}/{approvalDetail.RequiredApprovals}; Status={approvalDetail.Status}",
                NormalizeRequiredValue(approvalDetail.ExecutionSummary ?? approvalDetail.RejectReason, $"{approvalDetail.Status} · {approvalDetail.Summary}"));
        }

        if (string.Equals(row.Kind, "Incident", StringComparison.Ordinal) && incidentDetail is not null)
        {
            return (
                $"Status={incidentDetail.Status} · CreatedBy={NormalizeRequiredValue(incidentDetail.CreatedByUserId, "Unavailable")}",
                NormalizeRequiredValue(incidentDetail.ResolvedSummary, incidentDetail.Summary));
        }

        if (string.Equals(row.Kind, "DecisionTrace", StringComparison.Ordinal) ||
            string.Equals(row.Kind, "ExecutionTrace", StringComparison.Ordinal))
        {
            return ("Unavailable", "Unavailable");
        }

        return ("Unavailable", "Unavailable");
    }

    private static AdminIncidentAuditDecisionRowViewModel? ResolveSelectedRow(
        IReadOnlyCollection<AdminIncidentAuditDecisionRowViewModel> rows,
        string? focusReference,
        bool allowImplicitSelection)
    {
        if (!string.IsNullOrWhiteSpace(focusReference))
        {
            var selectors = new Func<AdminIncidentAuditDecisionRowViewModel, string?>[]
            {
                row => row.Reference,
                row => row.ApprovalReference,
                row => row.IncidentReference,
                row => row.DecisionId,
                row => row.ExecutionAttemptId,
                row => row.CorrelationId
            };

            foreach (var selector in selectors)
            {
                var match = rows.FirstOrDefault(row => string.Equals(selector(row), focusReference, StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                {
                    return match;
                }
            }
        }

        return allowImplicitSelection
            ? rows.FirstOrDefault()
            : null;
    }

    private static bool MatchesOutcome(AdminIncidentAuditDecisionRowViewModel row, string? outcome)
    {
        return string.IsNullOrWhiteSpace(outcome) ||
            string.Equals(row.OutcomeLabel, outcome, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesReasonCode(AdminIncidentAuditDecisionRowViewModel row, string? reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode))
        {
            return true;
        }

        return row.ReasonCode.Contains(reasonCode, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRelated(AdminIncidentAuditDecisionRowViewModel candidate, AdminIncidentAuditDecisionRowViewModel selected)
    {
        return
            (!string.IsNullOrWhiteSpace(selected.CorrelationId) && string.Equals(candidate.CorrelationId, selected.CorrelationId, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(selected.DecisionId) && string.Equals(candidate.DecisionId, selected.DecisionId, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(selected.ExecutionAttemptId) && string.Equals(candidate.ExecutionAttemptId, selected.ExecutionAttemptId, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(selected.IncidentReference) && string.Equals(candidate.IncidentReference, selected.IncidentReference, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(selected.ApprovalReference) && string.Equals(candidate.ApprovalReference, selected.ApprovalReference, StringComparison.OrdinalIgnoreCase));
    }

    private static (string Label, string Tone) ResolveOutcome(LogCenterEntrySnapshot entry)
    {
        var bag = string.Join(
            " | ",
            new[]
            {
                entry.Kind,
                entry.Status,
                entry.DecisionReasonType,
                entry.DecisionReasonCode,
                entry.DecisionSummary,
                entry.Summary,
                entry.Source
            }.Where(item => !string.IsNullOrWhiteSpace(item)))
            .ToLowerInvariant();

        if (ContainsAny(bag, "retry", "requeue", "backoff"))
        {
            return ("Retry", "warning");
        }

        if (ContainsAny(bag, "skip", "suppressed", "cooldown", "noop", "noeligiblecandidate", "no_signal", "nosignal"))
        {
            return ("Skip", "info");
        }

        if (entry.Kind == "ExecutionTrace")
        {
            var isSuccess = entry.Status.Contains("HTTP 2", StringComparison.OrdinalIgnoreCase);
            return isSuccess
                ? ("Allow", "healthy")
                : ("Block", "critical");
        }

        if (ContainsAny(bag, "allow", "allowed", "approved", "executed", "persisted", "submitted", "armed", "activated", "enabled", "resolved"))
        {
            return ("Allow", "healthy");
        }

        if (ContainsAny(bag, "block", "blocked", "veto", "reject", "rejected", "deny", "denied", "halt", "emergency", "stale", "breach", "breached", "insufficient", "failed", "invalid", "disabled"))
        {
            return ("Block", "critical");
        }

        return ("Unknown", "warning");
    }

    private static string ResolveReasonCode(LogCenterEntrySnapshot entry, string rawPayload)
    {
        var statusReasonCode = string.Equals(entry.Kind, "AdminAuditLog", StringComparison.Ordinal)
            ? entry.Status
            : null;

        return NormalizeRequiredValue(
            entry.DecisionReasonCode ??
            TryReadString(rawPayload, "DecisionReasonCode") ??
            statusReasonCode ??
            TryReadString(rawPayload, "ActionType") ??
            TryReadString(rawPayload, "EventType") ??
            TryReadString(rawPayload, "Status"),
            "Unavailable");
    }

    private static string ResolveChangedBy(LogCenterEntrySnapshot entry, string rawPayload)
    {
        return NormalizeRequiredValue(
            TryReadString(rawPayload, "ActorUserId") ??
            TryReadString(rawPayload, "RequestedByUserId") ??
            TryReadString(rawPayload, "CreatedByUserId") ??
            TryReadString(rawPayload, "ResolvedByUserId") ??
            TryReadString(rawPayload, "LastActorUserId") ??
            entry.UserId ??
            entry.Source,
            "Unavailable");
    }

    private static string ResolveScopeLabel(LogCenterEntrySnapshot entry, string rawPayload)
    {
        var targetType = TryReadString(rawPayload, "TargetType");
        var targetId = TryReadString(rawPayload, "TargetId");

        if (!string.IsNullOrWhiteSpace(targetType) || !string.IsNullOrWhiteSpace(targetId))
        {
            return $"{NormalizeRequiredValue(targetType, "Scope")} / {NormalizeRequiredValue(targetId, "n/a")}";
        }

        if (!string.IsNullOrWhiteSpace(entry.Symbol))
        {
            return entry.Symbol!;
        }

        return NormalizeRequiredValue(entry.Source, "Global");
    }

    private static string ResolveEmptyStateMessage(
        LogCenterPageSnapshot snapshot,
        IReadOnlyCollection<AdminIncidentAuditDecisionRowViewModel> filteredRows,
        IReadOnlyCollection<AdminIncidentAuditDecisionRowViewModel> pagedRows,
        AdminIncidentAuditDecisionFilterViewModel filters)
    {
        if (snapshot.HasError)
        {
            return NormalizeRequiredValue(snapshot.ErrorMessage, "Incident / audit / decision read-model verisi alinamadi.");
        }

        if (filteredRows.Count == 0 && filters.HasActiveFilters)
        {
            return "Filtrelerle eslesen incident, audit veya decision kaydi bulunamadi.";
        }

        if (filteredRows.Count > 0 && pagedRows.Count == 0)
        {
            return "Secilen sayfada kayit yok. Onceki sayfaya donup tekrar deneyin.";
        }

        return "Bu merkezde gosterilecek incident, audit veya decision kaydi yok.";
    }

    private static string ResolveOutcomeTone(string? decisionOutcome, string? decisionReasonType, string? decisionReasonCode)
    {
        var outcome = ResolveOutcome(new LogCenterEntrySnapshot(
            Kind: "DecisionTrace",
            Reference: "detail",
            Status: decisionOutcome ?? string.Empty,
            Tone: "info",
            Severity: null,
            CorrelationId: null,
            DecisionId: null,
            ExecutionAttemptId: null,
            IncidentReference: null,
            ApprovalReference: null,
            UserId: null,
            Symbol: null,
            Title: string.Empty,
            Summary: string.Empty,
            Source: null,
            CreatedAtUtc: DateTime.UtcNow,
            Tags: Array.Empty<string>(),
            RawJson: null,
            DecisionReasonType: decisionReasonType,
            DecisionReasonCode: decisionReasonCode));

        return outcome.Tone;
    }

    private static string ResolveExecutionTone(int? httpStatusCode)
    {
        return httpStatusCode is >= 200 and < 300 ? "healthy" : "critical";
    }

    private static string ResolveApprovalTone(string actionType)
    {
        var normalized = actionType.ToLowerInvariant();
        if (normalized.Contains("reject", StringComparison.Ordinal))
        {
            return "critical";
        }

        if (normalized.Contains("approve", StringComparison.Ordinal) || normalized.Contains("execut", StringComparison.Ordinal))
        {
            return "healthy";
        }

        return "warning";
    }

    private static string ResolveIncidentTone(string eventType)
    {
        var normalized = eventType.ToLowerInvariant();
        if (ContainsAny(normalized, "rejected", "failed", "expired"))
        {
            return "critical";
        }

        if (ContainsAny(normalized, "resolved", "recovery", "executed"))
        {
            return "healthy";
        }

        return "warning";
    }

    private static string NormalizeRequiredValue(string? value, string fallback)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string FormatUtc(DateTime value)
    {
        return value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
    }

    private static string? TryReadString(string? rawPayload, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return null;
        }

        foreach (var candidatePayload in EnumeratePayloadCandidates(rawPayload))
        {
            try
            {
                using var document = JsonDocument.Parse(candidatePayload);
                if (TryReadString(document.RootElement, propertyName, out var value))
                {
                    return value;
                }

                if (document.RootElement.ValueKind == JsonValueKind.String)
                {
                    var nestedPayload = document.RootElement.GetString();
                    if (!string.IsNullOrWhiteSpace(nestedPayload))
                    {
                        using var nestedDocument = JsonDocument.Parse(nestedPayload);
                        if (TryReadString(nestedDocument.RootElement, propertyName, out value))
                        {
                            return value;
                        }
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumeratePayloadCandidates(string rawPayload)
    {
        yield return rawPayload;

        var unescapedQuotes = rawPayload.Replace("\\\"", "\"", StringComparison.Ordinal);
        if (!string.Equals(unescapedQuotes, rawPayload, StringComparison.Ordinal))
        {
            yield return unescapedQuotes;
        }
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string? value)
    {
        value = null;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Null => null,
                _ => property.Value.ToString()
            };

            return true;
        }

        return false;
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        return needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }
}
