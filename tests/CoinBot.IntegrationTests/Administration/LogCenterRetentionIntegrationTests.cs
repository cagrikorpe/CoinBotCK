using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.IntegrationTests.Administration;

public sealed class LogCenterRetentionIntegrationTests
{
    [Fact]
    public async Task ApplyAsync_PurgesOnlyTerminalAndExpiredRecords_OnSqlServer()
    {
        var databaseName = $"CoinBotLogRetentionInt_{Guid.NewGuid():N}";
        var connectionString = ResolveConnectionString(databaseName);
        var now = new DateTime(2026, 3, 24, 12, 0, 0, DateTimeKind.Utc);
        var oldDate = now.AddDays(-60);

        await using var dbContext = CreateDbContext(connectionString);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

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
            CreatedAtUtc = now.AddDays(-60),
            UpdatedDate = now.AddDays(-60)
        };

        var activeDecision = new DecisionTrace
        {
            Id = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            CorrelationId = "corr-active",
            DecisionId = "dec-active",
            UserId = "user-active",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            StrategyVersion = "StrategyVersion:active",
            SignalType = "Entry",
            RiskScore = 72,
            DecisionOutcome = "Persisted",
            LatencyMs = 12,
            SnapshotJson = "{\"secret\":\"plain-secret\"}",
            CreatedAtUtc = now,
            UpdatedDate = now
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
            CreatedAtUtc = now.AddDays(-60),
            UpdatedDate = now.AddDays(-60)
        };

        var activeExecution = new ExecutionTrace
        {
            Id = Guid.NewGuid(),
            CorrelationId = "corr-active",
            ExecutionAttemptId = "exe-active",
            CommandId = "cmd-active",
            UserId = "user-active",
            Provider = "Binance.PrivateRest",
            Endpoint = "/fapi/v1/order",
            RequestMasked = "{\"secret\":\"plain-secret\"}",
            ResponseMasked = "{\"secret\":\"plain-secret\"}",
            HttpStatusCode = 200,
            ExchangeCode = "OK",
            LatencyMs = 18,
            CreatedAtUtc = now,
            UpdatedDate = now
        };

        var oldAuditLog = new AdminAuditLog
        {
            Id = Guid.NewGuid(),
            ActorUserId = "system:log-retention",
            ActionType = "LogCenter.Retention.Completed",
            TargetType = "LogCenterRetention",
            TargetId = null,
            OldValueSummary = "old=1",
            NewValueSummary = "new=2",
            Reason = "Old retention run",
            CorrelationId = "corr-old",
            CreatedAtUtc = now.AddDays(-60)
        };

        var oldIncident = new Incident
        {
            Id = Guid.NewGuid(),
            IncidentReference = "INC-old",
            Severity = IncidentSeverity.Critical,
            Status = IncidentStatus.Resolved,
            OperationType = ApprovalQueueOperationType.CrisisEscalationExecute,
            Title = "Old incident",
            Summary = "Resolved",
            Detail = "Old incident detail",
            TargetType = "Crisis",
            TargetId = "GLOBAL_FLATTEN",
            CorrelationId = "corr-old",
            CommandId = "cmd-old",
            DecisionId = "dec-old",
            ExecutionAttemptId = "exe-old",
            ApprovalReference = "APR-old",
            CreatedByUserId = "user-old",
            ResolvedAtUtc = now.AddDays(-55),
            ResolvedByUserId = "user-old",
            ResolvedSummary = "Resolved",
            CreatedDate = oldDate,
            UpdatedDate = oldDate
        };

        var oldIncidentEvent = new IncidentEvent
        {
            Id = Guid.NewGuid(),
            IncidentId = oldIncident.Id,
            IncidentReference = oldIncident.IncidentReference,
            EventType = IncidentEventType.TraceLinked,
            Message = "trace linked",
            ActorUserId = "user-old",
            CorrelationId = "corr-old",
            CommandId = "cmd-old",
            DecisionId = "dec-old",
            ExecutionAttemptId = "exe-old",
            ApprovalReference = "APR-old",
            PayloadJson = "{\"secret\":\"plain-secret\"}",
            CreatedDate = oldDate,
            UpdatedDate = oldDate
        };

