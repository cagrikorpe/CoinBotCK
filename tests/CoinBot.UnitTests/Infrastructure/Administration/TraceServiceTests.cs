using CoinBot.Application.Abstractions.Administration;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.UnitTests.Infrastructure.Administration;

public sealed class TraceServiceTests
{
    [Fact]
    public async Task WriteDecisionTraceAsync_MasksSnapshotJsonBeforePersistence()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var createdAtUtc = new DateTime(2026, 3, 24, 12, 0, 0, DateTimeKind.Utc);

        await service.WriteDecisionTraceAsync(
            new DecisionTraceWriteRequest(
                "user-01",
                "BTCUSDT",
                "1m",
                "StrategyVersion:test",
                "Entry",
                "Persisted",
                """
                {
                  "endpoint": "/fapi/v1/order?symbol=BTCUSDT&signature=abc123&apiKey=plain-key",
                  "secret": "plain-secret",
                  "nested": {
                    "authorization": "Bearer plain-token"
                  }
                }
                """,
                12,
                CorrelationId: "corr-trace-1",
                DecisionId: "dec-trace-1",
                RiskScore: 72,
                CreatedAtUtc: createdAtUtc));

        var entity = await dbContext.DecisionTraces.SingleAsync();

        Assert.DoesNotContain("plain-key", entity.SnapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-secret", entity.SnapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-token", entity.SnapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", entity.SnapshotJson, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", entity.SnapshotJson, StringComparison.Ordinal);
        Assert.Contains("BTCUSDT", entity.SnapshotJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteDecisionTraceAsync_PersistsNormalizedDecisionDiagnostics()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var createdAtUtc = new DateTime(2026, 4, 5, 9, 15, 0, DateTimeKind.Utc);

        var snapshot = await service.WriteDecisionTraceAsync(
            new DecisionTraceWriteRequest(
                "user-02",
                "BTCUSDT",
                "1m",
                "ExecutionGate",
                "ExecutionGate",
                "Block",
                "{\"decision\":true}",
                0,
                CorrelationId: "corr-trace-2",
                DecisionId: "dec-trace-2",
                DecisionReasonType: "StaleData",
                DecisionReasonCode: "StaleMarketData",
                DecisionSummary: "Execution blocked because market data is stale.",
                DecisionAtUtc: createdAtUtc,
                LastCandleAtUtc: createdAtUtc.AddSeconds(-3),
                DataAgeMs: 3000,
                StaleThresholdMs: 3000,
                StaleReason: "Market data stale",
                ContinuityState: "Continuity OK",
                ContinuityGapCount: 0,
                ContinuityGapStartedAtUtc: createdAtUtc.AddMinutes(-2),
                ContinuityGapLastSeenAtUtc: createdAtUtc.AddMinutes(-1),
                ContinuityRecoveredAtUtc: createdAtUtc));

        var entity = await dbContext.DecisionTraces.SingleAsync();

        Assert.Equal("StaleData", entity.DecisionReasonType);
        Assert.Equal("StaleMarketData", entity.DecisionReasonCode);
        Assert.Equal("Execution blocked because market data is stale.", entity.DecisionSummary);
        Assert.Equal(createdAtUtc, entity.DecisionAtUtc);
        Assert.Equal(createdAtUtc.AddSeconds(-3), entity.LastCandleAtUtc);
        Assert.Equal(3000, entity.DataAgeMs);
        Assert.Equal(3000, entity.StaleThresholdMs);
        Assert.Equal("Market data stale", entity.StaleReason);
        Assert.Equal("Continuity OK", entity.ContinuityState);
        Assert.Equal(0, entity.ContinuityGapCount);
        Assert.Equal(createdAtUtc.AddMinutes(-2), entity.ContinuityGapStartedAtUtc);
        Assert.Equal(createdAtUtc.AddMinutes(-1), entity.ContinuityGapLastSeenAtUtc);
        Assert.Equal(createdAtUtc, entity.ContinuityRecoveredAtUtc);
        Assert.Equal("StaleData", snapshot.DecisionReasonType);
        Assert.Equal("StaleMarketData", snapshot.DecisionReasonCode);
        Assert.Equal(createdAtUtc, snapshot.DecisionAtUtc);
    }

