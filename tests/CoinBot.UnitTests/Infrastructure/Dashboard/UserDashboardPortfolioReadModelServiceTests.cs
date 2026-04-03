using CoinBot.Application.Abstractions.DataScope;
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
}
