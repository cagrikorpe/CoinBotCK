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

    [Fact]
    public async Task FindExactMatchAsync_ReturnsDecisionSelection_WhenReferenceMatchesDecisionId()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        dbContext.DecisionTraces.Add(new CoinBot.Domain.Entities.DecisionTrace
        {
            Id = Guid.NewGuid(),
            CorrelationId = "corr-decision-1",
            DecisionId = "dec-decision-1",
            UserId = "user-1",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            StrategyVersion = "StrategyVersion:test",
            SignalType = "Entry",
            RiskScore = 50,
            DecisionOutcome = "Persisted",
            SnapshotJson = "{}",
            CreatedAtUtc = new DateTime(2026, 3, 24, 12, 15, 0, DateTimeKind.Utc),
            UpdatedDate = new DateTime(2026, 3, 24, 12, 15, 0, DateTimeKind.Utc)
        });
        await dbContext.SaveChangesAsync();

        var result = await service.FindExactMatchAsync("dec-decision-1");

        Assert.NotNull(result);
        Assert.Equal("corr-decision-1", result!.CorrelationId);
        Assert.Equal("dec-decision-1", result.DecisionId);
        Assert.Null(result.ExecutionAttemptId);
    }

    [Fact]
    public async Task FindExactMatchAsync_ReturnsExecutionSelection_WhenReferenceMatchesExecutionOrderId()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var executionOrderId = Guid.NewGuid();

        dbContext.ExecutionTraces.Add(new CoinBot.Domain.Entities.ExecutionTrace
        {
            Id = Guid.NewGuid(),
            CorrelationId = "corr-execution-1",
            ExecutionAttemptId = "exe-execution-1",
            CommandId = "cmd-execution-1",
            UserId = "user-execution-1",
            Provider = "Binance.PrivateRest",
            Endpoint = "/fapi/v1/order",
            ExecutionOrderId = executionOrderId,
            CreatedAtUtc = new DateTime(2026, 3, 24, 12, 20, 0, DateTimeKind.Utc),
            UpdatedDate = new DateTime(2026, 3, 24, 12, 20, 0, DateTimeKind.Utc)
        });
        await dbContext.SaveChangesAsync();

        var result = await service.FindExactMatchAsync(executionOrderId.ToString());

        Assert.NotNull(result);
        Assert.Equal("corr-execution-1", result!.CorrelationId);
        Assert.Null(result.DecisionId);
        Assert.Equal("exe-execution-1", result.ExecutionAttemptId);
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsHandoffAttemptsAndExecutionTransitions_WhenTransitionCorrelationIsStepLocal()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var strategySignalId = Guid.NewGuid();
        var executionOrderId = Guid.NewGuid();
        var now = new DateTime(2026, 4, 24, 12, 0, 0, DateTimeKind.Utc);

        dbContext.DecisionTraces.Add(new CoinBot.Domain.Entities.DecisionTrace
        {
            Id = Guid.NewGuid(),
            StrategySignalId = strategySignalId,
            CorrelationId = "corr-chain-1",
            DecisionId = "dec-chain-1",
            UserId = "user-chain-1",
            Symbol = "SOLUSDT",
            Timeframe = "1m",
            StrategyVersion = "StrategyVersion:test",
            SignalType = "Entry",
            DecisionOutcome = "Persisted",
            SnapshotJson = "{}",
            CreatedAtUtc = now
        });
        dbContext.MarketScannerHandoffAttempts.Add(new CoinBot.Domain.Entities.MarketScannerHandoffAttempt
        {
            Id = Guid.NewGuid(),
            ScanCycleId = Guid.NewGuid(),
            SelectedSymbol = "SOLUSDT",
            SelectedTimeframe = "1m",
            OwnerUserId = "user-chain-1",
            StrategySignalId = strategySignalId,
            StrategyDecisionOutcome = "Persisted",
            ExecutionRequestStatus = "Prepared",
            CorrelationId = "corr-chain-1",
            CompletedAtUtc = now.AddSeconds(2)
        });
        dbContext.ExecutionOrders.Add(new CoinBot.Domain.Entities.ExecutionOrder
        {
            Id = executionOrderId,
            OwnerUserId = "user-chain-1",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = strategySignalId,
            SignalType = CoinBot.Domain.Enums.StrategySignalType.Entry,
            StrategyKey = "strategy-chain",
            Symbol = "SOLUSDT",
            Timeframe = "1m",
            BaseAsset = "SOL",
            QuoteAsset = "USDT",
            Side = CoinBot.Domain.Enums.ExecutionOrderSide.Buy,
            OrderType = CoinBot.Domain.Enums.ExecutionOrderType.Market,
            Quantity = 1m,
            Price = 100m,
            ExecutionEnvironment = CoinBot.Domain.Enums.ExecutionEnvironment.Live,
            ExecutorKind = CoinBot.Domain.Enums.ExecutionOrderExecutorKind.Binance,
            State = CoinBot.Domain.Enums.ExecutionOrderState.Submitted,
            IdempotencyKey = "idemp-chain-1",
            RootCorrelationId = "corr-chain-1",
            LastStateChangedAtUtc = now.AddSeconds(4)
        });
        dbContext.ExecutionTraces.Add(new CoinBot.Domain.Entities.ExecutionTrace
        {
            Id = Guid.NewGuid(),
            ExecutionOrderId = executionOrderId,
            CorrelationId = "corr-chain-1",
            ExecutionAttemptId = "exe-chain-1",
            CommandId = "cmd-chain-1",
            UserId = "user-chain-1",
            Provider = "Binance.PrivateRest",
            Endpoint = "/fapi/v1/order",
            CreatedAtUtc = now.AddSeconds(3)
        });
        dbContext.ExecutionOrderTransitions.Add(new CoinBot.Domain.Entities.ExecutionOrderTransition
        {
            Id = Guid.NewGuid(),
            ExecutionOrderId = executionOrderId,
            OwnerUserId = "user-chain-1",
            SequenceNumber = 1,
            State = CoinBot.Domain.Enums.ExecutionOrderState.Submitted,
            EventCode = "Submitted",
            CorrelationId = "step-local-chain-1",
            ParentCorrelationId = "step-parent-chain-1",
            OccurredAtUtc = now.AddSeconds(4)
        });
        await dbContext.SaveChangesAsync();

        var detail = await service.GetDetailAsync("corr-chain-1");

        Assert.NotNull(detail);
        Assert.Single(detail!.HandoffAttempts!);
        Assert.Single(detail.ExecutionTransitions!);
        Assert.Equal("Prepared", detail.HandoffAttempts.Single().ExecutionRequestStatus);
        Assert.Equal("Submitted", detail.ExecutionTransitions.Single().State);
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsExecutionTransitions_WhenExecutionOrderMatchesDecisionStrategySignalAnchor()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var strategySignalId = Guid.NewGuid();
        var executionOrderId = Guid.NewGuid();
        var now = new DateTime(2026, 4, 24, 12, 10, 0, DateTimeKind.Utc);

        dbContext.DecisionTraces.Add(new CoinBot.Domain.Entities.DecisionTrace
        {
            Id = Guid.NewGuid(),
            StrategySignalId = strategySignalId,
            CorrelationId = "corr-anchor-1",
            DecisionId = "dec-anchor-1",
            UserId = "user-anchor-1",
            Symbol = "SOLUSDT",
            Timeframe = "1m",
            StrategyVersion = "StrategyVersion:test",
            SignalType = "Entry",
            DecisionOutcome = "Persisted",
            SnapshotJson = "{}",
            CreatedAtUtc = now
        });
        dbContext.ExecutionOrders.Add(new CoinBot.Domain.Entities.ExecutionOrder
        {
            Id = executionOrderId,
            OwnerUserId = "user-anchor-1",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = strategySignalId,
            SignalType = CoinBot.Domain.Enums.StrategySignalType.Entry,
            StrategyKey = "strategy-anchor",
            Symbol = "SOLUSDT",
            Timeframe = "1m",
            BaseAsset = "SOL",
            QuoteAsset = "USDT",
            Side = CoinBot.Domain.Enums.ExecutionOrderSide.Buy,
            OrderType = CoinBot.Domain.Enums.ExecutionOrderType.Market,
            Quantity = 1m,
            Price = 100m,
            ExecutionEnvironment = CoinBot.Domain.Enums.ExecutionEnvironment.Live,
            ExecutorKind = CoinBot.Domain.Enums.ExecutionOrderExecutorKind.Binance,
            State = CoinBot.Domain.Enums.ExecutionOrderState.Submitted,
            IdempotencyKey = "idemp-anchor-1",
            RootCorrelationId = "different-root-correlation",
            ParentCorrelationId = "different-parent-correlation",
            LastStateChangedAtUtc = now.AddSeconds(2)
        });
        dbContext.ExecutionOrderTransitions.Add(new CoinBot.Domain.Entities.ExecutionOrderTransition
        {
            Id = Guid.NewGuid(),
            ExecutionOrderId = executionOrderId,
            OwnerUserId = "user-anchor-1",
            SequenceNumber = 1,
            State = CoinBot.Domain.Enums.ExecutionOrderState.Submitted,
            EventCode = "Submitted",
            CorrelationId = "step-local-anchor-1",
            ParentCorrelationId = "step-parent-anchor-1",
            OccurredAtUtc = now.AddSeconds(3)
        });
        await dbContext.SaveChangesAsync();

        var detail = await service.GetDetailAsync("corr-anchor-1");

        Assert.NotNull(detail);
        Assert.Single(detail!.ExecutionTransitions!);
        Assert.Equal("Submitted", detail.ExecutionTransitions.Single().State);
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
