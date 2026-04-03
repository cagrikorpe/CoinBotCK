using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Dashboard;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Persistence;
using CoinBot.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.IntegrationTests.Execution;

public sealed class UserDashboardPortfolioReadModelIntegrationTests
{
    [Fact]
    public async Task GetSnapshotAsync_ProjectsPnlHistoryReasonChainAndAiPlaceholder_OnSqlServer()
    {
        var databaseName = $"CoinBotPortfolioAuditInt_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        var ownerUserId = "portfolio-user-01";
        var otherUserId = "portfolio-user-02";
        var strategyId = Guid.NewGuid();
        var strategyVersionId = Guid.NewGuid();
        var strategySignalId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var createdAtUtc = new DateTime(2026, 4, 3, 12, 30, 0, DateTimeKind.Utc);
        var filledAtUtc = createdAtUtc.AddMinutes(3);

        await using var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        try
        {
            dbContext.Users.AddRange(
                new ApplicationUser
                {
                    Id = ownerUserId,
                    UserName = ownerUserId,
                    NormalizedUserName = ownerUserId.ToUpperInvariant(),
                    Email = $"{ownerUserId}@coinbot.test",
                    NormalizedEmail = $"{ownerUserId.ToUpperInvariant()}@COINBOT.TEST",
                    FullName = ownerUserId,
                    EmailConfirmed = true
                },
                new ApplicationUser
                {
                    Id = otherUserId,
                    UserName = otherUserId,
                    NormalizedUserName = otherUserId.ToUpperInvariant(),
                    Email = $"{otherUserId}@coinbot.test",
                    NormalizedEmail = $"{otherUserId.ToUpperInvariant()}@COINBOT.TEST",
                    FullName = otherUserId,
                    EmailConfirmed = true
                });
            await dbContext.SaveChangesAsync();

            dbContext.TradingBots.Add(new TradingBot
            {
                Id = botId,
                OwnerUserId = ownerUserId,
                Name = "Portfolio History Bot",
                StrategyKey = "portfolio-history",
                Symbol = "BTCUSDT",
                IsEnabled = true
            });
            dbContext.TradingStrategies.Add(new TradingStrategy
            {
                Id = strategyId,
                OwnerUserId = ownerUserId,
                StrategyKey = "portfolio-history",
                DisplayName = "Portfolio History",
                PromotionState = StrategyPromotionState.LivePublished,
                PublishedMode = ExecutionEnvironment.Demo,
                PublishedAtUtc = createdAtUtc
            });
            dbContext.TradingStrategyVersions.Add(new TradingStrategyVersion
            {
                Id = strategyVersionId,
                OwnerUserId = ownerUserId,
                TradingStrategyId = strategyId,
                SchemaVersion = 1,
                VersionNumber = 2,
                Status = StrategyVersionStatus.Published,
                DefinitionJson = "{}",
                PublishedAtUtc = createdAtUtc
            });
            dbContext.DemoPositions.Add(new DemoPosition
            {
                OwnerUserId = ownerUserId,
                BotId = botId,
                PositionScopeKey = "portfolio",
                Symbol = "BTCUSDT",
                BaseAsset = "BTC",
                QuoteAsset = "USDT",
                Quantity = 0m,
                CostBasis = 0m,
                AverageEntryPrice = 0m,
                RealizedPnl = 42.5m,
                UnrealizedPnl = 0m,
                MarginMode = DemoMarginMode.Cross,
                LastFilledAtUtc = filledAtUtc,
                LastValuationAtUtc = filledAtUtc
            });
            dbContext.DemoLedgerTransactions.Add(new DemoLedgerTransaction
            {
                OwnerUserId = ownerUserId,
                OperationId = "execution-fill:" + orderId.ToString("N"),
                TransactionType = DemoLedgerTransactionType.FillApplied,
                BotId = botId,
                PositionScopeKey = "portfolio",
                OrderId = orderId.ToString("N"),
                FillId = $"demo-fill:{orderId:N}:1",
                Symbol = "BTCUSDT",
                BaseAsset = "BTC",
                QuoteAsset = "USDT",
                Side = DemoTradeSide.Sell,
                Quantity = 0.5m,
                Price = 65100m,
                FeeAmountInQuote = 1.25m,
                RealizedPnlDelta = 42.5m,
                CumulativeRealizedPnlAfter = 42.5m,
                UnrealizedPnlAfter = 0m,
                OccurredAtUtc = filledAtUtc
            });
            dbContext.TradingStrategySignals.Add(new TradingStrategySignal
            {
                Id = strategySignalId,
                OwnerUserId = ownerUserId,
                TradingStrategyId = strategyId,
                TradingStrategyVersionId = strategyVersionId,
                StrategyVersionNumber = 2,
                StrategySchemaVersion = 1,
                SignalType = StrategySignalType.Entry,
                ExecutionEnvironment = ExecutionEnvironment.Demo,
                Symbol = "BTCUSDT",
                Timeframe = "1m",
                IndicatorOpenTimeUtc = createdAtUtc.AddMinutes(-1),
                IndicatorCloseTimeUtc = createdAtUtc,
                IndicatorReceivedAtUtc = createdAtUtc,
                GeneratedAtUtc = createdAtUtc,
                IndicatorSnapshotJson = "{}",
                RuleResultSnapshotJson = "{}",
                RiskEvaluationJson = "{}"
            });
            dbContext.MarketScannerCycles.Add(new MarketScannerCycle
            {
                Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                StartedAtUtc = createdAtUtc.AddSeconds(-2),
                CompletedAtUtc = createdAtUtc,
                UniverseSource = "sql-test",
                ScannedSymbolCount = 1,
                EligibleCandidateCount = 1,
                TopCandidateCount = 1,
                BestCandidateSymbol = "BTCUSDT",
                BestCandidateScore = 500m,
                Summary = "sql-test"
            });
            dbContext.MarketScannerCandidates.Add(new MarketScannerCandidate
            {
                Id = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                ScanCycleId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                Symbol = "BTCUSDT",
                UniverseSource = "sql-test",
                ObservedAtUtc = createdAtUtc,
                IsEligible = true,
                Rank = 1,
                MarketScore = 400m,
                StrategyScore = 100,
                Score = 500m,
                ScoringSummary = "MarketScore=400; StrategyScore=100; CompositeScore=500"
            });
            dbContext.MarketScannerHandoffAttempts.Add(new MarketScannerHandoffAttempt
            {
                Id = Guid.NewGuid(),
                ScanCycleId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                SelectedCandidateId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                SelectedSymbol = "BTCUSDT",
                SelectedTimeframe = "1m",
                SelectedAtUtc = createdAtUtc,
                CandidateRank = 1,
                CandidateMarketScore = 400m,
                CandidateScore = 500m,
                SelectionReason = "Top-ranked eligible candidate selected. Symbol=BTCUSDT; Rank=1; MarketScore=400; StrategyScore=100; CompositeScore=500.",
                OwnerUserId = ownerUserId,
                BotId = botId,
                StrategyKey = "portfolio-history",
                TradingStrategyId = strategyId,
                TradingStrategyVersionId = strategyVersionId,
                StrategySignalId = strategySignalId,
                StrategyDecisionOutcome = "Persisted",
                StrategyScore = 100,
                RiskOutcome = "Allowed",
                RiskVetoReasonCode = "None",
                RiskSummary = "Reason=None; Scope=User:portfolio-user-01/Bot:" + botId.ToString("N") + "/Symbol:BTCUSDT/Coin:BTC/Timeframe:1m.",
                ExecutionRequestStatus = "Prepared",
                ExecutionSide = ExecutionOrderSide.Buy,
                ExecutionOrderType = ExecutionOrderType.Market,
                ExecutionEnvironment = ExecutionEnvironment.Demo,
                ExecutionQuantity = 0.5m,
                ExecutionPrice = 65000m,
                GuardSummary = "ExecutionGate=Allowed; UserExecutionOverride=Allowed; StrategyOutcome=Pass; StrategyScore=100",
                CorrelationId = "handoff-sql-01",
                CompletedAtUtc = createdAtUtc
            });
            dbContext.ExecutionOrders.Add(new ExecutionOrder
            {
                Id = orderId,
                OwnerUserId = ownerUserId,
                TradingStrategyId = strategyId,
                TradingStrategyVersionId = strategyVersionId,
                StrategySignalId = strategySignalId,
                SignalType = StrategySignalType.Entry,
                BotId = botId,
                StrategyKey = "portfolio-history",
                Symbol = "BTCUSDT",
                Timeframe = "1m",
                BaseAsset = "BTC",
                QuoteAsset = "USDT",
                Side = ExecutionOrderSide.Buy,
                OrderType = ExecutionOrderType.Market,
                Quantity = 0.5m,
                Price = 65000m,
                FilledQuantity = 0.5m,
                AverageFillPrice = 65100m,
                ExecutionEnvironment = ExecutionEnvironment.Demo,
                ExecutorKind = ExecutionOrderExecutorKind.Virtual,
                State = ExecutionOrderState.Filled,
                IdempotencyKey = "portfolio-history-idem",
                RootCorrelationId = "portfolio-history-root-correlation",
                ExternalOrderId = orderId.ToString("N"),
                SubmittedToBroker = true,
                CooldownApplied = true,
                CreatedDate = createdAtUtc,
                UpdatedDate = filledAtUtc,
                LastStateChangedAtUtc = filledAtUtc
            });
            dbContext.ExecutionOrderTransitions.Add(new ExecutionOrderTransition
            {
                OwnerUserId = ownerUserId,
                ExecutionOrderId = orderId,
                SequenceNumber = 1,
                State = ExecutionOrderState.Filled,
                EventCode = "DemoFillSimulated",
                Detail = $"ClientOrderId={orderId:N}; Demo fill completed.",
                CorrelationId = "portfolio-history-transition",
                ParentCorrelationId = "portfolio-history-root-correlation",
                OccurredAtUtc = filledAtUtc
            });
            dbContext.DecisionTraces.Add(new DecisionTrace
            {
                Id = Guid.NewGuid(),
                StrategySignalId = strategySignalId,
                CorrelationId = "portfolio-history-root-correlation",
                DecisionId = "portfolio-history-decision",
                UserId = ownerUserId,
                Symbol = "BTCUSDT",
                Timeframe = "1m",
                StrategyVersion = "2",
                SignalType = "Entry",
                RiskScore = 100,
                DecisionOutcome = "Allowed",
                LatencyMs = 5,
                SnapshotJson = "{}",
                CreatedAtUtc = createdAtUtc
            });
            dbContext.ExecutionTraces.Add(new ExecutionTrace
            {
                Id = Guid.NewGuid(),
                ExecutionOrderId = orderId,
                CorrelationId = "portfolio-history-transition",
                ExecutionAttemptId = "portfolio-history-attempt",
                CommandId = "portfolio-history-command",
                UserId = ownerUserId,
                Provider = "virtual",
                Endpoint = "DispatchAsync",
                ResponseMasked = "Accepted",
                CreatedAtUtc = filledAtUtc
            });
            dbContext.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                Actor = "system",
                Action = "TradeExecution.Dispatch",
                Target = $"ExecutionOrder/{orderId}",
                Context = "portfolio-history",
                CorrelationId = "portfolio-history-root-correlation",
                Outcome = "Allowed",
                Environment = "Demo"
            });
            dbContext.ExecutionOrders.Add(new ExecutionOrder
            {
                Id = Guid.NewGuid(),
                OwnerUserId = otherUserId,
                TradingStrategyId = Guid.NewGuid(),
                TradingStrategyVersionId = Guid.NewGuid(),
                StrategySignalId = Guid.NewGuid(),
                SignalType = StrategySignalType.Entry,
                StrategyKey = "other",
                Symbol = "ETHUSDT",
                Timeframe = "1m",
                BaseAsset = "ETH",
                QuoteAsset = "USDT",
                Side = ExecutionOrderSide.Buy,
                OrderType = ExecutionOrderType.Market,
                Quantity = 1m,
                Price = 100m,
                ExecutionEnvironment = ExecutionEnvironment.Demo,
                ExecutorKind = ExecutionOrderExecutorKind.Virtual,
                State = ExecutionOrderState.Rejected,
                IdempotencyKey = "other-idem",
                RootCorrelationId = "other-root-correlation",
                FailureCode = "OtherUserOrder",
                CreatedDate = createdAtUtc,
                UpdatedDate = createdAtUtc,
                LastStateChangedAtUtc = createdAtUtc
            });
            await dbContext.SaveChangesAsync();

