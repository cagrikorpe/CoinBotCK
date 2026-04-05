using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Dashboard;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CoinBot.UnitTests.Infrastructure.Dashboard;

public sealed class UserDashboardPortfolioReadModelServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_ReturnsBalancesPositionsAndSyncSummary_ForActiveBinanceAccount()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var context = CreateContext(databaseRoot);
        var exchangeAccountId = Guid.NewGuid();
        var syncedAtUtc = new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

        context.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = "user-dashboard-01",
            ExchangeName = "Binance",
            DisplayName = "Primary",
            CredentialStatus = ExchangeCredentialStatus.Active,
            ApiKeyCiphertext = "cipher-api-key",
            ApiSecretCiphertext = "cipher-api-secret"
        });
        context.ExchangeBalances.Add(new ExchangeBalance
        {
            OwnerUserId = "user-dashboard-01",
            ExchangeAccountId = exchangeAccountId,
            Asset = "USDT",
            WalletBalance = 1250m,
            CrossWalletBalance = 1240m,
            AvailableBalance = 1100m,
            MaxWithdrawAmount = 1000m,
            ExchangeUpdatedAtUtc = syncedAtUtc,
            SyncedAtUtc = syncedAtUtc
        });
        context.ExchangePositions.Add(new ExchangePosition
        {
            OwnerUserId = "user-dashboard-01",
            ExchangeAccountId = exchangeAccountId,
            Symbol = "BTCUSDT",
            PositionSide = "LONG",
            Quantity = 0.25m,
            EntryPrice = 65000m,
            BreakEvenPrice = 65010m,
            UnrealizedProfit = 35m,
            MarginType = "cross",
            IsolatedWallet = 0m,
            ExchangeUpdatedAtUtc = syncedAtUtc,
            SyncedAtUtc = syncedAtUtc
        });
        context.ExchangeAccountSyncStates.Add(new ExchangeAccountSyncState
        {
            OwnerUserId = "user-dashboard-01",
            ExchangeAccountId = exchangeAccountId,
            PrivateStreamConnectionState = ExchangePrivateStreamConnectionState.Connected,
            DriftStatus = ExchangeStateDriftStatus.InSync,
            LastPrivateStreamEventAtUtc = syncedAtUtc,
            LastBalanceSyncedAtUtc = syncedAtUtc,
            LastPositionSyncedAtUtc = syncedAtUtc
        });
        await context.SaveChangesAsync();

        var service = new UserDashboardPortfolioReadModelService(context);

        var snapshot = await service.GetSnapshotAsync("user-dashboard-01");

        Assert.Equal(1, snapshot.ActiveAccountCount);
        Assert.Equal("Canli senkron bagli", snapshot.SyncStatusLabel);
        Assert.Equal("positive", snapshot.SyncStatusTone);
        Assert.Equal(syncedAtUtc, snapshot.LastSynchronizedAtUtc);
        Assert.Single(snapshot.Balances);
        Assert.Single(snapshot.Positions);
        Assert.Equal("USDT", snapshot.Balances.Single().Asset);
        Assert.Equal("BTCUSDT", snapshot.Positions.Single().Symbol);
        Assert.Equal(35m, snapshot.UnrealizedPnl);
        Assert.Equal(0m, snapshot.RealizedPnl);
        Assert.Equal(35m, snapshot.TotalPnl);
        Assert.Equal("PnL consistent. Realized=0; Unrealized=35; Total=35; LedgerDelta=0.", snapshot.PnlConsistencySummary);
        Assert.Empty(snapshot.TradeHistory);
    }

    [Fact]
    public async Task GetSnapshotAsync_ProjectsTradeHistoryReasonChainAndAiPlaceholder_WithoutCrossUserLeak()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var context = CreateContext(databaseRoot);
        var ownerUserId = "user-history-01";
        var otherUserId = "user-history-02";
        var strategyId = Guid.NewGuid();
        var strategyVersionId = Guid.NewGuid();
        var strategySignalId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var otherOrderId = Guid.NewGuid();
        var createdAtUtc = new DateTime(2026, 4, 3, 11, 15, 0, DateTimeKind.Utc);
        var updatedAtUtc = createdAtUtc.AddMinutes(2);

        context.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = ownerUserId,
            Name = "History Bot",
            StrategyKey = "history-bot",
            Symbol = "BTCUSDT",
            IsEnabled = true
        });
        context.DemoPositions.Add(new DemoPosition
        {
            OwnerUserId = ownerUserId,
            BotId = botId,
            PositionScopeKey = "portfolio",
            Symbol = "BTCUSDT",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Quantity = 0.25m,
            CostBasis = 16000m,
            AverageEntryPrice = 64000m,
            RealizedPnl = 25m,
            UnrealizedPnl = 15m,
            MarginMode = DemoMarginMode.Cross,
            LastFilledAtUtc = updatedAtUtc,
            LastValuationAtUtc = updatedAtUtc
        });
        context.DemoLedgerTransactions.Add(new DemoLedgerTransaction
        {
            OwnerUserId = ownerUserId,
            OperationId = "fill-op",
            TransactionType = DemoLedgerTransactionType.FillApplied,
            BotId = botId,
            PositionScopeKey = "portfolio",
            OrderId = orderId.ToString("N"),
            FillId = $"demo-fill:{orderId:N}:1",
            Symbol = "BTCUSDT",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = DemoTradeSide.Sell,
            Quantity = 0.25m,
            Price = 64100m,
            FeeAmountInQuote = 1.5m,
            RealizedPnlDelta = 25m,
            CumulativeRealizedPnlAfter = 25m,
            UnrealizedPnlAfter = 15m,
            OccurredAtUtc = updatedAtUtc
        });
        context.TradingStrategySignals.Add(new TradingStrategySignal
        {
            Id = strategySignalId,
            OwnerUserId = ownerUserId,
            TradingStrategyId = strategyId,
            TradingStrategyVersionId = strategyVersionId,
            StrategyVersionNumber = 1,
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
            RuleResultSnapshotJson = "{\"rule\":\"ok\"}",
            RiskEvaluationJson = "{\"risk\":\"allowed\"}"
        });
        context.MarketScannerCycles.Add(new MarketScannerCycle
        {
            Id = Guid.NewGuid(),
            StartedAtUtc = createdAtUtc.AddSeconds(-5),
            CompletedAtUtc = createdAtUtc,
            UniverseSource = "unit-test",
            ScannedSymbolCount = 1,
            EligibleCandidateCount = 1,
            TopCandidateCount = 1,
            BestCandidateSymbol = "BTCUSDT",
            BestCandidateScore = 100m,
            Summary = "unit-test"
        });
        var scanCycleId = context.MarketScannerCycles.Local.Single().Id;
        context.MarketScannerCandidates.Add(new MarketScannerCandidate
        {
            Id = Guid.NewGuid(),
            ScanCycleId = scanCycleId,
            Symbol = "BTCUSDT",
            UniverseSource = "unit-test",
            ObservedAtUtc = createdAtUtc,
            IsEligible = true,
            Rank = 1,
            MarketScore = 100m,
            StrategyScore = 88,
            Score = 188m,
            ScoringSummary = "MarketScore=100; StrategyScore=88; CompositeScore=188"
        });
        context.MarketScannerHandoffAttempts.Add(new MarketScannerHandoffAttempt
        {
            Id = Guid.NewGuid(),
            ScanCycleId = scanCycleId,
            SelectedCandidateId = context.MarketScannerCandidates.Local.Single().Id,
            SelectedSymbol = "BTCUSDT",
            SelectedTimeframe = "1m",
            SelectedAtUtc = createdAtUtc,
            CandidateRank = 1,
            CandidateMarketScore = 100m,
            CandidateScore = 188m,
            SelectionReason = "Top-ranked eligible candidate selected. Symbol=BTCUSDT; Rank=1; MarketScore=100; StrategyScore=88; CompositeScore=188.",
            OwnerUserId = ownerUserId,
            BotId = botId,
            StrategyKey = "history-bot",
            TradingStrategyId = strategyId,
            TradingStrategyVersionId = strategyVersionId,
            StrategySignalId = strategySignalId,
            StrategyDecisionOutcome = "Persisted",
            StrategyScore = 88,
            RiskOutcome = "Allowed",
            RiskVetoReasonCode = "None",
            RiskSummary = "Reason=None; Scope=User:user-history-01/Bot:" + botId.ToString("N") + "/Symbol:BTCUSDT/Coin:BTC/Timeframe:1m.",
            ExecutionRequestStatus = "Prepared",
            ExecutionSide = ExecutionOrderSide.Buy,
            ExecutionOrderType = ExecutionOrderType.Market,
            ExecutionEnvironment = ExecutionEnvironment.Demo,
            ExecutionQuantity = 0.25m,
            ExecutionPrice = 64000m,
            GuardSummary = "ExecutionGate=Allowed; UserExecutionOverride=Allowed; Symbol=BTCUSDT; Timeframe=1m.",
            CorrelationId = "handoff-history-01",
            CompletedAtUtc = createdAtUtc
        });
        context.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = orderId,
            OwnerUserId = ownerUserId,
            TradingStrategyId = strategyId,
            TradingStrategyVersionId = strategyVersionId,
            StrategySignalId = strategySignalId,
            SignalType = StrategySignalType.Entry,
            BotId = botId,
            StrategyKey = "history-bot",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.25m,
            Price = 64000m,
            FilledQuantity = 0.25m,
            AverageFillPrice = 64100m,
            ExecutionEnvironment = ExecutionEnvironment.Demo,
            ExecutorKind = ExecutionOrderExecutorKind.Virtual,
            State = ExecutionOrderState.Filled,
            IdempotencyKey = "idem-history",
            RootCorrelationId = "root-history-01-correlation",
            ExternalOrderId = orderId.ToString("N"),
            SubmittedToBroker = true,
            CooldownApplied = true,
            CreatedDate = createdAtUtc,
            UpdatedDate = updatedAtUtc,
            LastStateChangedAtUtc = updatedAtUtc
        });
        context.ExecutionOrderTransitions.Add(new ExecutionOrderTransition
        {
            OwnerUserId = ownerUserId,
            ExecutionOrderId = orderId,
            SequenceNumber = 1,
            State = ExecutionOrderState.Filled,
            EventCode = "DemoFillSimulated",
            Detail = $"ClientOrderId={orderId:N}; Simulated fill completed.",
            CorrelationId = "exec-history-01",
            ParentCorrelationId = "root-history-01-correlation",
            OccurredAtUtc = updatedAtUtc
        });
        context.DecisionTraces.Add(new DecisionTrace
        {
            Id = Guid.NewGuid(),
            StrategySignalId = strategySignalId,
            CorrelationId = "root-history-01-correlation",
            DecisionId = "decision-history-01",
            UserId = ownerUserId,
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            StrategyVersion = "1",
            SignalType = "Entry",
            RiskScore = 88,
            DecisionOutcome = "Allowed",
            LatencyMs = 12,
            SnapshotJson = "{}",
            CreatedAtUtc = createdAtUtc
        });
        context.ExecutionTraces.Add(new ExecutionTrace
        {
            Id = Guid.NewGuid(),
            ExecutionOrderId = orderId,
            CorrelationId = "exec-history-01",
            ExecutionAttemptId = "attempt-history-01",
            CommandId = "cmd-history-01",
            UserId = ownerUserId,
            Provider = "virtual",
            Endpoint = "DispatchAsync",
            ResponseMasked = "Accepted",
            CreatedAtUtc = updatedAtUtc
        });
        context.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            Actor = "system",
            Action = "TradeExecution.Dispatch",
            Target = $"ExecutionOrder/{orderId}",
            Context = "unit-test",
            CorrelationId = "root-history-01-correlation",
            Outcome = "Allowed",
            Environment = "Demo"
        });
        context.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = otherOrderId,
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
            IdempotencyKey = "idem-other",
            RootCorrelationId = "other-correlation",
            FailureCode = "OtherUserOnly",
            FailureDetail = "Other user row",
            CreatedDate = createdAtUtc,
            UpdatedDate = updatedAtUtc,
            LastStateChangedAtUtc = updatedAtUtc
        });
        await context.SaveChangesAsync();

        var service = new UserDashboardPortfolioReadModelService(context);

        var snapshot = await service.GetSnapshotAsync(ownerUserId);

        Assert.Equal(25m, snapshot.RealizedPnl);
        Assert.Equal(15m, snapshot.UnrealizedPnl);
        Assert.Equal(40m, snapshot.TotalPnl);
        Assert.Equal("PnL consistent. Realized=25; Unrealized=15; Total=40; LedgerDelta=25.", snapshot.PnlConsistencySummary);
        Assert.Single(snapshot.Positions);

        var historyRow = Assert.Single(snapshot.TradeHistory);
        Assert.Equal(orderId, historyRow.OrderId);
        Assert.Equal(orderId.ToString("N")[..24], historyRow.ClientOrderId);
        Assert.Equal("root-history-01-correlation"[..24], historyRow.CorrelationId);
        Assert.Equal("BTCUSDT", historyRow.Symbol);
        Assert.Equal("1m", historyRow.Timeframe);
        Assert.Equal("Buy", historyRow.Side);
        Assert.Equal(0.25m, historyRow.Quantity);
        Assert.Equal(64100m, historyRow.AverageFillPrice);
        Assert.Equal(25m, historyRow.RealizedPnl);
        Assert.Equal(15m, historyRow.UnrealizedPnlContribution);
        Assert.Equal(1.5m, historyRow.FeeAmountInQuote);
        Assert.Equal(16025m, historyRow.CostImpact);
        Assert.Equal("Filled", historyRow.FinalState);
        Assert.Equal("Filled", historyRow.ExecutionResultCategory);
        Assert.Equal("DemoFillSimulated", historyRow.ExecutionResultCode);
        Assert.Equal("ClientOrderId=" + orderId.ToString("N") + "; Simulated fill completed.", historyRow.ExecutionResultSummary);
        Assert.Equal("None", historyRow.RejectionStage);
        Assert.True(historyRow.SubmittedToBroker);
        Assert.False(historyRow.RetryEligible);
        Assert.True(historyRow.CooldownApplied);
        Assert.Contains("Top-ranked eligible candidate selected", historyRow.ReasonChainSummary, StringComparison.Ordinal);
        Assert.Contains("StrategyOutcome=Persisted", historyRow.ReasonChainSummary, StringComparison.Ordinal);
        Assert.Contains("RiskOutcome=Allowed", historyRow.ReasonChainSummary, StringComparison.Ordinal);
        Assert.Contains("ExecutionState=Filled", historyRow.ReasonChainSummary, StringComparison.Ordinal);
        Assert.Contains("AuditTrail=DecisionTrace:1; ExecutionTrace:1; AuditLog:1; Bot=History Bot", historyRow.ReasonChainSummary, StringComparison.Ordinal);
        Assert.False(historyRow.AiScoreAvailable);
        Assert.Null(historyRow.AiScoreValue);
        Assert.Equal("AI score placeholder", historyRow.AiScoreLabel);
        Assert.Contains("placeholder contract", historyRow.AiScoreSummary, StringComparison.Ordinal);
        Assert.Equal("portfolio-history-placeholder", historyRow.AiScoreSource);
        Assert.True(historyRow.AiScoreIsPlaceholder);
    }

    [Fact]
    public async Task GetSnapshotAsync_ProjectsSpotHoldingsHistoryAndPnlParity()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var context = CreateContext(databaseRoot);
        var ownerUserId = "user-spot-portfolio-01";
        var strategyId = Guid.NewGuid();
        var strategyVersionId = Guid.NewGuid();
        var strategySignalId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var createdAtUtc = new DateTime(2026, 4, 5, 12, 0, 0, DateTimeKind.Utc);
        var filledAtUtc = createdAtUtc.AddMinutes(2);

        context.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = ownerUserId,
            ExchangeName = "Binance",
            DisplayName = "Spot Primary",
            CredentialStatus = ExchangeCredentialStatus.Active
        });
        context.ExchangeBalances.Add(new ExchangeBalance
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
        context.ExchangeAccountSyncStates.Add(new ExchangeAccountSyncState
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
        context.MarketScannerHandoffAttempts.Add(new MarketScannerHandoffAttempt
        {
            Id = Guid.NewGuid(),
            ScanCycleId = Guid.NewGuid(),
            SelectedCandidateId = Guid.NewGuid(),
            SelectedSymbol = "BTCUSDT",
            SelectedTimeframe = "1m",
            SelectedAtUtc = createdAtUtc,
            CandidateRank = 1,
            CandidateMarketScore = 100m,
            CandidateScore = 180m,
            SelectionReason = "Top-ranked eligible candidate selected. Symbol=BTCUSDT; Rank=1.",
            OwnerUserId = ownerUserId,
            StrategyKey = "spot-portfolio",
            TradingStrategyId = strategyId,
            TradingStrategyVersionId = strategyVersionId,
            StrategySignalId = strategySignalId,
            StrategyDecisionOutcome = "Persisted",
            StrategyScore = 80,
            RiskOutcome = "Allowed",
            RiskVetoReasonCode = "None",
            RiskSummary = "Reason=None; Scope=User:user-spot-portfolio-01/Bot:n/a/Symbol:BTCUSDT/Coin:BTC/Timeframe:1m.",
            ExecutionRequestStatus = "Prepared",
            ExecutionSide = ExecutionOrderSide.Buy,
            ExecutionOrderType = ExecutionOrderType.Market,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutionQuantity = 2m,
            ExecutionPrice = 150m,
            GuardSummary = "ExecutionGate=Allowed; UserExecutionOverride=Allowed;",
            CorrelationId = "root-spot-portfolio",
            CompletedAtUtc = createdAtUtc
        });
        context.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = orderId,
            OwnerUserId = ownerUserId,
            TradingStrategyId = strategyId,
            TradingStrategyVersionId = strategyVersionId,
            StrategySignalId = strategySignalId,
            SignalType = StrategySignalType.Entry,
            ExchangeAccountId = exchangeAccountId,
            Plane = ExchangeDataPlane.Spot,
            StrategyKey = "spot-portfolio",
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
            IdempotencyKey = "spot-portfolio-idem",
            RootCorrelationId = "root-spot-portfolio",
            ExternalOrderId = "spot-order-portfolio-1",
            SubmittedToBroker = true,
            LastStateChangedAtUtc = filledAtUtc
        });
        context.ExecutionOrderTransitions.Add(new ExecutionOrderTransition
        {
            OwnerUserId = ownerUserId,
            ExecutionOrderId = orderId,
            SequenceNumber = 1,
            State = ExecutionOrderState.Filled,
            EventCode = "ExchangeFilled",
            Detail = "ClientOrderId=cb_spot_portfolio_01; Plane=Spot; ExchangeStatus=FILLED",
            CorrelationId = "transition-spot-portfolio",
            ParentCorrelationId = "root-spot-portfolio",
            OccurredAtUtc = filledAtUtc
        });
        context.DecisionTraces.Add(new DecisionTrace
        {
            Id = Guid.NewGuid(),
            StrategySignalId = strategySignalId,
            CorrelationId = "root-spot-portfolio",
            DecisionId = "decision-spot-portfolio",
            UserId = ownerUserId,
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            StrategyVersion = "1",
            SignalType = "Entry",
            RiskScore = 80,
            DecisionOutcome = "Allowed",
            LatencyMs = 12,
            SnapshotJson = "{}",
            CreatedAtUtc = createdAtUtc
        });
        context.ExecutionTraces.Add(new ExecutionTrace
        {
            Id = Guid.NewGuid(),
            ExecutionOrderId = orderId,
            CorrelationId = "root-spot-portfolio",
            ExecutionAttemptId = "exec-attempt-spot-portfolio",
            CommandId = "cmd-spot-portfolio",
            UserId = ownerUserId,
            Provider = "Binance.SpotPrivateRest",
            Endpoint = "/api/v3/order",
            ResponseMasked = "Accepted",
            CreatedAtUtc = filledAtUtc
        });
        context.AuditLogs.AddRange(
            new AuditLog
            {
                Id = Guid.NewGuid(),
                Actor = "system",
                Action = "ExecutionOrder.ExchangeUpdate",
                Target = $"ExecutionOrder/{orderId}",
                Context = "Plane=Spot",
                CorrelationId = "root-spot-portfolio",
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
                CorrelationId = "root-spot-portfolio",
                Outcome = "Applied",
                Environment = "Live"
            });
        context.SpotPortfolioFills.AddRange(
            new SpotPortfolioFill
            {
                OwnerUserId = ownerUserId,
                ExchangeAccountId = exchangeAccountId,
                ExecutionOrderId = orderId,
                Plane = ExchangeDataPlane.Spot,
                Symbol = "BTCUSDT",
                BaseAsset = "BTC",
                QuoteAsset = "USDT",
                Side = ExecutionOrderSide.Buy,
                ExchangeOrderId = "spot-order-portfolio-1",
                ClientOrderId = "cb_spot_portfolio_01",
                TradeId = 1,
                Quantity = 1m,
                QuoteQuantity = 100m,
                Price = 100m,
                FeeAmountInQuote = 0m,
                RealizedPnlDelta = 0m,
                HoldingQuantityAfter = 1m,
                HoldingCostBasisAfter = 100m,
                HoldingAverageCostAfter = 100m,
                CumulativeRealizedPnlAfter = 0m,
                CumulativeFeesInQuoteAfter = 0m,
                Source = "Binance.SpotPrivateRest.MyTrades",
                RootCorrelationId = "root-spot-portfolio",
                OccurredAtUtc = createdAtUtc
            },
            new SpotPortfolioFill
            {
                OwnerUserId = ownerUserId,
                ExchangeAccountId = exchangeAccountId,
                ExecutionOrderId = orderId,
                Plane = ExchangeDataPlane.Spot,
                Symbol = "BTCUSDT",
                BaseAsset = "BTC",
                QuoteAsset = "USDT",
                Side = ExecutionOrderSide.Buy,
                ExchangeOrderId = "spot-order-portfolio-1",
                ClientOrderId = "cb_spot_portfolio_01",
                TradeId = 2,
                Quantity = 1m,
                QuoteQuantity = 200m,
                Price = 200m,
                FeeAmountInQuote = 0m,
                RealizedPnlDelta = 0m,
                HoldingQuantityAfter = 2m,
                HoldingCostBasisAfter = 300m,
                HoldingAverageCostAfter = 150m,
                CumulativeRealizedPnlAfter = 0m,
                CumulativeFeesInQuoteAfter = 0m,
                Source = "Binance.SpotPrivateRest.MyTrades",
                RootCorrelationId = "root-spot-portfolio",
                OccurredAtUtc = createdAtUtc.AddSeconds(1)
            },
            new SpotPortfolioFill
            {
                OwnerUserId = ownerUserId,
                ExchangeAccountId = exchangeAccountId,
                ExecutionOrderId = orderId,
                Plane = ExchangeDataPlane.Spot,
                Symbol = "BTCUSDT",
                BaseAsset = "BTC",
                QuoteAsset = "USDT",
                Side = ExecutionOrderSide.Sell,
                ExchangeOrderId = "spot-order-portfolio-1",
                ClientOrderId = "cb_spot_portfolio_01",
                TradeId = 3,
                Quantity = 1m,
                QuoteQuantity = 250m,
                Price = 250m,
                FeeAsset = "USDT",
                FeeAmount = 10m,
                FeeAmountInQuote = 10m,
                RealizedPnlDelta = 90m,
                HoldingQuantityAfter = 1m,
                HoldingCostBasisAfter = 150m,
                HoldingAverageCostAfter = 150m,
                CumulativeRealizedPnlAfter = 90m,
                CumulativeFeesInQuoteAfter = 10m,
                Source = "Binance.SpotPrivateRest.MyTrades",
                RootCorrelationId = "root-spot-portfolio",
                OccurredAtUtc = filledAtUtc
            });
        await context.SaveChangesAsync();

        var service = new UserDashboardPortfolioReadModelService(
            context,
            new FakeMarketDataService(
                new Dictionary<string, decimal>(StringComparer.Ordinal)
                {
                    ["BTCUSDT"] = 300m
                }));

        var snapshot = await service.GetSnapshotAsync(ownerUserId);
        var holding = Assert.Single(snapshot.SpotHoldings!);
        var position = Assert.Single(snapshot.Positions, entity => entity.Plane == ExchangeDataPlane.Spot);
        var historyRow = Assert.Single(snapshot.TradeHistory, entity => entity.Plane == ExchangeDataPlane.Spot);

        Assert.Equal(90m, snapshot.RealizedPnl);
        Assert.Equal(150m, snapshot.UnrealizedPnl);
        Assert.Equal(240m, snapshot.TotalPnl);
        Assert.Equal(1m, holding.Quantity);
        Assert.Equal(0.75m, holding.AvailableQuantity);
        Assert.Equal(0.25m, holding.LockedQuantity);
        Assert.Equal(150m, holding.AverageCost);
        Assert.Equal(150m, holding.CostBasis);
        Assert.Equal(90m, holding.RealizedPnl);
        Assert.Equal(150m, holding.UnrealizedPnl);
        Assert.Equal(10m, holding.TotalFeesInQuote);
        Assert.Equal(150m, position.UnrealizedProfit);
        Assert.Equal(150m, position.EntryPrice);
        Assert.Equal(0.75m, position.AvailableQuantity);
        Assert.Equal(0.25m, position.LockedQuantity);
        Assert.Equal(90m, historyRow.RealizedPnl);
        Assert.Equal(150m, historyRow.UnrealizedPnlContribution);
        Assert.Equal(10m, historyRow.FeeAmountInQuote);
        Assert.Equal(3, historyRow.FillCount);
        Assert.Equal("1,2,3", historyRow.TradeIdsSummary);
        Assert.Contains("Plane=Spot", historyRow.ReasonChainSummary, StringComparison.Ordinal);
        Assert.Contains("FillCount=3", historyRow.ExecutionResultSummary, StringComparison.Ordinal);
        Assert.Contains("FeeInQuote=10", historyRow.ExecutionResultSummary, StringComparison.Ordinal);
    }


    [Fact]
    public async Task GetSnapshotAsync_ProjectsLiveFuturesPortfolioHistoryAndReconciliationParity()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var context = CreateContext(databaseRoot);
        var ownerUserId = "user-futures-portfolio-01";
        var exchangeAccountId = Guid.NewGuid();
        var strategyId = Guid.NewGuid();
        var strategyVersionId = Guid.NewGuid();
        var strategySignalId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var scanCycleId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var createdAtUtc = new DateTime(2026, 4, 5, 14, 0, 0, DateTimeKind.Utc);
        var filledAtUtc = createdAtUtc.AddMinutes(1);

        context.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = ownerUserId,
            ExchangeName = "Binance",
            DisplayName = "Futures Primary",
            CredentialStatus = ExchangeCredentialStatus.Active,
            ApiKeyCiphertext = "cipher-api-key",
            ApiSecretCiphertext = "cipher-api-secret"
        });
        context.ExchangeAccountSyncStates.Add(new ExchangeAccountSyncState
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
        context.ExchangePositions.Add(new ExchangePosition
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
        context.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = ownerUserId,
            Name = "Futures History Bot",
            StrategyKey = "futures-portfolio",
            Symbol = "BTCUSDT",
            IsEnabled = true
        });
        context.TradingStrategySignals.Add(new TradingStrategySignal
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
        context.MarketScannerCycles.Add(new MarketScannerCycle
        {
            Id = scanCycleId,
            StartedAtUtc = createdAtUtc.AddSeconds(-3),
            CompletedAtUtc = createdAtUtc,
            UniverseSource = "unit-test",
            ScannedSymbolCount = 1,
            EligibleCandidateCount = 1,
            TopCandidateCount = 1,
            BestCandidateSymbol = "BTCUSDT",
            BestCandidateScore = 240m,
            Summary = "unit-test"
        });
        context.MarketScannerCandidates.Add(new MarketScannerCandidate
        {
            Id = candidateId,
            ScanCycleId = scanCycleId,
            Symbol = "BTCUSDT",
            UniverseSource = "unit-test",
            ObservedAtUtc = createdAtUtc,
            IsEligible = true,
            Rank = 1,
            MarketScore = 140m,
            StrategyScore = 100,
            Score = 240m,
            ScoringSummary = "MarketScore=140; StrategyScore=100; CompositeScore=240"
        });
        context.MarketScannerHandoffAttempts.Add(new MarketScannerHandoffAttempt
        {
            Id = Guid.NewGuid(),
            ScanCycleId = scanCycleId,
            SelectedCandidateId = candidateId,
            SelectedSymbol = "BTCUSDT",
            SelectedTimeframe = "5m",
            SelectedAtUtc = createdAtUtc,
            CandidateRank = 1,
            CandidateMarketScore = 140m,
            CandidateScore = 240m,
            SelectionReason = "Top-ranked eligible candidate selected. Symbol=BTCUSDT; Rank=1.",
            OwnerUserId = ownerUserId,
            BotId = botId,
            StrategyKey = "futures-portfolio",
            TradingStrategyId = strategyId,
            TradingStrategyVersionId = strategyVersionId,
            StrategySignalId = strategySignalId,
            StrategyDecisionOutcome = "Persisted",
            StrategyScore = 100,
            RiskOutcome = "Allowed",
            RiskVetoReasonCode = "None",
            RiskSummary = "Reason=None; Scope=User:user-futures-portfolio-01/Bot:" + botId.ToString("N") + "/Symbol:BTCUSDT/Coin:BTC/Timeframe:5m.",
            ExecutionRequestStatus = "Prepared",
            ExecutionSide = ExecutionOrderSide.Buy,
            ExecutionOrderType = ExecutionOrderType.Market,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutionQuantity = 0.5m,
            ExecutionPrice = 60200m,
            GuardSummary = "ExecutionGate=Allowed; UserExecutionOverride=Allowed;",
            CorrelationId = "root-futures-portfolio",
            CompletedAtUtc = createdAtUtc
        });
        context.ExecutionOrders.Add(new ExecutionOrder
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
            StrategyKey = "futures-portfolio",
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
            IdempotencyKey = "futures-portfolio-idem",
            RootCorrelationId = "root-futures-portfolio",
            ExternalOrderId = "futures-portfolio-order-1",
            SubmittedToBroker = true,
            LastStateChangedAtUtc = filledAtUtc
        });
        context.ExecutionOrderTransitions.Add(new ExecutionOrderTransition
        {
            OwnerUserId = ownerUserId,
            ExecutionOrderId = orderId,
            SequenceNumber = 1,
            State = ExecutionOrderState.Filled,
            EventCode = "ExchangeFilled",
            Detail = "ClientOrderId=cb_futures_portfolio_01; Plane=Futures; ExchangeStatus=FILLED; ExecutedQuantity=0.5; CumulativeQuoteQuantity=30100; TradeId=77; Fee=USDT:3.5; ReconciliationStatus=InSync; ReconciliationSummary=Exchange state aligned.",
            CorrelationId = "transition-futures-portfolio",
            ParentCorrelationId = "root-futures-portfolio",
            OccurredAtUtc = filledAtUtc
        });
        context.DecisionTraces.Add(new DecisionTrace
        {
            Id = Guid.NewGuid(),
            StrategySignalId = strategySignalId,
            CorrelationId = "root-futures-portfolio",
            DecisionId = "decision-futures-portfolio",
            UserId = ownerUserId,
            Symbol = "BTCUSDT",
            Timeframe = "5m",
            StrategyVersion = "1",
            SignalType = "Entry",
            RiskScore = 100,
            DecisionOutcome = "Allowed",
            LatencyMs = 9,
            SnapshotJson = "{}",
            CreatedAtUtc = createdAtUtc
        });
        context.ExecutionTraces.Add(new ExecutionTrace
        {
            Id = Guid.NewGuid(),
            ExecutionOrderId = orderId,
            CorrelationId = "root-futures-portfolio",
            ExecutionAttemptId = "exec-futures-portfolio",
            CommandId = "cmd-futures-portfolio",
            UserId = ownerUserId,
            Provider = "Binance.FuturesPrivateRest",
            Endpoint = "/fapi/v1/order",
            ResponseMasked = "Accepted",
            CreatedAtUtc = filledAtUtc
        });
        context.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            Actor = "system",
            Action = "ExecutionOrder.ExchangeUpdate",
            Target = $"ExecutionOrder/{orderId}",
            Context = "Plane=Futures",
            CorrelationId = "root-futures-portfolio",
            Outcome = "Applied:StateChanged",
            Environment = "Live"
        });
        await context.SaveChangesAsync();

        var service = new UserDashboardPortfolioReadModelService(context);

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
    private static ApplicationDbContext CreateContext(InMemoryDatabaseRoot databaseRoot)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"), databaseRoot)
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
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
                    ? new MarketPriceSnapshot(symbol, price, DateTime.UtcNow, DateTime.UtcNow, "SpotPortfolioReadModelTest")
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