        var activeIncident = new Incident
        {
            Id = Guid.NewGuid(),
            IncidentReference = "INC-active",
            Severity = IncidentSeverity.Warning,
            Status = IncidentStatus.Open,
            OperationType = ApprovalQueueOperationType.GlobalSystemStateUpdate,
            Title = "Active incident",
            Summary = "Monitoring",
            Detail = "Active incident detail",
            TargetType = "GlobalSystemState",
            TargetId = "Singleton",
            CorrelationId = "corr-active",
            CommandId = "cmd-active",
            DecisionId = "dec-active",
            ExecutionAttemptId = "exe-active",
            CreatedByUserId = "user-active",
            CreatedDate = now,
            UpdatedDate = now
        };

        var activeIncidentEvent = new IncidentEvent
        {
            Id = Guid.NewGuid(),
            IncidentId = activeIncident.Id,
            IncidentReference = activeIncident.IncidentReference,
            EventType = IncidentEventType.IncidentCreated,
            Message = "created",
            ActorUserId = "user-active",
            CorrelationId = "corr-active",
            CommandId = "cmd-active",
            DecisionId = "dec-active",
            ExecutionAttemptId = "exe-active",
            CreatedDate = now,
            UpdatedDate = now
        };

        var oldApprovalQueue = new ApprovalQueue
        {
            Id = Guid.NewGuid(),
            ApprovalReference = "APR-old",
            OperationType = ApprovalQueueOperationType.GlobalSystemStateUpdate,
            Status = ApprovalQueueStatus.Approved,
            Severity = IncidentSeverity.Warning,
            Title = "Old approval",
            Summary = "Old approval queue",
            TargetType = "GlobalSystemState",
            TargetId = "Singleton",
            RequestedByUserId = "user-old",
            RequiredApprovals = 2,
            ApprovalCount = 2,
            ExpiresAtUtc = now.AddDays(-59),
            Reason = "Old approval",
            PayloadJson = "{\"secret\":\"plain-secret\"}",
            PayloadHash = "hash-old",
            CorrelationId = "corr-old",
            CommandId = "cmd-old",
            DecisionId = "dec-old",
            ExecutionAttemptId = "exe-old",
            IncidentReference = oldIncident.IncidentReference,
            ApprovedAtUtc = now.AddDays(-59),
            CreatedDate = oldDate,
            UpdatedDate = oldDate
        };

        var oldApprovalAction = new ApprovalAction
        {
            Id = Guid.NewGuid(),
            ApprovalQueueId = oldApprovalQueue.Id,
            ApprovalReference = oldApprovalQueue.ApprovalReference,
            ActionType = ApprovalActionType.Approved,
            Sequence = 1,
            ActorUserId = "user-old",
            Reason = "old approval action",
            CorrelationId = "corr-old",
            CommandId = "cmd-old",
            DecisionId = "dec-old",
            ExecutionAttemptId = "exe-old",
            IncidentReference = oldIncident.IncidentReference,
            CreatedDate = oldDate,
            UpdatedDate = oldDate
        };

        var activeApprovalQueue = new ApprovalQueue
        {
            Id = Guid.NewGuid(),
            ApprovalReference = "APR-active",
            OperationType = ApprovalQueueOperationType.GlobalSystemStateUpdate,
            Status = ApprovalQueueStatus.Pending,
            Severity = IncidentSeverity.Warning,
            Title = "Active approval",
            Summary = "Active approval queue",
            TargetType = "GlobalSystemState",
            TargetId = "Singleton",
            RequestedByUserId = "user-active",
            RequiredApprovals = 2,
            ApprovalCount = 1,
            ExpiresAtUtc = now.AddDays(10),
            Reason = "Active approval",
            PayloadJson = "{\"secret\":\"plain-secret\"}",
            PayloadHash = "hash-active",
            CorrelationId = "corr-active",
            CommandId = "cmd-active",
            DecisionId = "dec-active",
            ExecutionAttemptId = "exe-active",
            IncidentReference = activeIncident.IncidentReference,
            CreatedDate = now,
            UpdatedDate = now
        };

        var activeApprovalAction = new ApprovalAction
        {
            Id = Guid.NewGuid(),
            ApprovalQueueId = activeApprovalQueue.Id,
            ApprovalReference = activeApprovalQueue.ApprovalReference,
            ActionType = ApprovalActionType.Approved,
            Sequence = 1,
            ActorUserId = "user-active",
            Reason = "active approval action",
            CorrelationId = "corr-active",
            CommandId = "cmd-active",
            DecisionId = "dec-active",
            ExecutionAttemptId = "exe-active",
            IncidentReference = activeIncident.IncidentReference,
            CreatedDate = now,
            UpdatedDate = now
        };

