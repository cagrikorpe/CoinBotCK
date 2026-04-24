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
            new LogCenterQueryRequest(null, "corr-hit", null, null, null, null, null, null, null, 100));

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

    [Fact]
    public async Task GetPageAsync_FiltersBySymbolAcrossCombinedKinds()
    {
        await using var dbContext = CreateDbContext();
        var now = new DateTime(2026, 3, 24, 12, 0, 0, DateTimeKind.Utc);
        await SeedSymbolFilterEntriesAsync(dbContext, now);

        var service = CreateService(dbContext);
        var snapshot = await service.GetPageAsync(
            new LogCenterQueryRequest(
                Query: null,
                CorrelationId: "corr-hit",
                DecisionId: null,
                ExecutionAttemptId: null,
                UserId: "user-hit",
                Symbol: "btcusdt",
                Status: null,
                FromUtc: null,
                ToUtc: null,
                Take: 100));

        Assert.False(snapshot.HasError);
        Assert.Equal(7, snapshot.Summary.TotalRows);
        Assert.Equal(1, snapshot.Summary.DecisionTraceRows);
        Assert.Equal(1, snapshot.Summary.ExecutionTraceRows);
        Assert.Equal(1, snapshot.Summary.AdminAuditLogRows);
        Assert.Equal(1, snapshot.Summary.IncidentRows);
        Assert.Equal(1, snapshot.Summary.IncidentEventRows);
        Assert.Equal(1, snapshot.Summary.ApprovalQueueRows);
        Assert.Equal(1, snapshot.Summary.ApprovalActionRows);
        Assert.Equal(
            ["AdminAuditLog", "ApprovalAction", "ApprovalQueue", "DecisionTrace", "ExecutionTrace", "Incident", "IncidentEvent"],
            snapshot.Entries.Select(entry => entry.Kind).OrderBy(kind => kind, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task GetPageAsync_PreservesExactSummaryCounts_WhenTakeIsSmallerThanAvailableRows()
    {
        await using var dbContext = CreateDbContext();
        var now = new DateTime(2026, 4, 10, 8, 0, 0, DateTimeKind.Utc);

        dbContext.DecisionTraces.AddRange(
            Enumerable.Range(1, 3).Select(offset => new DecisionTrace
            {
                Id = Guid.NewGuid(),
                StrategySignalId = Guid.NewGuid(),
                CorrelationId = $"corr-many-{offset}",
                DecisionId = $"dec-many-{offset}",
                UserId = "user-many",
                Symbol = "BTCUSDT",
                Timeframe = "1m",
                StrategyVersion = "StrategyVersion:test",
                SignalType = "Entry",
                DecisionOutcome = "Persisted",
                DecisionReasonType = "Allow",
                DecisionReasonCode = "Allowed",
                DecisionSummary = "Strategy produced an executable candidate.",
                DecisionAtUtc = now.AddMinutes(offset),
                LatencyMs = 10 + offset,
                SnapshotJson = "{\"decision\":true}",
                CreatedAtUtc = now.AddMinutes(offset),
                UpdatedDate = now.AddMinutes(offset)
            }));
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        var snapshot = await service.GetPageAsync(
            new LogCenterQueryRequest(
                Query: null,
                CorrelationId: null,
                DecisionId: null,
                ExecutionAttemptId: null,
                UserId: null,
                Symbol: null,
                Status: null,
                FromUtc: now,
                ToUtc: now,
                Take: 1));

        Assert.False(snapshot.HasError);
        Assert.Equal(3, snapshot.Summary.TotalRows);
        Assert.Equal(3, snapshot.Summary.DecisionTraceRows);
        Assert.Single(snapshot.Entries);
        Assert.Equal("dec-many-3", snapshot.Entries.Single().DecisionId);
    }

    [Fact]
    public async Task GetPageAsync_QuerylessInitialLoad_UsesLatestBoundedPreviewWindow()
    {
        await using var dbContext = CreateDbContext();
        var now = DateTime.UtcNow;

        dbContext.DecisionTraces.AddRange(
            new DecisionTrace
            {
                Id = Guid.NewGuid(),
                StrategySignalId = Guid.NewGuid(),
                CorrelationId = "corr-preview-recent",
                DecisionId = "dec-preview-recent",
                UserId = "user-preview",
                Symbol = "BTCUSDT",
                Timeframe = "1m",
                StrategyVersion = "StrategyVersion:test",
                SignalType = "Entry",
                DecisionOutcome = "Persisted",
                DecisionReasonType = "Allow",
                DecisionReasonCode = "Allowed",
                DecisionSummary = "Recent candidate.",
                DecisionAtUtc = now.AddMinutes(-10),
                LatencyMs = 9,
                SnapshotJson = "{\"decision\":true}",
                CreatedAtUtc = now.AddMinutes(-10),
                UpdatedDate = now.AddMinutes(-10)
            },
            new DecisionTrace
            {
                Id = Guid.NewGuid(),
                StrategySignalId = Guid.NewGuid(),
                CorrelationId = "corr-preview-old",
                DecisionId = "dec-preview-old",
                UserId = "user-preview",
                Symbol = "BTCUSDT",
                Timeframe = "1m",
                StrategyVersion = "StrategyVersion:test",
                SignalType = "Entry",
                DecisionOutcome = "Persisted",
                DecisionReasonType = "Allow",
                DecisionReasonCode = "Allowed",
                DecisionSummary = "Old candidate.",
                DecisionAtUtc = now.AddHours(-8),
                LatencyMs = 7,
                SnapshotJson = "{\"decision\":true}",
                CreatedAtUtc = now.AddHours(-8),
                UpdatedDate = now.AddHours(-8)
            });
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        var snapshot = await service.GetPageAsync(
            new LogCenterQueryRequest(
                Query: null,
                CorrelationId: null,
                DecisionId: null,
                ExecutionAttemptId: null,
                UserId: null,
                Symbol: null,
                Status: null,
                FromUtc: null,
                ToUtc: null,
                Take: 100));

        Assert.False(snapshot.HasError);
        Assert.NotNull(snapshot.Filters.FromUtc);
        Assert.NotNull(snapshot.Filters.ToUtc);
        var previewHours = snapshot.Filters.ToUtc!.Value - snapshot.Filters.FromUtc!.Value;
        Assert.InRange(previewHours.TotalHours, 5.9d, 6.1d);
        Assert.Single(snapshot.Entries);
        Assert.Equal("dec-preview-recent", snapshot.Entries.Single().DecisionId);
        Assert.Equal(1, snapshot.Summary.TotalRows);
        Assert.Equal(1, snapshot.Summary.DecisionTraceRows);
    }

    [Fact]
    public async Task GetPageAsync_EchoesNormalizedPaginationMetadata_InReturnedFilters()
    {
        await using var dbContext = CreateDbContext();
        var now = DateTime.UtcNow;

        dbContext.DecisionTraces.Add(new DecisionTrace
        {
            Id = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            CorrelationId = "corr-pagination",
            DecisionId = "dec-pagination",
            UserId = "user-pagination",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            StrategyVersion = "StrategyVersion:test",
            SignalType = "Entry",
            DecisionOutcome = "Persisted",
            DecisionReasonType = "Allow",
            DecisionReasonCode = "Allowed",
            DecisionSummary = "Pagination candidate.",
            DecisionAtUtc = now,
            LatencyMs = 12,
            SnapshotJson = "{\"decision\":true}",
            CreatedAtUtc = now,
            UpdatedDate = now
        });
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        var snapshot = await service.GetPageAsync(
            new LogCenterQueryRequest(
                Query: null,
                CorrelationId: null,
                DecisionId: null,
                ExecutionAttemptId: null,
                UserId: null,
                Symbol: null,
                Status: null,
                FromUtc: now,
                ToUtc: now,
                Take: 100,
                Page: 0,
                PageSize: 999));

        Assert.False(snapshot.HasError);
        Assert.Equal(1, snapshot.Filters.Page);
        Assert.Equal(100, snapshot.Filters.PageSize);
    }

    [Fact]
    public async Task GetPageAsync_UsesDecisionReasonFieldsInSummaryAndFilters()
    {
        await using var dbContext = CreateDbContext();
        var now = new DateTime(2026, 4, 5, 9, 30, 0, DateTimeKind.Utc);

        dbContext.DecisionTraces.AddRange(
            new DecisionTrace
            {
                Id = Guid.NewGuid(),
                StrategySignalId = Guid.NewGuid(),
                CorrelationId = "corr-stale",
                DecisionId = "dec-stale",
                UserId = "user-01",
                Symbol = "BTCUSDT",
                Timeframe = "1m",
                StrategyVersion = "ExecutionGate",
                SignalType = "ExecutionGate",
                DecisionOutcome = "Block",
                DecisionReasonType = "StaleData",
                DecisionReasonCode = "StaleMarketData",
                DecisionSummary = "Execution blocked because market data is stale.",
                DecisionAtUtc = now,
                LastCandleAtUtc = now.AddSeconds(-3),
                DataAgeMs = 3000,
                StaleThresholdMs = 3000,
                StaleReason = "Market data stale",
                ContinuityState = "Continuity OK",
                ContinuityGapCount = 0,
                LatencyMs = 0,
                SnapshotJson = "{\"decision\":true}",
                CreatedAtUtc = now,
                UpdatedDate = now
            },
            new DecisionTrace
            {
                Id = Guid.NewGuid(),
                StrategySignalId = Guid.NewGuid(),
                CorrelationId = "corr-risk",
                DecisionId = "dec-risk",
                UserId = "user-01",
                Symbol = "BTCUSDT",
                Timeframe = "1m",
                StrategyVersion = "StrategyVersion:test",
                SignalType = "Entry",
                DecisionOutcome = "Vetoed",
                DecisionReasonType = "RiskVeto",
                DecisionReasonCode = "UserExecutionRiskSymbolExposureLimitBreached",
                DecisionSummary = "Risk veto blocked execution.",
                DecisionAtUtc = now.AddSeconds(1),
                LatencyMs = 0,
                SnapshotJson = "{\"decision\":true}",
                CreatedAtUtc = now.AddSeconds(1),
                UpdatedDate = now.AddSeconds(1)
            });
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);

        var staleSnapshot = await service.GetPageAsync(
            new LogCenterQueryRequest(
                Query: "Market data stale",
                CorrelationId: null,
                DecisionId: null,
                ExecutionAttemptId: null,
                UserId: null,
                Symbol: null,
                Status: "StaleMarketData",
                FromUtc: null,
                ToUtc: null,
                Take: 20));

        var staleEntry = Assert.Single(staleSnapshot.Entries);
        Assert.Equal("StaleData", staleEntry.DecisionReasonType);
        Assert.Equal("StaleMarketData", staleEntry.DecisionReasonCode);
        Assert.Equal("Market data stale", staleEntry.StaleReason);
        Assert.Contains("ReasonType=StaleData", staleEntry.Summary, StringComparison.Ordinal);
        Assert.Contains("ContinuityState=Continuity OK", staleEntry.Summary, StringComparison.Ordinal);

        var riskSnapshot = await service.GetPageAsync(
            new LogCenterQueryRequest(
                Query: null,
                CorrelationId: null,
                DecisionId: null,
                ExecutionAttemptId: null,
                UserId: null,
                Symbol: null,
                Status: "RiskVeto",
                FromUtc: null,
                ToUtc: null,
                Take: 20));

        var riskEntry = Assert.Single(riskSnapshot.Entries);
        Assert.Equal("RiskVeto", riskEntry.DecisionReasonType);
        Assert.Equal("UserExecutionRiskSymbolExposureLimitBreached", riskEntry.DecisionReasonCode);
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
            DecisionReasonType = "Allow",
            DecisionReasonCode = "Allowed",
            DecisionSummary = "Strategy produced an executable candidate.",
            DecisionAtUtc = now,
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
            DecisionReasonType = "Allow",
            DecisionReasonCode = "Allowed",
            DecisionSummary = "Strategy produced an executable candidate.",
            DecisionAtUtc = hitDate,
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
            DecisionReasonType = "Allow",
            DecisionReasonCode = "Allowed",
            DecisionSummary = "Strategy produced an executable candidate.",
            DecisionAtUtc = oldDate,
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
            Endpoint = "/fapi/v1/order?symbol=BTCUSDT",
            RequestMasked = "{\"symbol\":\"BTCUSDT\",\"secret\":\"plain-secret\"}",
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

    private static async Task SeedSymbolFilterEntriesAsync(ApplicationDbContext dbContext, DateTime now)
    {
        var incident = new Incident
        {
            Id = Guid.NewGuid(),
            IncidentReference = "INC-hit",
            Severity = IncidentSeverity.Warning,
            Status = IncidentStatus.Open,
            OperationType = ApprovalQueueOperationType.GlobalSystemStateUpdate,
            Title = "BTCUSDT risk alert",
            Summary = "BTCUSDT monitoring",
            Detail = "BTCUSDT correlation details",
            TargetType = "Symbol",
            TargetId = "BTCUSDT",
            CorrelationId = "corr-hit",
            CommandId = "cmd-hit",
            DecisionId = "dec-hit",
            ExecutionAttemptId = "exe-hit",
            CreatedByUserId = "user-hit",
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
            Title = "BTCUSDT override approval",
            Summary = "BTCUSDT scope request",
            TargetType = "Symbol",
            TargetId = "BTCUSDT",
            RequestedByUserId = "user-hit",
            RequiredApprovals = 2,
            ApprovalCount = 1,
            ExpiresAtUtc = now.AddHours(1),
            Reason = "Allow BTCUSDT only",
            PayloadJson = "{\"symbol\":\"BTCUSDT\",\"secret\":\"plain-secret\"}",
            PayloadHash = "hash-hit",
            CorrelationId = "corr-hit",
            CommandId = "cmd-hit",
            DecisionId = "dec-hit",
            ExecutionAttemptId = "exe-hit",
            IncidentReference = incident.IncidentReference,
            CreatedDate = now,
            UpdatedDate = now
        };

        dbContext.AddRange(
            new DecisionTrace
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
            DecisionReasonType = "Allow",
            DecisionReasonCode = "Allowed",
            DecisionSummary = "Strategy produced an executable candidate.",
            DecisionAtUtc = now,
            LatencyMs = 12,
                SnapshotJson = "{\"symbol\":\"BTCUSDT\",\"secret\":\"plain-secret\"}",
                CreatedAtUtc = now,
                UpdatedDate = now
            },
            new DecisionTrace
            {
                Id = Guid.NewGuid(),
                StrategySignalId = Guid.NewGuid(),
                CorrelationId = "corr-noise",
                DecisionId = "dec-noise",
                UserId = "user-noise",
                Symbol = "ETHUSDT",
                Timeframe = "5m",
                StrategyVersion = "StrategyVersion:noise",
                SignalType = "Entry",
            RiskScore = 22,
            DecisionOutcome = "Persisted",
            DecisionReasonType = "Allow",
            DecisionReasonCode = "Allowed",
            DecisionSummary = "Strategy produced an executable candidate.",
            DecisionAtUtc = now,
            LatencyMs = 8,
                SnapshotJson = "{\"symbol\":\"ETHUSDT\"}",
                CreatedAtUtc = now,
                UpdatedDate = now
            },
            new ExecutionTrace
            {
                Id = Guid.NewGuid(),
                CorrelationId = "corr-hit",
                ExecutionAttemptId = "exe-hit",
                CommandId = "cmd-hit",
                UserId = "user-hit",
                Provider = "Binance.PrivateRest",
                Endpoint = "/fapi/v1/order?symbol=BTCUSDT",
                RequestMasked = "{\"symbol\":\"BTCUSDT\"}",
                ResponseMasked = "{\"status\":\"ok\"}",
                HttpStatusCode = 200,
                ExchangeCode = "OK",
                LatencyMs = 18,
                CreatedAtUtc = now,
                UpdatedDate = now
            },
            new AdminAuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = "user-hit",
                ActionType = "LogCenter.Export",
                TargetType = "LogCenter",
                TargetId = "BTCUSDT",
                OldValueSummary = "Symbol=BTCUSDT",
                NewValueSummary = "Decision=dec-hit; Execution=exe-hit",
                Reason = "BTCUSDT export requested",
                CorrelationId = "corr-hit",
                CreatedAtUtc = now
            },
            incident,
            new IncidentEvent
            {
                Id = Guid.NewGuid(),
                IncidentId = incident.Id,
                IncidentReference = incident.IncidentReference,
                EventType = IncidentEventType.TraceLinked,
                Message = "BTCUSDT trace linked",
                ActorUserId = "user-hit",
                CorrelationId = "corr-hit",
                CommandId = "cmd-hit",
                DecisionId = "dec-hit",
                ExecutionAttemptId = "exe-hit",
                PayloadJson = "{\"symbol\":\"BTCUSDT\"}",
                CreatedDate = now,
                UpdatedDate = now
            },
            approvalQueue,
            new ApprovalAction
            {
                Id = Guid.NewGuid(),
                ApprovalQueueId = approvalQueue.Id,
                ApprovalReference = approvalQueue.ApprovalReference,
                ActionType = ApprovalActionType.Approved,
                Sequence = 1,
                ActorUserId = "user-hit",
                Reason = "BTCUSDT action approved",
                CorrelationId = "corr-hit",
                CommandId = "cmd-hit",
                DecisionId = "dec-hit",
                ExecutionAttemptId = "exe-hit",
                IncidentReference = incident.IncidentReference,
                CreatedDate = now,
                UpdatedDate = now
            });

        await dbContext.SaveChangesAsync();
    }

    private sealed class TestDataScopeContext : CoinBot.Application.Abstractions.DataScope.IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }
}
