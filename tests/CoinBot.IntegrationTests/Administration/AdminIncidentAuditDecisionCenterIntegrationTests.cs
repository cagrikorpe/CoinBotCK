using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.IntegrationTests.Infrastructure;
using CoinBot.Web.ViewModels.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.IntegrationTests.Administration;

public sealed class AdminIncidentAuditDecisionCenterIntegrationTests
{
    [Fact]
    public async Task IncidentAuditDecisionCenter_ComposesPersistedDecisionApprovalIncidentChain()
    {
        var databaseName = $"CoinBotAdminIncidentAuditInt_{Guid.NewGuid():N}";
        var connectionString = ResolveConnectionString(databaseName);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IDataScopeContext>(new TestDataScopeContext());
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(connectionString));

        using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        var now = new DateTime(2026, 4, 8, 14, 0, 0, DateTimeKind.Utc);
        var incidentId = Guid.NewGuid();
        var approvalQueueId = Guid.NewGuid();
        var scanCycleId = Guid.NewGuid();
        var strategySignalId = Guid.NewGuid();
        var executionOrderId = Guid.NewGuid();

        dbContext.Users.AddRange(
            new ApplicationUser
            {
                Id = "user-ops-1",
                UserName = "user-ops-1",
                NormalizedUserName = "USER-OPS-1",
                Email = "user-ops-1@coinbot.test",
                NormalizedEmail = "USER-OPS-1@COINBOT.TEST",
                FullName = "Audit Ops User"
            },
            new ApplicationUser
            {
                Id = "requestor-ops-1",
                UserName = "requestor-ops-1",
                NormalizedUserName = "REQUESTOR-OPS-1",
                Email = "requestor-ops-1@coinbot.test",
                NormalizedEmail = "REQUESTOR-OPS-1@COINBOT.TEST",
                FullName = "Audit Requestor"
            },
            new ApplicationUser
            {
                Id = "approver-ops-1",
                UserName = "approver-ops-1",
                NormalizedUserName = "APPROVER-OPS-1",
                Email = "approver-ops-1@coinbot.test",
                NormalizedEmail = "APPROVER-OPS-1@COINBOT.TEST",
                FullName = "Audit Approver"
            },
            new ApplicationUser
            {
                Id = "super-admin",
                UserName = "super-admin",
                NormalizedUserName = "SUPER-ADMIN",
                Email = "super-admin@coinbot.test",
                NormalizedEmail = "SUPER-ADMIN@COINBOT.TEST",
                FullName = "Super Admin"
            });

        dbContext.MarketScannerCycles.Add(new MarketScannerCycle
        {
            Id = scanCycleId,
            StartedAtUtc = now.AddSeconds(-45),
            CompletedAtUtc = now.AddSeconds(-15),
            UniverseSource = "integration-test",
            ScannedSymbolCount = 1,
            EligibleCandidateCount = 1,
            TopCandidateCount = 1,
            BestCandidateSymbol = "BTCUSDT",
            BestCandidateScore = 95m,
            Summary = "admin-trace-correlation"
        });

        dbContext.DecisionTraces.Add(new DecisionTrace
        {
            Id = Guid.NewGuid(),
            StrategySignalId = strategySignalId,
            CorrelationId = "corr-audit-int-1",
            DecisionId = "dec-audit-int-1",
            UserId = "user-ops-1",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            StrategyVersion = "StrategyVersion:test",
            SignalType = "Entry",
            RiskScore = 63,
            DecisionOutcome = "Blocked",
            DecisionReasonType = "Readiness",
            DecisionReasonCode = "MarketDataLatencyBreached",
            DecisionSummary = "Market data freshness gate blocked activation.",
            DecisionAtUtc = now,
            LatencyMs = 12,
            SnapshotJson = "{}",
            CreatedAtUtc = now
        });

        dbContext.ExecutionTraces.Add(new ExecutionTrace
        {
            Id = Guid.NewGuid(),
            ExecutionOrderId = executionOrderId,
            CorrelationId = "corr-audit-int-1",
            ExecutionAttemptId = "exe-audit-int-1",
            CommandId = "cmd-audit-int-1",
            UserId = "user-ops-1",
            Provider = "Binance.PrivateRest",
            Endpoint = "/fapi/v1/order",
            RequestMasked = "{}",
            ResponseMasked = "{}",
            HttpStatusCode = 503,
            ExchangeCode = "-1001",
            LatencyMs = 31,
            CreatedAtUtc = now.AddMinutes(1)
        });

        dbContext.MarketScannerHandoffAttempts.Add(new MarketScannerHandoffAttempt
        {
            Id = Guid.NewGuid(),
            ScanCycleId = scanCycleId,
            SelectedSymbol = "BTCUSDT",
            SelectedTimeframe = "1m",
            OwnerUserId = "user-ops-1",
            BotId = Guid.NewGuid(),
            StrategySignalId = strategySignalId,
            StrategyDecisionOutcome = "Persisted",
            ExecutionRequestStatus = "Prepared",
            BlockerSummary = "Allowed: execution request prepared.",
            GuardSummary = "ExecutionGate=Allowed",
            CorrelationId = "corr-audit-int-1",
            CompletedAtUtc = now.AddSeconds(30)
        });

        dbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = executionOrderId,
            OwnerUserId = "user-ops-1",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = strategySignalId,
            SignalType = StrategySignalType.Entry,
            StrategyKey = "strategy-audit-int",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 1m,
            Price = 100m,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            State = ExecutionOrderState.Submitted,
            IdempotencyKey = "audit-int-idempotency",
            RootCorrelationId = "corr-audit-int-1",
            LastStateChangedAtUtc = now.AddMinutes(1)
        });

        dbContext.ExecutionOrderTransitions.Add(new ExecutionOrderTransition
        {
            Id = Guid.NewGuid(),
            ExecutionOrderId = executionOrderId,
            OwnerUserId = "user-ops-1",
            SequenceNumber = 1,
            State = ExecutionOrderState.Submitted,
            EventCode = "Submitted",
            Detail = "Execution submitted to exchange.",
            CorrelationId = "step-local-audit-int-1",
            ParentCorrelationId = "step-parent-audit-int-1",
            OccurredAtUtc = now.AddMinutes(1)
        });

        dbContext.AdminAuditLogs.Add(new AdminAuditLog
        {
            Id = Guid.NewGuid(),
            ActorUserId = "super-admin",
            ActionType = "Admin.Settings.GlobalSystemState.Update",
            TargetType = "GlobalSystemState",
            TargetId = "Singleton",
            OldValueSummary = "State=Active",
            NewValueSummary = "State=MaintenancePending",
            Reason = "Maintenance approval required",
            CorrelationId = "corr-audit-int-1",
            CreatedAtUtc = now.AddMinutes(2)
        });

        dbContext.ApprovalQueues.Add(new ApprovalQueue
        {
            Id = approvalQueueId,
            ApprovalReference = "APR-audit-int-1",
            OperationType = ApprovalQueueOperationType.GlobalSystemStateUpdate,
            Status = ApprovalQueueStatus.Pending,
            Severity = IncidentSeverity.Critical,
            Title = "Maintenance approval",
            Summary = "Awaiting final approval.",
            TargetType = "GlobalSystemState",
            TargetId = "Singleton",
            RequestedByUserId = "requestor-ops-1",
            RequiredApprovals = 2,
            ApprovalCount = 1,
            ExpiresAtUtc = now.AddHours(1),
            Reason = "Maintenance required",
            PayloadJson = "{\"state\":\"Maintenance\"}",
            PayloadHash = "hash-audit-int-1",
            CorrelationId = "corr-audit-int-1",
            CommandId = "cmd-audit-int-1",
            DecisionId = "dec-audit-int-1",
            ExecutionAttemptId = "exe-audit-int-1",
            IncidentId = incidentId,
            IncidentReference = "INC-audit-int-1",
            LastActorUserId = "approver-ops-1",
            CreatedDate = now.AddMinutes(2),
            UpdatedDate = now.AddMinutes(2)
        });

        dbContext.ApprovalActions.Add(new ApprovalAction
        {
            Id = Guid.NewGuid(),
            ApprovalQueueId = approvalQueueId,
            ApprovalReference = "APR-audit-int-1",
            ActionType = ApprovalActionType.Approved,
            Sequence = 1,
            ActorUserId = "approver-ops-1",
            Reason = "Looks good",
            CorrelationId = "corr-audit-int-1",
            CommandId = "cmd-audit-int-1",
            DecisionId = "dec-audit-int-1",
            ExecutionAttemptId = "exe-audit-int-1",
            IncidentId = incidentId,
            IncidentReference = "INC-audit-int-1",
            CreatedDate = now.AddMinutes(3),
            UpdatedDate = now.AddMinutes(3)
        });

        dbContext.Incidents.Add(new Incident
        {
            Id = incidentId,
            IncidentReference = "INC-audit-int-1",
            Severity = IncidentSeverity.Critical,
            Status = IncidentStatus.PendingApproval,
            OperationType = ApprovalQueueOperationType.GlobalSystemStateUpdate,
            Title = "Maintenance incident",
            Summary = "Pending approval for maintenance.",
            Detail = "Detailed incident chain.",
            TargetType = "GlobalSystemState",
            TargetId = "Singleton",
            CorrelationId = "corr-audit-int-1",
            CommandId = "cmd-audit-int-1",
            DecisionId = "dec-audit-int-1",
            ExecutionAttemptId = "exe-audit-int-1",
            ApprovalQueueId = approvalQueueId,
            ApprovalReference = "APR-audit-int-1",
            CreatedByUserId = "requestor-ops-1",
            CreatedDate = now.AddMinutes(2),
            UpdatedDate = now.AddMinutes(4)
        });

        dbContext.IncidentEvents.AddRange(
            new IncidentEvent
            {
                Id = Guid.NewGuid(),
                IncidentId = incidentId,
                IncidentReference = "INC-audit-int-1",
                EventType = IncidentEventType.IncidentCreated,
                Message = "Incident created",
                ActorUserId = "requestor-ops-1",
                CorrelationId = "corr-audit-int-1",
                CommandId = "cmd-audit-int-1",
                DecisionId = "dec-audit-int-1",
                ExecutionAttemptId = "exe-audit-int-1",
                ApprovalQueueId = approvalQueueId,
                ApprovalReference = "APR-audit-int-1",
                PayloadJson = "{}",
                CreatedDate = now.AddMinutes(2),
                UpdatedDate = now.AddMinutes(2)
            },
            new IncidentEvent
            {
                Id = Guid.NewGuid(),
                IncidentId = incidentId,
                IncidentReference = "INC-audit-int-1",
                EventType = IncidentEventType.ApprovalQueued,
                Message = "Approval queued",
                ActorUserId = "requestor-ops-1",
                CorrelationId = "corr-audit-int-1",
                CommandId = "cmd-audit-int-1",
                DecisionId = "dec-audit-int-1",
                ExecutionAttemptId = "exe-audit-int-1",
                ApprovalQueueId = approvalQueueId,
                ApprovalReference = "APR-audit-int-1",
                PayloadJson = "{}",
                CreatedDate = now.AddMinutes(4),
                UpdatedDate = now.AddMinutes(4)
            });

        await dbContext.SaveChangesAsync();

        var logCenterService = new LogCenterReadModelService(
            dbContext,
            Options.Create(new LogCenterRetentionOptions()),
            NullLogger<LogCenterReadModelService>.Instance);
        var traceService = new TraceService(dbContext, new CorrelationContextAccessor(), new FixedTimeProvider(now));
        var governanceReadModelService = new AdminGovernanceReadModelService(dbContext);

        var snapshot = await logCenterService.GetPageAsync(
            new LogCenterQueryRequest(
                null,
                "corr-audit-int-1",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                50));
        var traceDetail = await traceService.GetDetailAsync("corr-audit-int-1", "dec-audit-int-1", "exe-audit-int-1");
        var incidentDetail = await governanceReadModelService.GetIncidentDetailAsync("INC-audit-int-1");
        var approvalDetail = new ApprovalQueueDetailSnapshot(
            "APR-audit-int-1",
            ApprovalQueueOperationType.GlobalSystemStateUpdate,
            ApprovalQueueStatus.Pending,
            IncidentSeverity.Critical,
            "Maintenance approval",
            "Awaiting final approval.",
            "GlobalSystemState",
            "Singleton",
            "requestor-ops-1",
            "Maintenance required",
            "{\"state\":\"Maintenance\"}",
            2,
            1,
            now.AddHours(1),
            "corr-audit-int-1",
            "cmd-audit-int-1",
            "dec-audit-int-1",
            "exe-audit-int-1",
            "INC-audit-int-1",
            null,
            null,
            now.AddMinutes(2),
            now.AddMinutes(2),
            null,
            null,
            null,
            null,
            null,
            null,
            [
                new ApprovalActionSnapshot(
                    1,
                    ApprovalActionType.Approved,
                    "approver-ops-1",
                    "Looks good",
                    "corr-audit-int-1",
                    "cmd-audit-int-1",
                    "dec-audit-int-1",
                    "exe-audit-int-1",
                    now.AddMinutes(3))
            ]);

        var model = AdminIncidentAuditDecisionCenterComposer.Compose(
            snapshot,
            outcome: null,
            reasonCode: null,
            focusReference: "APR-audit-int-1",
            traceDetail,
            approvalDetail,
            incidentDetail,
            now.AddMinutes(5));

        Assert.True(model.Rows.Count >= 6);
        Assert.Contains(model.Rows, item => item.OutcomeLabel == "Block");
        Assert.Contains(model.Rows, item => item.ReasonCode == "MarketDataLatencyBreached");
        Assert.Equal("APR-audit-int-1", model.Detail.Reference);
        Assert.Equal("Approvals=1/2; Status=Pending", model.Detail.BeforeSummary);
        Assert.Contains("Pending", model.Detail.AfterSummary);
        Assert.Equal(4, model.Detail.DecisionExecutionTrace.Count);
        Assert.Single(model.Detail.ApprovalHistory);
        Assert.Equal(2, model.Detail.IncidentTimeline.Count);
        Assert.Single(model.Detail.AdminAuditTrail);
        Assert.Equal("approver-ops-1", model.Detail.ApprovalHistory.Single().ChangedBy);

        await dbContext.Database.EnsureDeletedAsync();
    }

    private static string ResolveConnectionString(string databaseName) => SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        private readonly DateTimeOffset current = new(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));

        public override DateTimeOffset GetUtcNow() => current;
    }
}
