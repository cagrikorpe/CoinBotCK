using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.IntegrationTests.Administration;

public sealed class LogCenterReadModelIntegrationTests
{
    [Fact]
    public async Task GetPageAsync_ReturnsMaskedCombinedEntries_AndAppliesFilters_OnSqlServer()
    {
        var databaseName = $"CoinBotLogCenterReadModelInt_{Guid.NewGuid():N}";
        var connectionString = ResolveConnectionString(databaseName);
        var now = new DateTime(2026, 3, 24, 12, 0, 0, DateTimeKind.Utc);

        await using var dbContext = CreateDbContext(connectionString);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        var incident = new Incident
        {
            Id = Guid.NewGuid(),
            IncidentReference = "INC-hit",
            Severity = IncidentSeverity.Warning,
            Status = IncidentStatus.Open,
            OperationType = ApprovalQueueOperationType.GlobalSystemStateUpdate,
            Title = "BTCUSDT risk alert",
            Summary = "BTCUSDT monitoring",
            Detail = "authorization=Bearer plain-token",
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
            PayloadJson = """
            {
              "symbol": "BTCUSDT",
              "apiSecret": "plain-secret"
            }
            """,
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
                LatencyMs = 12,
                SnapshotJson = """
                {
                  "symbol": "BTCUSDT",
                  "apiKey": "plain-key",
                  "signature": "abc123"
                }
                """,
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
                Endpoint = "/fapi/v1/order?symbol=BTCUSDT&signature=abc123&apiKey=plain-key",
                RequestMasked = """
                {
                  "symbol": "BTCUSDT",
                  "Authorization": "Bearer plain-token"
                }
                """,
                ResponseMasked = """
                {
                  "message": "ok",
                  "secret": "plain-secret"
                }
                """,
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
                OldValueSummary = "Decision=dec-hit",
                NewValueSummary = "Execution=exe-hit",
                Reason = "BTCUSDT export requested with plain-token",
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
                PayloadJson = """
                {
                  "symbol": "BTCUSDT",
                  "apiSecret": "plain-secret"
                }
                """,
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

        var service = new LogCenterReadModelService(
            dbContext,
            Options.Create(new LogCenterRetentionOptions()),
            NullLogger<LogCenterReadModelService>.Instance);

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
        Assert.Equal(
            ["AdminAuditLog", "ApprovalAction", "ApprovalQueue", "DecisionTrace", "ExecutionTrace", "Incident", "IncidentEvent"],
            snapshot.Entries.Select(entry => entry.Kind).OrderBy(kind => kind, StringComparer.Ordinal).ToArray());
        Assert.All(snapshot.Entries, entry =>
        {
            Assert.DoesNotContain("plain-secret", entry.RawJson ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("plain-key", entry.RawJson ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("plain-token", entry.RawJson ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("abc123", entry.RawJson ?? string.Empty, StringComparison.Ordinal);
        });
        Assert.Contains(snapshot.Entries, entry => entry.RawJson?.Contains("***REDACTED***", StringComparison.Ordinal) == true);
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
        var configuredConnectionString = Environment.GetEnvironmentVariable("COINBOT_INTEGRATION_SQLSERVER_CONNECTION_STRING");

        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return configuredConnectionString.Replace("{Database}", databaseName, StringComparison.OrdinalIgnoreCase);
        }

        return $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }
}
