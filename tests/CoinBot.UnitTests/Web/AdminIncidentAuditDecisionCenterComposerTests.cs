using CoinBot.Application.Abstractions.Administration;
using CoinBot.Domain.Enums;
using CoinBot.Web.ViewModels.Admin;

namespace CoinBot.UnitTests.Web;

public sealed class AdminIncidentAuditDecisionCenterComposerTests
{
    [Fact]
    public void Compose_NormalizesOutcomeAndAppliesReasonCodeFilter()
    {
        var now = new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc);
        var snapshot = CreateSnapshot(
            now,
            new LogCenterEntrySnapshot(
                "DecisionTrace",
                "dec-allow",
                "Persisted",
                "healthy",
                "Info",
                "corr-allow",
                "dec-allow",
                null,
                null,
                null,
                "user-1",
                "BTCUSDT",
                "Allow decision",
                "Allowed path",
                "StrategyVersion:test",
                now,
                ["Allowed"],
                null,
                DecisionReasonType: "Allow",
                DecisionReasonCode: "Allowed"),
            new LogCenterEntrySnapshot(
                "DecisionTrace",
                "dec-block",
                "Blocked",
                "critical",
                "Critical",
                "corr-block",
                "dec-block",
                null,
                null,
                null,
                "user-2",
                "ETHUSDT",
                "Block decision",
                "Freshness breached",
                "StrategyVersion:test",
                now.AddMinutes(1),
                ["MarketDataLatencyBreached"],
                null,
                DecisionReasonType: "StaleData",
                DecisionReasonCode: "MarketDataLatencyBreached"),
            new LogCenterEntrySnapshot(
                "ApprovalAction",
                "apr-retry:1",
                "RetryScheduled",
                "warning",
                "Warning",
                "corr-retry",
                null,
                null,
                null,
                "APR-retry",
                "approver-1",
                null,
                "Retry scheduled",
                "Retry scheduled after partial outage",
                "approver-1",
                now.AddMinutes(2),
                ["Retry"],
                null),
            new LogCenterEntrySnapshot(
                "DecisionTrace",
                "dec-skip",
                "Suppressed",
                "info",
                "Info",
                "corr-skip",
                "dec-skip",
                null,
                null,
                null,
                "user-3",
                "SOLUSDT",
                "Skip decision",
                "No eligible candidate",
                "StrategyVersion:test",
                now.AddMinutes(3),
                ["NoEligibleCandidate"],
                null,
                DecisionReasonType: "Skip",
                DecisionReasonCode: "NoEligibleCandidate"));

        var model = AdminIncidentAuditDecisionCenterComposer.Compose(
            snapshot,
            outcome: "Block",
            reasonCode: "MarketDataLatency",
            focusReference: null,
            traceDetail: null,
            approvalDetail: null,
            incidentDetail: null,
            evaluatedAtUtc: now.AddMinutes(5));