        dbContext.AddRange(
            oldDecision,
            activeDecision,
            oldExecution,
            activeExecution,
            oldAuditLog,
            oldIncident,
            oldIncidentEvent,
            activeIncident,
            activeIncidentEvent,
            oldApprovalQueue,
            oldApprovalAction,
            activeApprovalQueue,
            activeApprovalAction);

        await dbContext.SaveChangesAsync();

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE Incidents
            SET CreatedDate = {oldDate}, UpdatedDate = {oldDate}
            WHERE Id = {oldIncident.Id};

            UPDATE IncidentEvents
            SET CreatedDate = {oldDate}, UpdatedDate = {oldDate}
            WHERE Id = {oldIncidentEvent.Id};

            UPDATE ApprovalQueues
            SET CreatedDate = {oldDate}, UpdatedDate = {oldDate}
            WHERE Id = {oldApprovalQueue.Id};

            UPDATE ApprovalActions
            SET CreatedDate = {oldDate}, UpdatedDate = {oldDate}
            WHERE Id = {oldApprovalAction.Id};
            """);

        var timeProvider = new FixedTimeProvider(now);
        var auditService = new AdminAuditLogService(dbContext, new CorrelationContextAccessor(), timeProvider);
        var retentionService = new LogCenterRetentionService(
            dbContext,
            auditService,
            Options.Create(new LogCenterRetentionOptions
            {
                Enabled = true,
                DecisionTraceRetentionDays = 30,
                ExecutionTraceRetentionDays = 30,
                AdminAuditLogRetentionDays = 30,
                IncidentRetentionDays = 30,
                ApprovalRetentionDays = 30,
                BatchSize = 100
            }),
            timeProvider,
            NullLogger<LogCenterRetentionService>.Instance);

        var runSnapshot = await retentionService.ApplyAsync();

        Assert.Equal(1, runSnapshot.DecisionTraceCount);
        Assert.Equal(1, runSnapshot.ExecutionTraceCount);
        Assert.Equal(1, runSnapshot.AdminAuditLogCount);
        Assert.Equal(1, runSnapshot.IncidentCount);
        Assert.Equal(1, runSnapshot.IncidentEventCount);
        Assert.Equal(1, runSnapshot.ApprovalQueueCount);
        Assert.Equal(1, runSnapshot.ApprovalActionCount);

        Assert.DoesNotContain(dbContext.DecisionTraces, entity => entity.DecisionId == "dec-old");
        Assert.DoesNotContain(dbContext.ExecutionTraces, entity => entity.ExecutionAttemptId == "exe-old");
        Assert.DoesNotContain(dbContext.AdminAuditLogs, entity => entity.CorrelationId == "corr-old" && entity.ActionType == "LogCenter.Retention.Completed" && entity.Reason == "Old retention run");
        Assert.DoesNotContain(dbContext.Incidents, entity => entity.IncidentReference == "INC-old");
        Assert.DoesNotContain(dbContext.IncidentEvents, entity => entity.IncidentReference == "INC-old");
        Assert.DoesNotContain(dbContext.ApprovalQueues, entity => entity.ApprovalReference == "APR-old");
        Assert.DoesNotContain(dbContext.ApprovalActions, entity => entity.ApprovalReference == "APR-old");

        Assert.True(dbContext.DecisionTraces.Any(entity => entity.DecisionId == "dec-active"));
        Assert.True(dbContext.ExecutionTraces.Any(entity => entity.ExecutionAttemptId == "exe-active"));
        Assert.True(dbContext.Incidents.Any(entity => entity.IncidentReference == "INC-active"));
        Assert.True(dbContext.IncidentEvents.Any(entity => entity.IncidentReference == "INC-active"));
        Assert.True(dbContext.ApprovalQueues.Any(entity => entity.ApprovalReference == "APR-active"));
        Assert.True(dbContext.ApprovalActions.Any(entity => entity.ApprovalReference == "APR-active"));
    }

    [Fact]
    public async Task ApplyAsync_DoesNotPurgeOldOpenIncidentsOrPendingApprovals_OnSqlServer()
    {
        var databaseName = $"CoinBotLogRetentionProtectedInt_{Guid.NewGuid():N}";
        var connectionString = ResolveConnectionString(databaseName);
        var now = new DateTime(2026, 3, 24, 12, 0, 0, DateTimeKind.Utc);
        var oldDate = now.AddDays(-60);

        await using var dbContext = CreateDbContext(connectionString);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        var openIncident = new Incident
        {
            Id = Guid.NewGuid(),
            IncidentReference = "INC-protected",
            Severity = IncidentSeverity.Warning,
            Status = IncidentStatus.Open,
            OperationType = ApprovalQueueOperationType.GlobalSystemStateUpdate,
            Title = "Protected incident",
            Summary = "Still active",
            Detail = "Old but not terminal",
            TargetType = "GlobalSystemState",
            TargetId = "Singleton",
            CorrelationId = "corr-protected",
            CommandId = "cmd-protected",
            DecisionId = "dec-protected",
            ExecutionAttemptId = "exe-protected",
            CreatedByUserId = "user-protected",
            CreatedDate = oldDate,
            UpdatedDate = oldDate
        };

        var openIncidentEvent = new IncidentEvent
        {
            Id = Guid.NewGuid(),
            IncidentId = openIncident.Id,
            IncidentReference = openIncident.IncidentReference,
            EventType = IncidentEventType.IncidentCreated,
            Message = "protected event",
            ActorUserId = "user-protected",
            CorrelationId = "corr-protected",
            CommandId = "cmd-protected",
            DecisionId = "dec-protected",
            ExecutionAttemptId = "exe-protected",
            CreatedDate = oldDate,
            UpdatedDate = oldDate
        };

        var pendingApprovalQueue = new ApprovalQueue
        {
            Id = Guid.NewGuid(),
            ApprovalReference = "APR-protected",
            OperationType = ApprovalQueueOperationType.GlobalSystemStateUpdate,
            Status = ApprovalQueueStatus.Pending,
            Severity = IncidentSeverity.Warning,
            Title = "Protected approval",
            Summary = "Still pending",
            TargetType = "GlobalSystemState",
            TargetId = "Singleton",
            RequestedByUserId = "user-protected",
            RequiredApprovals = 2,
            ApprovalCount = 1,
            ExpiresAtUtc = oldDate.AddHours(1),
            Reason = "Old but active",
            PayloadJson = "{\"secret\":\"plain-secret\"}",
            PayloadHash = "hash-protected",
            CorrelationId = "corr-protected",
            CommandId = "cmd-protected",
            DecisionId = "dec-protected",
            ExecutionAttemptId = "exe-protected",
            IncidentReference = openIncident.IncidentReference,
            CreatedDate = oldDate,
            UpdatedDate = oldDate
        };

        var pendingApprovalAction = new ApprovalAction
        {
            Id = Guid.NewGuid(),
            ApprovalQueueId = pendingApprovalQueue.Id,
            ApprovalReference = pendingApprovalQueue.ApprovalReference,
            ActionType = ApprovalActionType.Approved,
            Sequence = 1,
            ActorUserId = "user-protected",
            Reason = "protected action",
            CorrelationId = "corr-protected",
            CommandId = "cmd-protected",
            DecisionId = "dec-protected",
            ExecutionAttemptId = "exe-protected",
            IncidentReference = openIncident.IncidentReference,
            CreatedDate = oldDate,
            UpdatedDate = oldDate
        };

        dbContext.AddRange(openIncident, openIncidentEvent, pendingApprovalQueue, pendingApprovalAction);
        await dbContext.SaveChangesAsync();

        var timeProvider = new FixedTimeProvider(now);
        var auditService = new AdminAuditLogService(dbContext, new CorrelationContextAccessor(), timeProvider);
        var retentionService = new LogCenterRetentionService(
            dbContext,
            auditService,
            Options.Create(new LogCenterRetentionOptions
            {
                Enabled = true,
                DecisionTraceRetentionDays = 30,
                ExecutionTraceRetentionDays = 30,
                AdminAuditLogRetentionDays = 30,
                IncidentRetentionDays = 30,
                ApprovalRetentionDays = 30,
                BatchSize = 100
            }),
            timeProvider,
            NullLogger<LogCenterRetentionService>.Instance);

        var runSnapshot = await retentionService.ApplyAsync();

        Assert.Equal(0, runSnapshot.IncidentCount);
        Assert.Equal(0, runSnapshot.IncidentEventCount);
        Assert.Equal(0, runSnapshot.ApprovalQueueCount);
        Assert.Equal(0, runSnapshot.ApprovalActionCount);

        Assert.True(dbContext.Incidents.Any(entity => entity.IncidentReference == "INC-protected"));
        Assert.True(dbContext.IncidentEvents.Any(entity => entity.IncidentReference == "INC-protected"));
        Assert.True(dbContext.ApprovalQueues.Any(entity => entity.ApprovalReference == "APR-protected"));
        Assert.True(dbContext.ApprovalActions.Any(entity => entity.ApprovalReference == "APR-protected"));
    }

    private static ApplicationDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private static string ResolveConnectionString(string databaseName)
    {
        return SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