    [Fact]
    public async Task WriteExecutionTraceAsync_MasksEndpointRequestAndResponseBeforePersistence()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var createdAtUtc = new DateTime(2026, 3, 24, 12, 5, 0, DateTimeKind.Utc);

        await service.WriteExecutionTraceAsync(
            new ExecutionTraceWriteRequest(
                "cmd-raw-1",
                "user-01",
                "Binance.PrivateRest",
                "/fapi/v1/order?symbol=BTCUSDT&signature=abc123&apiKey=plain-key",
                """
                {
                  "endpoint": "/fapi/v1/order?symbol=BTCUSDT&signature=abc123&apiKey=plain-key",
                  "headers": {
                    "X-MBX-APIKEY": "plain-key",
                    "Authorization": "Bearer plain-token"
                  }
                }
                """,
                """
                {
                  "signature": "abc123",
                  "apiSecret": "plain-secret",
                  "message": "ok"
                }
                """,
                CorrelationId: "corr-trace-2",
                ExecutionAttemptId: "exe-trace-2",
                ExecutionOrderId: Guid.NewGuid(),
                HttpStatusCode: 200,
                ExchangeCode: "OK",
                LatencyMs: 18,
                CreatedAtUtc: createdAtUtc));

        var entity = await dbContext.ExecutionTraces.SingleAsync();

        Assert.DoesNotContain("plain-key", entity.Endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", entity.Endpoint, StringComparison.Ordinal);
        Assert.Contains("signature=***REDACTED***", entity.Endpoint, StringComparison.Ordinal);
        Assert.Contains("apiKey=***REDACTED***", entity.Endpoint, StringComparison.Ordinal);

        Assert.NotNull(entity.RequestMasked);
        Assert.DoesNotContain("plain-key", entity.RequestMasked, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-token", entity.RequestMasked, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", entity.RequestMasked, StringComparison.Ordinal);
        Assert.Contains("signature=***REDACTED***", entity.RequestMasked, StringComparison.Ordinal);
        Assert.Contains("Authorization", entity.RequestMasked, StringComparison.Ordinal);

        Assert.NotNull(entity.ResponseMasked);
        Assert.DoesNotContain("plain-secret", entity.ResponseMasked, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", entity.ResponseMasked, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", entity.ResponseMasked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_ReturnsTraceRow_WhenQueryMatchesExecutionOrderId()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var executionOrderId = Guid.NewGuid();

        dbContext.ExecutionTraces.Add(new CoinBot.Domain.Entities.ExecutionTrace
        {
            Id = Guid.NewGuid(),
            CorrelationId = "corr-order-1",
            ExecutionAttemptId = "exe-order-1",
            CommandId = "cmd-order-1",
            UserId = "user-order-1",
            Provider = "Binance.PrivateRest",
            Endpoint = "/fapi/v1/order",
            ExecutionOrderId = executionOrderId,
            CreatedAtUtc = new DateTime(2026, 3, 24, 12, 10, 0, DateTimeKind.Utc),
            UpdatedDate = new DateTime(2026, 3, 24, 12, 10, 0, DateTimeKind.Utc)
        });
        await dbContext.SaveChangesAsync();

        var result = await service.SearchAsync(
            new AdminTraceSearchRequest(
                Query: executionOrderId.ToString(),
                Take: 20));

        var row = Assert.Single(result);
        Assert.Equal("corr-order-1", row.CorrelationId);
        Assert.Equal(1, row.ExecutionCount);
    }

    private static TraceService CreateService(ApplicationDbContext dbContext)
    {
        return new TraceService(
            dbContext,
            new CorrelationContextAccessor(),
            TimeProvider.System);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private sealed class TestDataScopeContext : CoinBot.Application.Abstractions.DataScope.IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }
}
