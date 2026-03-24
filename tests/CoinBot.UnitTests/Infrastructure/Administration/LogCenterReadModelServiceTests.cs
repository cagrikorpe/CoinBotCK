using CoinBot.Application.Abstractions.Administration;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Administration;

public sealed class LogCenterReadModelServiceTests
{
    [Fact]
    public async Task GetPageAsync_ReturnsCombinedEntries_AndMasksRawJson()
    {
        await using var dbContext = CreateDbContext();
        var now = new DateTime(2026, 3, 24, 12, 0, 0, DateTimeKind.Utc);
        await SeedCombinedEntriesAsync(dbContext, now);

        var service = CreateService(dbContext);
        var snapshot = await service.GetPageAsync(
            new LogCenterQueryRequest(null, null, null, null, null, null, null, null, null, 100));

        Assert.False(snapshot.HasError);
        Assert.Equal(7, snapshot.Summary.TotalRows);
        Assert.Equal(1, snapshot.Summary.DecisionTraceRows);
        Assert.Equal(1, snapshot.Summary.ExecutionTraceRows);
        Assert.Equal(1, snapshot.Summary.AdminAuditLogRows);
        Assert.Equal(1, snapshot.Summary.IncidentRows);
        Assert.Equal(1, snapshot.Summary.IncidentEventRows);
        Assert.Equal(1, snapshot.Summary.ApprovalQueueRows);
        Assert.Equal(1, snapshot.Summary.ApprovalActionRows);
        Assert.Equal(7, snapshot.Entries.Count);
        Assert.Contains("LogCenter.Retention.Completed", snapshot.Retention.LastRunSummary, StringComparison.Ordinal);

        Assert.All(snapshot.Entries, entry =>
        {
            Assert.DoesNotContain("plain-secret", entry.RawJson ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("plain-key", entry.RawJson ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("plain-token", entry.RawJson ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("abc123", entry.RawJson ?? string.Empty, StringComparison.Ordinal);
        });

        Assert.Contains(snapshot.Entries, entry => entry.RawJson?.Contains("***REDACTED***", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task GetPageAsync_FiltersByCorrelationDecisionExecutionStatusAndDateRange()
    {
        await using var dbContext = CreateDbContext();
        var hitDate = new DateTime(2026, 3, 24, 12, 0, 0, DateTimeKind.Utc);
        var oldDate = new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);
        await SeedFilterEntriesAsync(dbContext, hitDate, oldDate);

        var service = CreateService(dbContext);

        var correlationSnapshot = await service.GetPageAsync(
            new LogCenterQueryRequest(
                Query: null,
                CorrelationId: "corr-hit",
                DecisionId: null,
                ExecutionAttemptId: null,
                UserId: "user-hit",
                Symbol: "BTCUSDT",
                Status: null,
                FromUtc: hitDate,
                ToUtc: hitDate,
                Take: 100));

        Assert.False(correlationSnapshot.HasError);
        Assert.Equal(2, correlationSnapshot.Summary.TotalRows);
        Assert.Equal(1, correlationSnapshot.Summary.DecisionTraceRows);
        Assert.Equal(1, correlationSnapshot.Summary.ExecutionTraceRows);
        Assert.Equal(2, correlationSnapshot.Entries.Count);

        var decisionSnapshot = await service.GetPageAsync(
            new LogCenterQueryRequest(
                Query: "dec-hit",
                CorrelationId: null,
                DecisionId: "dec-hit",
                ExecutionAttemptId: null,
                UserId: null,
                Symbol: null,
                Status: null,
                FromUtc: hitDate,
                ToUtc: hitDate,
                Take: 100));

        Assert.Equal(1, decisionSnapshot.Summary.DecisionTraceRows);
        Assert.Single(decisionSnapshot.Entries);
        Assert.Equal("dec-hit", decisionSnapshot.Entries.Single().DecisionId);

        var executionSnapshot = await service.GetPageAsync(
            new LogCenterQueryRequest(
                Query: "exe-hit",
                CorrelationId: null,
                DecisionId: null,
                ExecutionAttemptId: "exe-hit",
                UserId: null,
                Symbol: null,
                Status: "success",
                FromUtc: hitDate,
                ToUtc: hitDate,
                Take: 100));

        Assert.Equal(1, executionSnapshot.Summary.ExecutionTraceRows);
        Assert.Single(executionSnapshot.Entries);
        Assert.Equal("exe-hit", executionSnapshot.Entries.Single().ExecutionAttemptId);
    }

    private static LogCenterReadModelService CreateService(ApplicationDbContext dbContext)
    {
        return new LogCenterReadModelService(
            dbContext,
            Options.Create(new LogCenterRetentionOptions()),
            NullLogger<LogCenterReadModelService>.Instance);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private static async Task SeedCombinedEntriesAsync(ApplicationDbContext dbContext, DateTime now)
    {
        var retentionAuditLog = new AdminAuditLog
        {
            Id = Guid.NewGuid(),
            ActorUserId = "system:log-retention",
            ActionType = "LogCenter.Retention.Completed",
            TargetType = "LogCenterRetention",
            Reason = "Retention run completed.",
            OldValueSummary = "apiSecret=plain-secret",
            NewValueSummary = "signature=abc123",
            CorrelationId = "corr-hit",
            CreatedAtUtc = now
        };

        var decisionTrace = new DecisionTrace
        {
            Id = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            CorrelationId = "corr-hit",
            DecisionId = "dec-hit",
            UserId = "user-hit",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            StrategyVersion = "StrategyVersion:test",
            SignalType = "Entry",
            RiskScore = 72,
            DecisionOutcome = "Persisted",
            VetoReasonCode = null,
            LatencyMs = 12,
            SnapshotJson = """
            {
              "endpoint": "/fapi/v1/order?symbol=BTCUSDT&signature=abc123&apiKey=plain-key",
              "secret": "plain-secret",
              "nested": {
                "authorization": "Bearer plain-token"
              }
            }
            """,
            CreatedAtUtc = now,
            UpdatedDate = now
        };

        var executionTrace = new ExecutionTrace
        {
            Id = Guid.NewGuid(),
            CorrelationId = "corr-hit",
            ExecutionAttemptId = "exe-hit",
            CommandId = "cmd-hit",
            UserId = "user-hit",
            Provider = "Binance.PrivateRest",
            Endpoint = "/fapi/v1/order?symbol=BTCUSDT&signature=abc123&apiKey=plain-key",
            RequestMasked = """
            {
              "endpoint": "/fapi/v1/order?symbol=BTCUSDT&signature=abc123&apiKey=plain-key",
              "headers": {
                "X-MBX-APIKEY": "plain-key",
                "Authorization": "Bearer plain-token"
              }
            }
            """,
            ResponseMasked = """
            {
              "signature": "abc123",
              "apiSecret": "plain-secret",
              "message": "ok"
            }
            """,
            HttpStatusCode = 200,
            ExchangeCode = "OK",
            LatencyMs = 18,
            CreatedAtUtc = now,
            UpdatedDate = now
        };

        var incident = new Incident
        {
            Id = Guid.NewGuid(),
            IncidentReference = "INC-hit",
            Severity = IncidentSeverity.Critical,
            Status = IncidentStatus.Resolved,
            OperationType = ApprovalQueueOperationType.CrisisEscalationExecute,
            Title = "Emergency flatten",
            Summary = "Flatten completed",
            Detail = "Detailed incident payload",
            TargetType = "Crisis",
            TargetId = "GLOBAL_FLATTEN",
            CorrelationId = "corr-hit",
            CommandId = "cmd-hit",
            DecisionId = "dec-hit",
            ExecutionAttemptId = "exe-hit",
            ApprovalReference = "APR-hit",
            SystemStateHistoryReference = "GST-hit",
            DependencyCircuitBreakerStateReference = "BREAKER-hit",
            CreatedByUserId = "user-hit",
            ResolvedAtUtc = now.AddMinutes(5),
            ResolvedByUserId = "user-hit",
            ResolvedSummary = "Incident resolved",
            CreatedDate = now,
            UpdatedDate = now
        };

        var incidentEvent = new IncidentEvent
        {
            Id = Guid.NewGuid(),
            IncidentId = incident.Id,
            IncidentReference = incident.IncidentReference,
            EventType = IncidentEventType.TraceLinked,
            Message = "trace linked",
            ActorUserId = "user-hit",
            CorrelationId = "corr-hit",
            CommandId = "cmd-hit",
            DecisionId = "dec-hit",
            ExecutionAttemptId = "exe-hit",
            ApprovalReference = "APR-hit",
            SystemStateHistoryReference = "GST-hit",
            DependencyCircuitBreakerStateReference = "BREAKER-hit",
            PayloadJson = """
            {
              "apiSecret": "plain-secret"
            }
            """,
            CreatedDate = now,
            UpdatedDate = now
        };

        var approvalQueue = new ApprovalQueue
        {
            Id = Guid.NewGuid(),
            ApprovalReference = "APR-hit",
            OperationType = ApprovalQueueOperationType.GlobalSystemStateUpdate,
            Status = ApprovalQueueStatus.Pending,
            Severity = IncidentSeverity.Warning,
            Title = "Global system state update",
            Summary = "Maintenance request",
            TargetType = "GlobalSystemState",
            TargetId = "Singleton",
            RequestedByUserId = "user-hit",
            RequiredApprovals = 2,
            ApprovalCount = 1,
            ExpiresAtUtc = now.AddHours(1),
            Reason = "Maintenance required",
            PayloadJson = """
            {
              "apiSecret": "plain-secret"
            }
            """,
            PayloadHash = "hash-hit",
            CorrelationId = "corr-hit",
            CommandId = "cmd-hit",
            DecisionId = "dec-hit",
            ExecutionAttemptId = "exe-hit",
            IncidentReference = incident.IncidentReference,
            SystemStateHistoryReference = "GST-hit",
            DependencyCircuitBreakerStateReference = "BREAKER-hit",
            CreatedDate = now,
            UpdatedDate = now
        };

        var approvalAction = new ApprovalAction
        {
            Id = Guid.NewGuid(),
            ApprovalQueueId = approvalQueue.Id,
            ApprovalReference = approvalQueue.ApprovalReference,
            ActionType = ApprovalActionType.Approved,
            Sequence = 1,
            ActorUserId = "user-hit",
            Reason = "approve plain-secret",
            CorrelationId = "corr-hit",
            CommandId = "cmd-hit",
            DecisionId = "dec-hit",
            ExecutionAttemptId = "exe-hit",
            IncidentReference = incident.IncidentReference,
            SystemStateHistoryReference = "GST-hit",
            DependencyCircuitBreakerStateReference = "BREAKER-hit",
            CreatedDate = now,
            UpdatedDate = now
        };

        dbContext.AddRange(retentionAuditLog, decisionTrace, executionTrace, incident, incidentEvent, approvalQueue, approvalAction);
        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedFilterEntriesAsync(ApplicationDbContext dbContext, DateTime hitDate, DateTime oldDate)
    {
        var hitDecision = new DecisionTrace
        {
            Id = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            CorrelationId = "corr-hit",
            DecisionId = "dec-hit",
            UserId = "user-hit",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            StrategyVersion = "StrategyVersion:test",
            SignalType = "Entry",
            RiskScore = 72,
            DecisionOutcome = "Persisted",
            LatencyMs = 12,
            SnapshotJson = "{\"secret\":\"plain-secret\"}",
            CreatedAtUtc = hitDate,
            UpdatedDate = hitDate
        };

        var oldDecision = new DecisionTrace
        {
            Id = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            CorrelationId = "corr-old",
            DecisionId = "dec-old",
            UserId = "user-old",
            Symbol = "ETHUSDT",
            Timeframe = "5m",
            StrategyVersion = "StrategyVersion:old",
            SignalType = "Entry",
            RiskScore = 35,
            DecisionOutcome = "Persisted",
            LatencyMs = 22,
            SnapshotJson = "{\"secret\":\"plain-secret\"}",
            CreatedAtUtc = oldDate,
            UpdatedDate = oldDate
        };

        var hitExecution = new ExecutionTrace
        {
            Id = Guid.NewGuid(),
            CorrelationId = "corr-hit",
            ExecutionAttemptId = "exe-hit",
            CommandId = "cmd-hit",
            UserId = "user-hit",
            Provider = "Binance.PrivateRest",
            Endpoint = "/fapi/v1/order",
            RequestMasked = "{\"secret\":\"plain-secret\"}",
            ResponseMasked = "{\"secret\":\"plain-secret\"}",
            HttpStatusCode = 200,
            ExchangeCode = "OK",
            LatencyMs = 18,
            CreatedAtUtc = hitDate,
            UpdatedDate = hitDate
        };

        var oldExecution = new ExecutionTrace
        {
            Id = Guid.NewGuid(),
            CorrelationId = "corr-old",
            ExecutionAttemptId = "exe-old",
            CommandId = "cmd-old",
            UserId = "user-old",
            Provider = "Binance.PrivateRest",
            Endpoint = "/fapi/v1/order",
            RequestMasked = "{\"secret\":\"plain-secret\"}",
            ResponseMasked = "{\"secret\":\"plain-secret\"}",
            HttpStatusCode = 500,
            ExchangeCode = "ERR",
            LatencyMs = 28,
            CreatedAtUtc = oldDate,
            UpdatedDate = oldDate
        };

        dbContext.AddRange(hitDecision, oldDecision, hitExecution, oldExecution);
        await dbContext.SaveChangesAsync();
    }

    private sealed class TestDataScopeContext : CoinBot.Application.Abstractions.DataScope.IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }
}