            var service = new UserDashboardPortfolioReadModelService(dbContext);

            var snapshot = await service.GetSnapshotAsync(ownerUserId);

            Assert.Equal(42.5m, snapshot.RealizedPnl);
            Assert.Equal(0m, snapshot.UnrealizedPnl);
            Assert.Equal(42.5m, snapshot.TotalPnl);
            Assert.Equal("PnL consistent. Realized=42.5; Unrealized=0; Total=42.5; LedgerDelta=42.5.", snapshot.PnlConsistencySummary);

            var historyRow = Assert.Single(snapshot.TradeHistory);
            Assert.Equal(orderId, historyRow.OrderId);
            Assert.Equal("BTCUSDT", historyRow.Symbol);
            Assert.Equal("Filled", historyRow.FinalState);
            Assert.Equal("Filled", historyRow.ExecutionResultCategory);
            Assert.Equal("DemoFillSimulated", historyRow.ExecutionResultCode);
            Assert.Equal("None", historyRow.RejectionStage);
            Assert.True(historyRow.SubmittedToBroker);
            Assert.True(historyRow.CooldownApplied);
            Assert.Equal(42.5m, historyRow.RealizedPnl);
            Assert.False(historyRow.AiScoreAvailable);
            Assert.True(historyRow.AiScoreIsPlaceholder);
            Assert.Contains("Top-ranked eligible candidate selected", historyRow.ReasonChainSummary, StringComparison.Ordinal);
            Assert.Contains("StrategyOutcome=Persisted", historyRow.ReasonChainSummary, StringComparison.Ordinal);
            Assert.Contains("RiskOutcome=Allowed", historyRow.ReasonChainSummary, StringComparison.Ordinal);
            Assert.Contains("ExecutionState=Filled", historyRow.ReasonChainSummary, StringComparison.Ordinal);
            Assert.Contains("AuditTrail=DecisionTrace:1; ExecutionTrace:1; AuditLog:1; Bot=Portfolio History Bot", historyRow.ReasonChainSummary, StringComparison.Ordinal);
        }
        finally
        {
            await dbContext.Database.EnsureDeletedAsync();
        }
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }
}