        var row = Assert.Single(model.Rows);
        Assert.Equal("Block", row.OutcomeLabel);
        Assert.Equal("MarketDataLatencyBreached", row.ReasonCode);
        Assert.Equal("dec-block", model.Detail.Reference);
        Assert.Contains(model.SummaryCards, item => item.Label == "Block" && item.Value == "1");
    }

    [Fact]
    public void Compose_BuildsChangedByBeforeAfter_FromAdminAuditEntry()
    {
        var now = new DateTime(2026, 4, 8, 12, 10, 0, DateTimeKind.Utc);
        var snapshot = CreateSnapshot(
            now,
            new LogCenterEntrySnapshot(
                "AdminAuditLog",
                "Admin.Settings.GlobalSystemState.Update:audit-1",
                "Admin.Settings.GlobalSystemState.Update",
                "warning",
                "Warning",
                "corr-admin",
                null,
                null,
                null,
                null,
                "super-admin",
                null,
                "Global system state update",
                "Maintenance override applied.",
                "Singleton",
                now,
                ["GlobalSystemState"],
                """{"ActorUserId":"super-admin","OldValueSummary":"State=Active","NewValueSummary":"State=Maintenance","Reason":"Maintenance"}"""));

        var model = AdminIncidentAuditDecisionCenterComposer.Compose(
            snapshot,
            outcome: null,
            reasonCode: null,
            focusReference: null,
            traceDetail: null,
            approvalDetail: null,
            incidentDetail: null,
            evaluatedAtUtc: now.AddMinutes(1));

        Assert.Equal("super-admin", model.Detail.ChangedBy);
        Assert.Equal("State=Active", model.Detail.BeforeSummary);
        Assert.Equal("State=Maintenance", model.Detail.AfterSummary);
        Assert.Equal("Admin.Settings.GlobalSystemState.Update", model.Detail.ReasonCode);
    }

    [Fact]
    public void Compose_BuildsApprovalIncidentAndAuditSections_ForFocusedChain()
    {
        var now = new DateTime(2026, 4, 8, 12, 20, 0, DateTimeKind.Utc);
        var snapshot = CreateSnapshot(
            now,
            new LogCenterEntrySnapshot(
                "ApprovalQueue",
                "APR-1",
                "Pending",
                "warning",
                "Warning",
                "corr-1",
                "dec-1",
                "exe-1",
                "INC-1",
                "APR-1",
                "requestor-1",
                null,
                "Maintenance approval",
                "Awaiting final approval",
                "requestor-1",
                now,
                ["GlobalSystemState"],
                """{"ApprovalReference":"APR-1","Status":"Pending","RequestedByUserId":"requestor-1"}"""),
            new LogCenterEntrySnapshot(
                "AdminAuditLog",
                "Admin.Settings.GlobalSystemState.Update:audit-2",
                "Admin.Settings.GlobalSystemState.Update",
                "warning",
                "Warning",
                "corr-1",
                null,
                null,
                "INC-1",
                "APR-1",
                "super-admin",
                null,
                "Global system state update",
                "Maintenance pending approval",
                "Singleton",
                now.AddMinutes(1),
                ["GlobalSystemState"],
                """{"ActorUserId":"super-admin","OldValueSummary":"State=Active","NewValueSummary":"State=MaintenancePending"}"""));
        var traceDetail = new AdminTraceDetailSnapshot(
            "corr-1",
            [
                new DecisionTraceSnapshot(Guid.NewGuid(), Guid.NewGuid(), "corr-1", "dec-1", "requestor-1", "BTCUSDT", "1m", "StrategyVersion:test", "Entry", 52, "Allow", null, 11, "{}", now, DecisionReasonType: "Allow", DecisionReasonCode: "Allowed", DecisionSummary: "Decision allowed.", DecisionAtUtc: now)
            ],
            [
                new ExecutionTraceSnapshot(Guid.NewGuid(), Guid.NewGuid(), "corr-1", "exe-1", "cmd-1", "requestor-1", "Binance.PrivateRest", "/fapi/v1/order", "{}", "{}", 200, "OK", 22, now.AddMinutes(1))
            ]);
        var approvalDetail = new ApprovalQueueDetailSnapshot(
            "APR-1",
            ApprovalQueueOperationType.GlobalSystemStateUpdate,
            ApprovalQueueStatus.Pending,
            IncidentSeverity.Warning,
            "Maintenance approval",
            "Awaiting final approval",
            "GlobalSystemState",
            "Singleton",
            "requestor-1",
            "Maintenance required",
            "{\"state\":\"Maintenance\"}",
            2,
            1,
            now.AddHours(1),
            "corr-1",
            "cmd-1",
            "dec-1",
            "exe-1",
            "INC-1",
            null,
            null,
            now,
            now,
            null,
            null,
            null,
            null,
            null,
            null,
            [
                new ApprovalActionSnapshot(1, ApprovalActionType.Approved, "approver-1", "Looks good", "corr-1", "cmd-1", "dec-1", "exe-1", now.AddMinutes(2))
            ]);
        var incidentDetail = new IncidentDetailSnapshot(
            "INC-1",
            IncidentSeverity.Warning,
            IncidentStatus.PendingApproval,
            "Maintenance incident",
            "Pending approval for maintenance",
            "Detailed incident",
            ApprovalQueueOperationType.GlobalSystemStateUpdate,
            "GlobalSystemState",
            "Singleton",
            "corr-1",
            "cmd-1",
            "dec-1",
            "exe-1",
            "APR-1",
            null,
            null,
            "requestor-1",
            now,
            null,
            null,
            null,
            [
                new IncidentEventSnapshot(IncidentEventType.ApprovalQueued, "Approval queued", "requestor-1", "corr-1", "cmd-1", "dec-1", "exe-1", "APR-1", null, null, null, now.AddMinutes(3))
            ]);

        var model = AdminIncidentAuditDecisionCenterComposer.Compose(
            snapshot,
            outcome: null,
            reasonCode: null,
            focusReference: "APR-1",
            traceDetail,
            approvalDetail,
            incidentDetail,
            now.AddMinutes(5));

        Assert.Equal("APR-1", model.Detail.Reference);
        Assert.Equal(2, model.Detail.DecisionExecutionTrace.Count);
        Assert.Single(model.Detail.ApprovalHistory);
        Assert.Single(model.Detail.IncidentTimeline);
        Assert.Single(model.Detail.AdminAuditTrail);
        Assert.Equal("approver-1", model.Detail.ApprovalHistory.Single().ChangedBy);
    }

    [Fact]
    public void Compose_IncludesHandoffAndExecutionTransition_InDecisionExecutionTimeline()
    {
        var now = new DateTime(2026, 4, 8, 12, 25, 0, DateTimeKind.Utc);
        var executionOrderId = Guid.NewGuid();
        var strategySignalId = Guid.NewGuid();
        var snapshot = CreateSnapshot(
            now,
            new LogCenterEntrySnapshot(
                "DecisionTrace",
                "dec-chain",
                "Persisted",
                "healthy",
                "Info",
                "corr-chain",
                "dec-chain",
                "exe-chain",
                null,
                null,
                "user-chain",
                "SOLUSDT",
                "Signal persisted",
                "Signal persisted",
                "StrategyVersion:test",
                now,
                ["CandidatePersisted"],
                "{}",
                DecisionReasonType: "StrategyCandidate",
                DecisionReasonCode: "CandidatePersisted"));
        var traceDetail = new AdminTraceDetailSnapshot(
            "corr-chain",
            [
                new DecisionTraceSnapshot(Guid.NewGuid(), strategySignalId, "corr-chain", "dec-chain", "user-chain", "SOLUSDT", "1m", "StrategyVersion:test", "Entry", 55, "Persisted", null, 11, "{}", now)
            ],
            [
                new ExecutionTraceSnapshot(Guid.NewGuid(), executionOrderId, "corr-chain", "exe-chain", "cmd-chain", "user-chain", "Binance.PrivateRest", "/fapi/v1/order", "{}", "{}", 200, "OK", 22, now.AddSeconds(2))
            ],
            [
                new AdminTraceHandoffAttemptSnapshot(Guid.NewGuid(), Guid.NewGuid(), strategySignalId, "user-chain", Guid.NewGuid(), "SOLUSDT", "1m", "Persisted", "Prepared", null, "Allowed: execution request prepared.", "ExecutionGate=Allowed", "Live", "Buy", now.AddSeconds(1))
            ],
            [
                new AdminTraceExecutionTransitionSnapshot(Guid.NewGuid(), executionOrderId, 1, "Submitted", "Submitted", "Execution accepted", "corr-chain", "corr-chain", now.AddSeconds(3))
            ]);

        var model = AdminIncidentAuditDecisionCenterComposer.Compose(
            snapshot,
            outcome: null,
            reasonCode: null,
            focusReference: "dec-chain",
            traceDetail,
            approvalDetail: null,
            incidentDetail: null,
            evaluatedAtUtc: now.AddMinutes(1));

        Assert.Equal(4, model.Detail.DecisionExecutionTrace.Count);
        Assert.Contains(model.Detail.DecisionExecutionTrace, item => item.Kind == "Handoff");
        Assert.Contains(model.Detail.DecisionExecutionTrace, item => item.Kind == "Execution transition");
    }

    [Fact]
    public void Compose_UsesUnavailableFallback_WhenReasonAndDiffMissing()
    {
        var now = new DateTime(2026, 4, 8, 12, 30, 0, DateTimeKind.Utc);
        var snapshot = CreateSnapshot(
            now,
            new LogCenterEntrySnapshot(
                "ExecutionTrace",
                "exe-missing",
                "Pending",
                "warning",
                "Warning",
                "corr-missing",
                null,
                "exe-missing",
                null,
                null,
                null,
                null,
                "Execution pending",
                "Execution pending",
                null,
                now,
                Array.Empty<string>(),
                "{}"));

        var model = AdminIncidentAuditDecisionCenterComposer.Compose(
            snapshot,
            outcome: null,
            reasonCode: null,
            focusReference: null,
            traceDetail: null,
            approvalDetail: null,
            incidentDetail: null,
            evaluatedAtUtc: now.AddMinutes(1));

        Assert.Equal("Unavailable", model.Detail.ReasonCode);
        Assert.Equal("Unavailable", model.Detail.BeforeSummary);
        Assert.Equal("Unavailable", model.Detail.AfterSummary);
        Assert.Equal("Unavailable", model.Detail.ChangedBy);
    }
    private static LogCenterPageSnapshot CreateSnapshot(DateTime now, params LogCenterEntrySnapshot[] entries)
    {
        return new LogCenterPageSnapshot(
            new LogCenterQueryRequest(null, null, null, null, null, null, null, null, null, 120),
            new LogCenterSummarySnapshot(entries.Length, 0, 0, 0, 0, 0, 0, 0, entries.Length, now),
            new LogCenterRetentionSnapshot(true, 45, 45, 90, 180, 180, 250, now, "Retention completed"),
            entries,
            false,
            null);
    }
}



