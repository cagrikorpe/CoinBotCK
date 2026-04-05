using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.MarketData;
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

    [Fact]
    public async Task GetSnapshotAsync_ProjectsSpotHoldingsHistoryAndAuditParity_OnSqlServer()
    {
        var databaseName = $"CoinBotSpotPortfolioAuditInt_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        var ownerUserId = "portfolio-spot-user-01";
        var exchangeAccountId = Guid.NewGuid();
        var strategyId = Guid.NewGuid();
        var strategyVersionId = Guid.NewGuid();
        var strategySignalId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var scanCycleId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var createdAtUtc = new DateTime(2026, 4, 5, 12, 0, 0, DateTimeKind.Utc);
        var filledAtUtc = createdAtUtc.AddMinutes(2);

        await using var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        try
        {
            dbContext.Users.Add(new ApplicationUser
            {
                Id = ownerUserId,
                UserName = ownerUserId,
                NormalizedUserName = ownerUserId.ToUpperInvariant(),
                Email = $"{ownerUserId}@coinbot.test",
                NormalizedEmail = $"{ownerUserId.ToUpperInvariant()}@COINBOT.TEST",
                FullName = ownerUserId,
                EmailConfirmed = true
            });
            await dbContext.SaveChangesAsync();

            dbContext.ExchangeAccounts.Add(new ExchangeAccount
            {
                Id = exchangeAccountId,
                OwnerUserId = ownerUserId,
                ExchangeName = "Binance",
                DisplayName = "Spot Portfolio",
                CredentialStatus = ExchangeCredentialStatus.Active,
                ApiKeyCiphertext = "cipher-api-key",
                ApiSecretCiphertext = "cipher-api-secret"
            });
            dbContext.ExchangeBalances.Add(new ExchangeBalance
            {
                OwnerUserId = ownerUserId,
                ExchangeAccountId = exchangeAccountId,
                Plane = ExchangeDataPlane.Spot,
                Asset = "BTC",
                WalletBalance = 1.25m,
                CrossWalletBalance = 1.25m,
                AvailableBalance = 1m,
                MaxWithdrawAmount = 1m,
                LockedBalance = 0.25m,
                ExchangeUpdatedAtUtc = filledAtUtc,
                SyncedAtUtc = filledAtUtc
            });
            dbContext.ExchangeAccountSyncStates.Add(new ExchangeAccountSyncState
            {
                OwnerUserId = ownerUserId,
                ExchangeAccountId = exchangeAccountId,
                Plane = ExchangeDataPlane.Spot,
                PrivateStreamConnectionState = ExchangePrivateStreamConnectionState.Connected,
                DriftStatus = ExchangeStateDriftStatus.InSync,
                LastPrivateStreamEventAtUtc = filledAtUtc,
                LastBalanceSyncedAtUtc = filledAtUtc,
                LastStateReconciledAtUtc = filledAtUtc
            });
            dbContext.ExecutionOrders.Add(new ExecutionOrder
            {
                Id = orderId,
                OwnerUserId = ownerUserId,
                TradingStrategyId = strategyId,
                TradingStrategyVersionId = strategyVersionId,
                StrategySignalId = strategySignalId,
                SignalType = StrategySignalType.Entry,
                ExchangeAccountId = exchangeAccountId,
                Plane = ExchangeDataPlane.Spot,
                StrategyKey = "portfolio-spot-history",
                Symbol = "BTCUSDT",
                Timeframe = "1m",
                BaseAsset = "BTC",
                QuoteAsset = "USDT",
                Side = ExecutionOrderSide.Buy,
                OrderType = ExecutionOrderType.Market,
                Quantity = 2m,
                Price = 150m,
                FilledQuantity = 2m,
                AverageFillPrice = 150m,
                ExecutionEnvironment = ExecutionEnvironment.Live,
                ExecutorKind = ExecutionOrderExecutorKind.Binance,
                State = ExecutionOrderState.Filled,
                IdempotencyKey = "portfolio-spot-idem",
                RootCorrelationId = "root-portfolio-spot",
                ExternalOrderId = "spot-portfolio-order-1",
                SubmittedToBroker = true,
                LastStateChangedAtUtc = filledAtUtc
            });
            dbContext.ExecutionOrderTransitions.Add(new ExecutionOrderTransition
            {
                OwnerUserId = ownerUserId,
                ExecutionOrderId = orderId,
                SequenceNumber = 1,
                State = ExecutionOrderState.Filled,
                EventCode = "ExchangeFilled",
                Detail = "ClientOrderId=cb_spot_portfolio_sql_01; Plane=Spot; ExchangeStatus=FILLED",
                CorrelationId = "transition-portfolio-spot",
                ParentCorrelationId = "root-portfolio-spot",
                OccurredAtUtc = filledAtUtc
            });
            dbContext.MarketScannerCycles.Add(new MarketScannerCycle
            {
                Id = scanCycleId,
                StartedAtUtc = createdAtUtc.AddSeconds(-5),
                CompletedAtUtc = createdAtUtc,
                UniverseSource = "sql-test",
                ScannedSymbolCount = 1,
                EligibleCandidateCount = 1,
                TopCandidateCount = 1,
                BestCandidateSymbol = "BTCUSDT",
                BestCandidateScore = 200m,
                Summary = "sql-test"
            });
            dbContext.MarketScannerCandidates.Add(new MarketScannerCandidate
            {
                Id = candidateId,
                ScanCycleId = scanCycleId,
                Symbol = "BTCUSDT",
                UniverseSource = "sql-test",
                ObservedAtUtc = createdAtUtc,
                IsEligible = true,
                Rank = 1,
                MarketScore = 120m,
                StrategyScore = 80,
                Score = 200m,
                ScoringSummary = "MarketScore=120; StrategyScore=80; CompositeScore=200"
            });
            dbContext.MarketScannerHandoffAttempts.Add(new MarketScannerHandoffAttempt
            {
                Id = Guid.NewGuid(),
                ScanCycleId = scanCycleId,
                SelectedCandidateId = candidateId,
                SelectedSymbol = "BTCUSDT",
                SelectedTimeframe = "1m",
                SelectedAtUtc = createdAtUtc,
                CandidateRank = 1,
                CandidateMarketScore = 120m,
                CandidateScore = 200m,
                SelectionReason = "Top-ranked eligible candidate selected. Symbol=BTCUSDT; Rank=1.",
                OwnerUserId = ownerUserId,
                StrategyKey = "portfolio-spot-history",
                TradingStrategyId = strategyId,
                TradingStrategyVersionId = strategyVersionId,
                StrategySignalId = strategySignalId,
                StrategyDecisionOutcome = "Persisted",
                StrategyScore = 80,
                RiskOutcome = "Allowed",
                RiskVetoReasonCode = "None",
                RiskSummary = "Reason=None; Scope=User:portfolio-spot-user-01/Bot:n/a/Symbol:BTCUSDT/Coin:BTC/Timeframe:1m.",
                ExecutionRequestStatus = "Prepared",
                ExecutionSide = ExecutionOrderSide.Buy,
                ExecutionOrderType = ExecutionOrderType.Market,
                ExecutionEnvironment = ExecutionEnvironment.Live,
                ExecutionQuantity = 2m,
                ExecutionPrice = 150m,
                GuardSummary = "ExecutionGate=Allowed; UserExecutionOverride=Allowed;",
                CorrelationId = "root-portfolio-spot",
                CompletedAtUtc = createdAtUtc
            });
            dbContext.DecisionTraces.Add(new DecisionTrace
            {
                Id = Guid.NewGuid(),
                StrategySignalId = strategySignalId,
                CorrelationId = "root-portfolio-spot",
                DecisionId = "decision-portfolio-spot",
                UserId = ownerUserId,
                Symbol = "BTCUSDT",
                Timeframe = "1m",
                StrategyVersion = "1",
                SignalType = "Entry",
                RiskScore = 80,
                DecisionOutcome = "Allowed",
                LatencyMs = 11,
                SnapshotJson = "{}",
                CreatedAtUtc = createdAtUtc
            });
            dbContext.ExecutionTraces.Add(new ExecutionTrace
            {
                Id = Guid.NewGuid(),
                ExecutionOrderId = orderId,
                CorrelationId = "root-portfolio-spot",
                ExecutionAttemptId = "exec-portfolio-spot",
                CommandId = "cmd-portfolio-spot",
                UserId = ownerUserId,
                Provider = "Binance.SpotPrivateRest",
                Endpoint = "/api/v3/order",
                ResponseMasked = "Accepted",
                CreatedAtUtc = filledAtUtc
            });
            dbContext.AuditLogs.AddRange(
                new AuditLog
                {
                    Id = Guid.NewGuid(),
                    Actor = "system",
                    Action = "ExecutionOrder.ExchangeUpdate",
                    Target = $"ExecutionOrder/{orderId}",
                    Context = "Plane=Spot",
                    CorrelationId = "root-portfolio-spot",
                    Outcome = "Applied:StateChanged",
                    Environment = "Live"
                },
                new AuditLog
                {
                    Id = Guid.NewGuid(),
                    Actor = "system",
                    Action = "SpotPortfolio.FillApplied",
                    Target = $"ExecutionOrder/{orderId}",
                    Context = "Plane=Spot | AppliedTrades=3",
                    CorrelationId = "root-portfolio-spot",
                    Outcome = "Applied",
                    Environment = "Live"
                });
            dbContext.SpotPortfolioFills.AddRange(
                CreateSpotFill(ownerUserId, exchangeAccountId, orderId, 1, ExecutionOrderSide.Buy, 1m, 100m, 100m, 1m, 100m, 100m, 0m, 0m, createdAtUtc),
                CreateSpotFill(ownerUserId, exchangeAccountId, orderId, 2, ExecutionOrderSide.Buy, 1m, 200m, 200m, 2m, 300m, 150m, 0m, 0m, createdAtUtc.AddSeconds(1)),
                CreateSpotFill(ownerUserId, exchangeAccountId, orderId, 3, ExecutionOrderSide.Sell, 1m, 250m, 250m, 1m, 150m, 150m, 90m, 10m, filledAtUtc, feeAsset: "USDT", feeAmount: 10m));
            await dbContext.SaveChangesAsync();

            var service = new UserDashboardPortfolioReadModelService(
                dbContext,
                new FakeMarketDataService(
                    new Dictionary<string, decimal>(StringComparer.Ordinal)
                    {
                        ["BTCUSDT"] = 300m
                    }));

            var snapshot = await service.GetSnapshotAsync(ownerUserId);
            var holding = Assert.Single(snapshot.SpotHoldings!);
            var historyRow = Assert.Single(snapshot.TradeHistory, entity => entity.Plane == ExchangeDataPlane.Spot);

            Assert.Equal(90m, snapshot.RealizedPnl);
            Assert.Equal(150m, snapshot.UnrealizedPnl);
            Assert.Equal(240m, snapshot.TotalPnl);
            Assert.Equal(1m, holding.Quantity);
            Assert.Equal(0.75m, holding.AvailableQuantity);
            Assert.Equal(0.25m, holding.LockedQuantity);
            Assert.Equal(150m, holding.AverageCost);
            Assert.Equal(90m, holding.RealizedPnl);
            Assert.Equal(150m, holding.UnrealizedPnl);
            Assert.Equal(10m, historyRow.FeeAmountInQuote);
            Assert.Equal(3, historyRow.FillCount);
            Assert.Equal("1,2,3", historyRow.TradeIdsSummary);
            Assert.Contains("Plane=Spot", historyRow.ReasonChainSummary, StringComparison.Ordinal);
            Assert.Contains("FillCount=3", historyRow.ExecutionResultSummary, StringComparison.Ordinal);
        }
        finally
        {
            await dbContext.Database.EnsureDeletedAsync();
        }
    }


    [Fact]
    public async Task GetSnapshotAsync_ProjectsLiveFuturesPortfolioHistoryAndReconciliationParity_OnSqlServer()
    {
        var databaseName = $"CoinBotFuturesPortfolioAuditInt_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        var ownerUserId = "portfolio-futures-user-01";
        var exchangeAccountId = Guid.NewGuid();
        var strategyId = Guid.NewGuid();
        var strategyVersionId = Guid.NewGuid();
        var strategySignalId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var scanCycleId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var createdAtUtc = new DateTime(2026, 4, 5, 15, 0, 0, DateTimeKind.Utc);
        var filledAtUtc = createdAtUtc.AddMinutes(1);

        await using var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        try
        {
            dbContext.Users.Add(new ApplicationUser
            {
                Id = ownerUserId,
                UserName = ownerUserId,
                NormalizedUserName = ownerUserId.ToUpperInvariant(),
                Email = $"{ownerUserId}@coinbot.test",
                NormalizedEmail = $"{ownerUserId.ToUpperInvariant()}@COINBOT.TEST",
                FullName = ownerUserId,
                EmailConfirmed = true
            });
            await dbContext.SaveChangesAsync();

            dbContext.ExchangeAccounts.Add(new ExchangeAccount
            {
                Id = exchangeAccountId,
                OwnerUserId = ownerUserId,
                ExchangeName = "Binance",
                DisplayName = "Futures Portfolio",
                CredentialStatus = ExchangeCredentialStatus.Active,
                ApiKeyCiphertext = "cipher-api-key",
                ApiSecretCiphertext = "cipher-api-secret"
            });
            dbContext.ExchangeAccountSyncStates.Add(new ExchangeAccountSyncState
            {
                OwnerUserId = ownerUserId,
                ExchangeAccountId = exchangeAccountId,
                Plane = ExchangeDataPlane.Futures,
                PrivateStreamConnectionState = ExchangePrivateStreamConnectionState.Connected,
                DriftStatus = ExchangeStateDriftStatus.InSync,
                LastPrivateStreamEventAtUtc = filledAtUtc,
                LastPositionSyncedAtUtc = filledAtUtc,
                LastStateReconciledAtUtc = filledAtUtc
            });
            dbContext.ExchangePositions.Add(new ExchangePosition
            {
                OwnerUserId = ownerUserId,
                ExchangeAccountId = exchangeAccountId,
                Plane = ExchangeDataPlane.Futures,
                Symbol = "BTCUSDT",
                PositionSide = "LONG",
                Quantity = 0.5m,
                EntryPrice = 60000m,
                BreakEvenPrice = 60010m,
                UnrealizedProfit = 250m,
                MarginType = "cross",
                IsolatedWallet = 0m,
                ExchangeUpdatedAtUtc = filledAtUtc,
                SyncedAtUtc = filledAtUtc
            });
            dbContext.TradingBots.Add(new TradingBot
            {
                Id = botId,
                OwnerUserId = ownerUserId,
                Name = "Portfolio Futures Bot",
                StrategyKey = "portfolio-futures-history",
                Symbol = "BTCUSDT",
                IsEnabled = true
            });
            dbContext.TradingStrategies.Add(new TradingStrategy
            {
                Id = strategyId,
                OwnerUserId = ownerUserId,
                StrategyKey = "portfolio-futures-history",
                DisplayName = "Portfolio Futures History",
                PromotionState = StrategyPromotionState.LivePublished,
                PublishedMode = ExecutionEnvironment.Live,
                PublishedAtUtc = createdAtUtc
            });
            dbContext.TradingStrategyVersions.Add(new TradingStrategyVersion
            {
                Id = strategyVersionId,
                OwnerUserId = ownerUserId,
                TradingStrategyId = strategyId,
                SchemaVersion = 1,
                VersionNumber = 1,
                Status = StrategyVersionStatus.Published,
                DefinitionJson = "{}",
                PublishedAtUtc = createdAtUtc
            });
            dbContext.TradingStrategySignals.Add(new TradingStrategySignal
            {
                Id = strategySignalId,
                OwnerUserId = ownerUserId,
                TradingStrategyId = strategyId,
                TradingStrategyVersionId = strategyVersionId,
                StrategyVersionNumber = 1,
                StrategySchemaVersion = 1,
                SignalType = StrategySignalType.Entry,
                ExecutionEnvironment = ExecutionEnvironment.Live,
                Symbol = "BTCUSDT",
                Timeframe = "5m",
                IndicatorOpenTimeUtc = createdAtUtc.AddMinutes(-5),
                IndicatorCloseTimeUtc = createdAtUtc,
                IndicatorReceivedAtUtc = createdAtUtc,
                GeneratedAtUtc = createdAtUtc,
                IndicatorSnapshotJson = "{}",
                RuleResultSnapshotJson = "{}",
                RiskEvaluationJson = "{}"
            });
            dbContext.MarketScannerCycles.Add(new MarketScannerCycle
            {
                Id = scanCycleId,
                StartedAtUtc = createdAtUtc.AddSeconds(-5),
                CompletedAtUtc = createdAtUtc,
                UniverseSource = "sql-test",
                ScannedSymbolCount = 1,
                EligibleCandidateCount = 1,
                TopCandidateCount = 1,
                BestCandidateSymbol = "BTCUSDT",
                BestCandidateScore = 220m,
                Summary = "sql-test"
            });
            dbContext.MarketScannerCandidates.Add(new MarketScannerCandidate
            {
                Id = candidateId,
                ScanCycleId = scanCycleId,
                Symbol = "BTCUSDT",
                UniverseSource = "sql-test",
                ObservedAtUtc = createdAtUtc,
                IsEligible = true,
                Rank = 1,
                MarketScore = 120m,
                StrategyScore = 100,
                Score = 220m,
                ScoringSummary = "MarketScore=120; StrategyScore=100; CompositeScore=220"
            });
            dbContext.MarketScannerHandoffAttempts.Add(new MarketScannerHandoffAttempt
            {
                Id = Guid.NewGuid(),
                ScanCycleId = scanCycleId,
                SelectedCandidateId = candidateId,
                SelectedSymbol = "BTCUSDT",
                SelectedTimeframe = "5m",
                SelectedAtUtc = createdAtUtc,
                CandidateRank = 1,
                CandidateMarketScore = 120m,
                CandidateScore = 220m,
                SelectionReason = "Top-ranked eligible candidate selected. Symbol=BTCUSDT; Rank=1.",
                OwnerUserId = ownerUserId,
                BotId = botId,
                StrategyKey = "portfolio-futures-history",
                TradingStrategyId = strategyId,
                TradingStrategyVersionId = strategyVersionId,
                StrategySignalId = strategySignalId,
                StrategyDecisionOutcome = "Persisted",
                StrategyScore = 100,
                RiskOutcome = "Allowed",
                RiskVetoReasonCode = "None",
                RiskSummary = "Reason=None; Scope=User:portfolio-futures-user-01/Bot:" + botId.ToString("N") + "/Symbol:BTCUSDT/Coin:BTC/Timeframe:5m.",
                ExecutionRequestStatus = "Prepared",
                ExecutionSide = ExecutionOrderSide.Buy,
                ExecutionOrderType = ExecutionOrderType.Market,
                ExecutionEnvironment = ExecutionEnvironment.Live,
                ExecutionQuantity = 0.5m,
                ExecutionPrice = 60200m,
                GuardSummary = "ExecutionGate=Allowed; UserExecutionOverride=Allowed;",
                CorrelationId = "root-portfolio-futures",
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
                ExchangeAccountId = exchangeAccountId,
                Plane = ExchangeDataPlane.Futures,
                StrategyKey = "portfolio-futures-history",
                Symbol = "BTCUSDT",
                Timeframe = "5m",
                BaseAsset = "BTC",
                QuoteAsset = "USDT",
                Side = ExecutionOrderSide.Buy,
                OrderType = ExecutionOrderType.Market,
                Quantity = 0.5m,
                Price = 60200m,
                FilledQuantity = 0.5m,
                AverageFillPrice = 60200m,
                ExecutionEnvironment = ExecutionEnvironment.Live,
                ExecutorKind = ExecutionOrderExecutorKind.Binance,
                State = ExecutionOrderState.Filled,
                IdempotencyKey = "portfolio-futures-idem",
                RootCorrelationId = "root-portfolio-futures",
                ExternalOrderId = "futures-portfolio-order-1",
                SubmittedToBroker = true,
                LastStateChangedAtUtc = filledAtUtc
            });
            dbContext.ExecutionOrderTransitions.Add(new ExecutionOrderTransition
            {
                OwnerUserId = ownerUserId,
                ExecutionOrderId = orderId,
                SequenceNumber = 1,
                State = ExecutionOrderState.Filled,
                EventCode = "ExchangeFilled",
                Detail = "ClientOrderId=cb_futures_portfolio_sql_01; Plane=Futures; ExchangeStatus=FILLED; ExecutedQuantity=0.5; CumulativeQuoteQuantity=30100; TradeId=77; Fee=USDT:3.5; ReconciliationStatus=InSync; ReconciliationSummary=Exchange state aligned.",
                CorrelationId = "transition-portfolio-futures",
                ParentCorrelationId = "root-portfolio-futures",
                OccurredAtUtc = filledAtUtc
            });
            dbContext.DecisionTraces.Add(new DecisionTrace
            {
                Id = Guid.NewGuid(),
                StrategySignalId = strategySignalId,
                CorrelationId = "root-portfolio-futures",
                DecisionId = "decision-portfolio-futures",
                UserId = ownerUserId,
                Symbol = "BTCUSDT",
                Timeframe = "5m",
                StrategyVersion = "1",
                SignalType = "Entry",
                RiskScore = 100,
                DecisionOutcome = "Allowed",
                LatencyMs = 7,
                SnapshotJson = "{}",
                CreatedAtUtc = createdAtUtc
            });
            dbContext.ExecutionTraces.Add(new ExecutionTrace
            {
                Id = Guid.NewGuid(),
                ExecutionOrderId = orderId,
                CorrelationId = "root-portfolio-futures",
                ExecutionAttemptId = "exec-portfolio-futures",
                CommandId = "cmd-portfolio-futures",
                UserId = ownerUserId,
                Provider = "Binance.FuturesPrivateRest",
                Endpoint = "/fapi/v1/order",
                ResponseMasked = "Accepted",
                CreatedAtUtc = filledAtUtc
            });
            dbContext.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                Actor = "system",
                Action = "ExecutionOrder.ExchangeUpdate",
                Target = $"ExecutionOrder/{orderId}",
                Context = "Plane=Futures",
                CorrelationId = "root-portfolio-futures",
                Outcome = "Applied:StateChanged",
                Environment = "Live"
            });
            await dbContext.SaveChangesAsync();

            var service = new UserDashboardPortfolioReadModelService(dbContext);

            var snapshot = await service.GetSnapshotAsync(ownerUserId);
            var position = Assert.Single(snapshot.Positions, entity => entity.Plane == ExchangeDataPlane.Futures && entity.Symbol == "BTCUSDT");
            var historyRow = Assert.Single(snapshot.TradeHistory, entity => entity.Plane == ExchangeDataPlane.Futures);

            Assert.Equal(0m, snapshot.RealizedPnl);
            Assert.Equal(250m, snapshot.UnrealizedPnl);
            Assert.Equal(250m, snapshot.TotalPnl);
            Assert.Equal(30000m, position.CostBasis);
            Assert.Equal(60500m, position.MarkPrice);
            Assert.Equal(250m, historyRow.UnrealizedPnlContribution);
            Assert.Equal(3.5m, historyRow.FeeAmountInQuote);
            Assert.Equal(30100m, historyRow.CumulativeQuoteQuantity);
            Assert.Equal("77", historyRow.TradeIdsSummary);
            Assert.Contains("TradeId=77", historyRow.ExecutionResultSummary, StringComparison.Ordinal);
            Assert.Contains("Fee=USDT:3.5", historyRow.ExecutionResultSummary, StringComparison.Ordinal);
            Assert.Contains("Plane=Futures", historyRow.ReasonChainSummary, StringComparison.Ordinal);
            Assert.Contains("ExecutedQuantity=0.5", historyRow.ReasonChainSummary, StringComparison.Ordinal);
            Assert.Contains("ReconciliationStatus=InSync", historyRow.ReasonChainSummary, StringComparison.Ordinal);
            Assert.Contains("ReconciliationSummary=Exchange state aligned.", historyRow.ExecutionResultSummary, StringComparison.Ordinal);
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

    private static SpotPortfolioFill CreateSpotFill(
        string ownerUserId,
        Guid exchangeAccountId,
        Guid executionOrderId,
        long tradeId,
        ExecutionOrderSide side,
        decimal quantity,
        decimal quoteQuantity,
        decimal price,
        decimal holdingQuantityAfter,
        decimal holdingCostBasisAfter,
        decimal holdingAverageCostAfter,
        decimal realizedPnlDelta,
        decimal cumulativeFeesInQuoteAfter,
        DateTime occurredAtUtc,
        string? feeAsset = null,
        decimal? feeAmount = null)
    {
        return new SpotPortfolioFill
        {
            OwnerUserId = ownerUserId,
            ExchangeAccountId = exchangeAccountId,
            ExecutionOrderId = executionOrderId,
            Plane = ExchangeDataPlane.Spot,
            Symbol = "BTCUSDT",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = side,
            ExchangeOrderId = "spot-portfolio-order-1",
            ClientOrderId = "cb_spot_portfolio_sql_01",
            TradeId = tradeId,
            Quantity = quantity,
            QuoteQuantity = quoteQuantity,
            Price = price,
            FeeAsset = feeAsset,
            FeeAmount = feeAmount,
            FeeAmountInQuote = feeAmount ?? 0m,
            RealizedPnlDelta = realizedPnlDelta,
            HoldingQuantityAfter = holdingQuantityAfter,
            HoldingCostBasisAfter = holdingCostBasisAfter,
            HoldingAverageCostAfter = holdingAverageCostAfter,
            CumulativeRealizedPnlAfter = tradeId == 3 ? realizedPnlDelta : 0m,
            CumulativeFeesInQuoteAfter = cumulativeFeesInQuoteAfter,
            Source = "Binance.SpotPrivateRest.MyTrades",
            RootCorrelationId = "root-portfolio-spot",
            OccurredAtUtc = occurredAtUtc
        };
    }

    private sealed class FakeMarketDataService(IReadOnlyDictionary<string, decimal> prices) : IMarketDataService
    {
        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<MarketPriceSnapshot?>(
                prices.TryGetValue(symbol, out var price)
                    ? new MarketPriceSnapshot(symbol, price, DateTime.UtcNow, DateTime.UtcNow, "SpotPortfolioReadModelSqlTest")
                    : null);
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(null);
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }
    }
}


