using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.DemoPortfolio;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.DemoPortfolio;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.DemoPortfolio;

public sealed class DemoSessionServiceTests
{
    [Fact]
    public async Task EnsureActiveSessionAsync_BootstrapsExistingWalletState_WithoutCreatingDrift()
    {
        await using var harness = CreateHarness();
        harness.DbContext.DemoWallets.Add(new DemoWallet
        {
            OwnerUserId = "user-bootstrap",
            Asset = "USDT",
            AvailableBalance = 1000m,
            ReservedBalance = 0m,
            LastActivityAtUtc = At(0)
        });
        await harness.DbContext.SaveChangesAsync();

        var session = await harness.Service.EnsureActiveSessionAsync("user-bootstrap");
        var checkedSession = await harness.Service.RunConsistencyCheckAsync("user-bootstrap");
        var transaction = await harness.DbContext.DemoLedgerTransactions.SingleAsync(entity => entity.OwnerUserId == "user-bootstrap");

        Assert.Equal(1, session.SequenceNumber);
        Assert.Equal(DemoConsistencyStatus.InSync, checkedSession!.ConsistencyStatus);
        Assert.Equal(DemoLedgerTransactionType.SessionBootstrapped, transaction.TransactionType);
    }

    [Fact]
    public async Task RunConsistencyCheckAsync_DetectsWalletDrift_AndStoresSummary()
    {
        await using var harness = CreateHarness();
        harness.DbContext.DemoSessions.Add(new DemoSession
        {
            OwnerUserId = "user-drift",
            SequenceNumber = 1,
            SeedAsset = "USDT",
            SeedAmount = 1000m,
            State = DemoSessionState.Active,
            ConsistencyStatus = DemoConsistencyStatus.Unknown,
            StartedAtUtc = At(0)
        });
        harness.DbContext.DemoWallets.Add(new DemoWallet
        {
            OwnerUserId = "user-drift",
            Asset = "USDT",
            AvailableBalance = 1000m,
            ReservedBalance = 0m,
            LastActivityAtUtc = At(1)
        });
        await harness.DbContext.SaveChangesAsync();

        var session = await harness.Service.RunConsistencyCheckAsync("user-drift");
        var auditLog = await harness.DbContext.AuditLogs.SingleAsync(entity => entity.Action == "DemoSession.DriftDetected");

        Assert.NotNull(session);
        Assert.Equal(DemoConsistencyStatus.DriftDetected, session!.ConsistencyStatus);
        Assert.Contains("WalletMismatches=1", session.LastDriftSummary, StringComparison.Ordinal);
        Assert.Equal("Detected", auditLog.Outcome);
    }

    [Fact]
    public async Task RunConsistencyCheckAsync_SeedsDefaultWallet_WhenActiveSessionHasNoPortfolioState()
    {
        await using var harness = CreateHarness();
        harness.DbContext.DemoSessions.Add(new DemoSession
        {
            OwnerUserId = "user-empty-session",
            SequenceNumber = 1,
            SeedAsset = "USDT",
            SeedAmount = 10000m,
            State = DemoSessionState.Active,
            ConsistencyStatus = DemoConsistencyStatus.Unknown,
            StartedAtUtc = At(0)
        });
        await harness.DbContext.SaveChangesAsync();

        var session = await harness.Service.RunConsistencyCheckAsync("user-empty-session");

        var wallet = await harness.DbContext.DemoWallets.SingleAsync(entity => entity.OwnerUserId == "user-empty-session");
        var transaction = await harness.DbContext.DemoLedgerTransactions.SingleAsync(entity => entity.OwnerUserId == "user-empty-session");
        Assert.NotNull(session);
        Assert.Equal(DemoConsistencyStatus.InSync, session!.ConsistencyStatus);
        Assert.Equal("USDT", wallet.Asset);
        Assert.Equal(10000m, wallet.AvailableBalance);
        Assert.Equal(0m, wallet.ReservedBalance);
        Assert.Equal(DemoLedgerTransactionType.WalletSeeded, transaction.TransactionType);
    }

    [Fact]
    public async Task RunConsistencyCheckAsync_IgnoresMissingReservation_DuringRecentFillSettlement()
    {
        await using var harness = CreateHarness();
        harness.TimeProvider.Advance(TimeSpan.FromMinutes(2));
        SeedSubmittedOrderWithConsumedReservation(harness, "user-inflight-fill", At(2));
        await harness.DbContext.SaveChangesAsync();

        var session = await harness.Service.RunConsistencyCheckAsync("user-inflight-fill");
        var driftAuditCount = await harness.DbContext.AuditLogs.CountAsync(entity => entity.Action == "DemoSession.DriftDetected");

        Assert.NotNull(session);
        Assert.Equal(DemoConsistencyStatus.InSync, session!.ConsistencyStatus);
        Assert.DoesNotContain("MissingReservation", session.LastDriftSummary ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(0, driftAuditCount);
    }

    [Fact]
    public async Task RunConsistencyCheckAsync_DetectsMissingReservation_WhenSubmittedOrderRemainsUnreservedAfterGrace()
    {
        await using var harness = CreateHarness();
        harness.TimeProvider.Advance(TimeSpan.FromMinutes(2));
        SeedSubmittedOrderWithConsumedReservation(harness, "user-stuck-fill", At(1));
        await harness.DbContext.SaveChangesAsync();

        var session = await harness.Service.RunConsistencyCheckAsync("user-stuck-fill");
        var auditLog = await harness.DbContext.AuditLogs.SingleAsync(entity => entity.Action == "DemoSession.DriftDetected");

        Assert.NotNull(session);
        Assert.Equal(DemoConsistencyStatus.DriftDetected, session!.ConsistencyStatus);
        Assert.Contains("MissingReservation", session.LastDriftSummary, StringComparison.Ordinal);
        Assert.Equal("Detected", auditLog.Outcome);
    }

    [Fact]
    public async Task ResetAsync_SeedsNewSession_ZeroesState_CancelsOpenOrders_AndWritesAudit()
    {
        await using var harness = CreateHarness();
        var botId = Guid.NewGuid();
        harness.DbContext.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = "user-reset",
            Name = "Demo Bot",
            StrategyKey = "demo-reset",
            IsEnabled = true,
            OpenOrderCount = 1,
            OpenPositionCount = 1
        });
        harness.DbContext.DemoSessions.Add(new DemoSession
        {
            OwnerUserId = "user-reset",
            SequenceNumber = 1,
            SeedAsset = "USDT",
            SeedAmount = 2500m,
            State = DemoSessionState.Active,
            ConsistencyStatus = DemoConsistencyStatus.InSync,
            StartedAtUtc = At(0)
        });
        harness.DbContext.DemoWallets.AddRange(
            new DemoWallet
            {
                OwnerUserId = "user-reset",
                Asset = "USDT",
                AvailableBalance = 400m,
                ReservedBalance = 100m,
                LastActivityAtUtc = At(1)
            },
            new DemoWallet
            {
                OwnerUserId = "user-reset",
                Asset = "BTC",
                AvailableBalance = 0.01m,
                ReservedBalance = 0m,
                LastActivityAtUtc = At(1)
            });
        harness.DbContext.DemoPositions.Add(new DemoPosition
        {
            OwnerUserId = "user-reset",
            BotId = botId,
            PositionScopeKey = "bot:reset",
            Symbol = "BTCUSDT",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Quantity = 0.01m,
            CostBasis = 500m,
            AverageEntryPrice = 50000m,
            RealizedPnl = 10m,
            UnrealizedPnl = 5m,
            TotalFeesInQuote = 1m,
            LastMarkPrice = 50500m
        });
        harness.DbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            OwnerUserId = "user-reset",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            BotId = botId,
            StrategyKey = "demo-reset",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Limit,
            Quantity = 0.01m,
            Price = 50000m,
            ExecutionEnvironment = ExecutionEnvironment.Demo,
            ExecutorKind = ExecutionOrderExecutorKind.Virtual,
            State = ExecutionOrderState.Submitted,
            IdempotencyKey = "reset-open-order",
            RootCorrelationId = "corr-reset-order",
            SubmittedAtUtc = At(1),
            LastStateChangedAtUtc = At(1)
        });
        await harness.DbContext.SaveChangesAsync();

        var session = await harness.Service.ResetAsync(
            new DemoSessionResetRequest(
                "user-reset",
                ExecutionEnvironment.Demo,
                Actor: "admin-reset",
                Reason: "Support reset",
                CorrelationId: "corr-reset-1"));

        var sessions = await harness.DbContext.DemoSessions
            .IgnoreQueryFilters()
            .OrderBy(entity => entity.SequenceNumber)
            .ToListAsync();
        var wallets = await harness.DbContext.DemoWallets
            .IgnoreQueryFilters()
            .Where(entity => entity.OwnerUserId == "user-reset")
            .OrderBy(entity => entity.Asset)
            .ToListAsync();
        var position = await harness.DbContext.DemoPositions
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.OwnerUserId == "user-reset");
        var order = await harness.DbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.OwnerUserId == "user-reset");
        var bot = await harness.DbContext.TradingBots
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.Id == botId);
        var auditLog = await harness.DbContext.AuditLogs.SingleAsync(entity => entity.Action == "DemoSession.ResetApplied");

        Assert.Equal(2, session.SequenceNumber);
        Assert.Equal(DemoConsistencyStatus.InSync, session.ConsistencyStatus);
        Assert.Equal(DemoSessionState.Closed, sessions[0].State);
        Assert.Equal(DemoSessionState.Active, sessions[1].State);
        Assert.Equal(10000m, wallets.Single(wallet => wallet.Asset == "USDT").AvailableBalance);
        Assert.Equal(0m, wallets.Single(wallet => wallet.Asset == "USDT").ReservedBalance);
        Assert.Equal(0m, wallets.Single(wallet => wallet.Asset == "BTC").AvailableBalance);
        Assert.Equal(0m, position.Quantity);
        Assert.Equal(ExecutionOrderState.Cancelled, order.State);
        Assert.Equal("DemoSessionReset", order.FailureCode);
        Assert.Equal(0, bot.OpenOrderCount);
        Assert.Equal(0, bot.OpenPositionCount);
        Assert.Equal("Applied", auditLog.Outcome);
        Assert.Equal(nameof(ExecutionEnvironment.Demo), auditLog.Environment);
    }

    [Fact]
    public async Task RunConsistencyCheckAsync_ReconcilesPositionLeak_FromFailedVirtualOrder()
    {
        await using var harness = CreateHarness();
        var botId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var positionScopeKey = $"bot:{botId:N}";
        harness.DbContext.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = "user-failed-leak",
            Name = "Demo Bot",
            StrategyKey = "demo-failed-leak",
            Symbol = "SOLUSDT",
            IsEnabled = true,
            OpenOrderCount = 0,
            OpenPositionCount = 1
        });
        harness.DbContext.DemoSessions.Add(new DemoSession
        {
            OwnerUserId = "user-failed-leak",
            SequenceNumber = 1,
            SeedAsset = "USDT",
            SeedAmount = 10000m,
            State = DemoSessionState.Active,
            ConsistencyStatus = DemoConsistencyStatus.InSync,
            StartedAtUtc = At(0)
        });
        harness.DbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = orderId,
            OwnerUserId = "user-failed-leak",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            BotId = botId,
            Plane = ExchangeDataPlane.Futures,
            StrategyKey = "demo-failed-leak",
            Symbol = "SOLUSDT",
            Timeframe = "1m",
            BaseAsset = "SOL",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.06m,
            Price = 86.9m,
            FilledQuantity = 0.06m,
            AverageFillPrice = 86.9m,
            LastFilledAtUtc = At(2),
            ExecutionEnvironment = ExecutionEnvironment.Demo,
            ExecutorKind = ExecutionOrderExecutorKind.Virtual,
            State = ExecutionOrderState.Failed,
            FailureCode = "VirtualWatchdogFailedClosed",
            FailureDetail = "Insufficient reserved demo balance for asset 'USDT'.",
            IdempotencyKey = "failed-leak-order",
            RootCorrelationId = "corr-failed-leak",
            SubmittedToBroker = true,
            LastStateChangedAtUtc = At(3),
            CreatedDate = At(2),
            UpdatedDate = At(3)
        });
        harness.DbContext.DemoPositions.Add(new DemoPosition
        {
            OwnerUserId = "user-failed-leak",
            BotId = botId,
            PositionScopeKey = positionScopeKey,
            Symbol = "SOLUSDT",
            BaseAsset = "SOL",
            QuoteAsset = "USDT",
            PositionKind = DemoPositionKind.Futures,
            MarginMode = DemoMarginMode.Cross,
            Leverage = 1m,
            Quantity = 0.06m,
            CostBasis = 5.214m,
            AverageEntryPrice = 86.9m,
            LastFilledAtUtc = At(2),
            LastMarkPrice = 86.7m,
            CreatedDate = At(2),
            UpdatedDate = At(3)
        });
        harness.DbContext.DemoLedgerTransactions.Add(new DemoLedgerTransaction
        {
            OwnerUserId = "user-failed-leak",
            OperationId = $"execution-fill:{orderId:N}:5",
            TransactionType = DemoLedgerTransactionType.FillApplied,
            BotId = botId,
            PositionScopeKey = positionScopeKey,
            OrderId = orderId.ToString("N"),
            Symbol = "SOLUSDT",
            BaseAsset = "SOL",
            QuoteAsset = "USDT",
            PositionKind = DemoPositionKind.Futures,
            MarginMode = DemoMarginMode.Cross,
            Leverage = 1m,
            Quantity = 0.06m,
            Price = 86.9m,
            PositionQuantityAfter = 0.06m,
            PositionCostBasisAfter = 5.214m,
            PositionAverageEntryPriceAfter = 86.9m,
            CumulativeRealizedPnlAfter = 0m,
            UnrealizedPnlAfter = 0m,
            CumulativeFeesInQuoteAfter = 0m,
            NetFundingInQuoteAfter = 0m,
            OccurredAtUtc = At(2),
            CreatedDate = At(2),
            UpdatedDate = At(2)
        });
        await harness.DbContext.SaveChangesAsync();

        var session = await harness.Service.RunConsistencyCheckAsync("user-failed-leak");

        var position = await harness.DbContext.DemoPositions.SingleAsync(entity => entity.BotId == botId);
        var bot = await harness.DbContext.TradingBots.SingleAsync(entity => entity.Id == botId);
        var order = await harness.DbContext.ExecutionOrders.SingleAsync(entity => entity.Id == orderId);
        var reconciliationTransaction = await harness.DbContext.DemoLedgerTransactions
            .SingleAsync(entity => entity.TransactionType == DemoLedgerTransactionType.Reconciled);
        var auditLog = await harness.DbContext.AuditLogs.SingleAsync(entity => entity.Action == "DemoSession.FailedOrderLeakReconciled");

        Assert.NotNull(session);
        Assert.Equal(DemoConsistencyStatus.InSync, session!.ConsistencyStatus);
        Assert.Equal(0m, position.Quantity);
        Assert.Equal(0m, position.CostBasis);
        Assert.Equal(0m, position.AverageEntryPrice);
        Assert.Null(position.LastFilledAtUtc);
        Assert.Equal(0, bot.OpenPositionCount);
        Assert.Equal(0, bot.OpenOrderCount);
        Assert.Equal(ExecutionOrderState.Failed, order.State);
        Assert.Equal("VirtualWatchdogFailedClosed", order.FailureCode);
        Assert.Equal(0m, order.FilledQuantity);
        Assert.Null(order.AverageFillPrice);
        Assert.Null(order.LastFilledAtUtc);
        Assert.Equal(orderId.ToString("N"), reconciliationTransaction.OrderId);
        Assert.Equal(0m, reconciliationTransaction.PositionQuantityAfter);
        Assert.Equal("Applied", auditLog.Outcome);
    }

    [Fact]
    public async Task RunConsistencyCheckAsync_ReconcilesWalletLeak_FromFailedVirtualOrderFillLedger()
    {
        await using var harness = CreateHarness();
        var botId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var seedTransactionId = Guid.NewGuid();
        var reserveTransactionId = Guid.NewGuid();
        var fillTransactionId = Guid.NewGuid();
        harness.DbContext.DemoSessions.Add(new DemoSession
        {
            OwnerUserId = "user-failed-wallet-leak",
            SequenceNumber = 1,
            SeedAsset = "USDT",
            SeedAmount = 10000m,
            State = DemoSessionState.Active,
            ConsistencyStatus = DemoConsistencyStatus.InSync,
            StartedAtUtc = At(0)
        });
        harness.DbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = orderId,
            OwnerUserId = "user-failed-wallet-leak",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            BotId = botId,
            StrategyKey = "demo-failed-wallet-leak",
            Symbol = "SOLUSDT",
            Timeframe = "1m",
            BaseAsset = "SOL",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.06m,
            Price = 86.9m,
            ExecutionEnvironment = ExecutionEnvironment.Demo,
            ExecutorKind = ExecutionOrderExecutorKind.Virtual,
            State = ExecutionOrderState.Failed,
            FailureCode = "VirtualWatchdogFailedClosed",
            FailureDetail = "Insufficient reserved demo balance for asset 'USDT'.",
            IdempotencyKey = "failed-wallet-leak-order",
            RootCorrelationId = "corr-failed-wallet-leak",
            SubmittedToBroker = true,
            LastStateChangedAtUtc = At(3),
            CreatedDate = At(2),
            UpdatedDate = At(3)
        });
        harness.DbContext.DemoWallets.AddRange(
            new DemoWallet
            {
                OwnerUserId = "user-failed-wallet-leak",
                Asset = "USDT",
                AvailableBalance = 9994.7855908m,
                ReservedBalance = 0m,
                LastActivityAtUtc = At(2)
            },
            new DemoWallet
            {
                OwnerUserId = "user-failed-wallet-leak",
                Asset = "SOL",
                AvailableBalance = 0.06m,
                ReservedBalance = 0m,
                LastActivityAtUtc = At(2)
            });
        harness.DbContext.DemoLedgerTransactions.AddRange(
            new DemoLedgerTransaction
            {
                Id = seedTransactionId,
                OwnerUserId = "user-failed-wallet-leak",
                OperationId = "demo-session:seed:user-failed-wallet-leak",
                TransactionType = DemoLedgerTransactionType.WalletSeeded,
                PositionScopeKey = "portfolio",
                OccurredAtUtc = At(0),
                CreatedDate = At(0),
                UpdatedDate = At(0)
            },
            new DemoLedgerTransaction
            {
                Id = reserveTransactionId,
                OwnerUserId = "user-failed-wallet-leak",
                OperationId = $"execution-reserve:{orderId:N}",
                TransactionType = DemoLedgerTransactionType.FundsReserved,
                PositionScopeKey = "portfolio",
                OrderId = orderId.ToString("N"),
                OccurredAtUtc = At(2),
                CreatedDate = At(2),
                UpdatedDate = At(2)
            },
            new DemoLedgerTransaction
            {
                Id = fillTransactionId,
                OwnerUserId = "user-failed-wallet-leak",
                OperationId = $"execution-fill:{orderId:N}:5",
                TransactionType = DemoLedgerTransactionType.FillApplied,
                BotId = botId,
                PositionScopeKey = $"bot:{botId:N}",
                OrderId = orderId.ToString("N"),
                Symbol = "SOLUSDT",
                BaseAsset = "SOL",
                QuoteAsset = "USDT",
                Quantity = 0.06m,
                Price = 86.9m,
                OccurredAtUtc = At(2),
                CreatedDate = At(2),
                UpdatedDate = At(2)
            });
        harness.DbContext.DemoLedgerEntries.AddRange(
            new DemoLedgerEntry
            {
                Id = Guid.NewGuid(),
                OwnerUserId = "user-failed-wallet-leak",
                DemoLedgerTransactionId = seedTransactionId,
                Asset = "USDT",
                AvailableDelta = 10000m,
                ReservedDelta = 0m,
                AvailableBalanceAfter = 10000m,
                ReservedBalanceAfter = 0m,
                CreatedDate = At(0),
                UpdatedDate = At(0)
            },
            new DemoLedgerEntry
            {
                Id = Guid.NewGuid(),
                OwnerUserId = "user-failed-wallet-leak",
                DemoLedgerTransactionId = reserveTransactionId,
                Asset = "USDT",
                AvailableDelta = -5.2144092m,
                ReservedDelta = 5.2144092m,
                AvailableBalanceAfter = 9994.7855908m,
                ReservedBalanceAfter = 5.2144092m,
                CreatedDate = At(2),
                UpdatedDate = At(2)
            },
            new DemoLedgerEntry
            {
                Id = Guid.NewGuid(),
                OwnerUserId = "user-failed-wallet-leak",
                DemoLedgerTransactionId = fillTransactionId,
                Asset = "USDT",
                AvailableDelta = 0m,
                ReservedDelta = -5.2144092m,
                AvailableBalanceAfter = 9994.7855908m,
                ReservedBalanceAfter = 0m,
                CreatedDate = At(2),
                UpdatedDate = At(2)
            },
            new DemoLedgerEntry
            {
                Id = Guid.NewGuid(),
                OwnerUserId = "user-failed-wallet-leak",
                DemoLedgerTransactionId = fillTransactionId,
                Asset = "SOL",
                AvailableDelta = 0.06m,
                ReservedDelta = 0m,
                AvailableBalanceAfter = 0.06m,
                ReservedBalanceAfter = 0m,
                CreatedDate = At(2),
                UpdatedDate = At(2)
            });
        await harness.DbContext.SaveChangesAsync();

        var session = await harness.Service.RunConsistencyCheckAsync("user-failed-wallet-leak");

        var wallets = await harness.DbContext.DemoWallets
            .Where(entity => entity.OwnerUserId == "user-failed-wallet-leak")
            .ToDictionaryAsync(entity => entity.Asset, StringComparer.Ordinal);
        var reconciliation = await harness.DbContext.DemoLedgerTransactions
            .SingleAsync(entity => entity.OperationId == $"demo-reconcile:failed-order-wallet:{orderId:N}");
        var reconciliationEntries = await harness.DbContext.DemoLedgerEntries
            .Where(entity => entity.DemoLedgerTransactionId == reconciliation.Id)
            .ToListAsync();
        var auditLog = await harness.DbContext.AuditLogs.SingleAsync(entity => entity.Action == "DemoSession.FailedOrderWalletLeakReconciled");

        Assert.NotNull(session);
        Assert.Equal(DemoConsistencyStatus.InSync, session!.ConsistencyStatus);
        Assert.Equal(10000m, wallets["USDT"].AvailableBalance);
        Assert.Equal(0m, wallets["USDT"].ReservedBalance);
        Assert.Equal(0m, wallets["SOL"].AvailableBalance);
        Assert.Equal(0m, wallets["SOL"].ReservedBalance);
        Assert.Contains(reconciliationEntries, entity => entity.Asset == "USDT" && entity.AvailableDelta == 5.2144092m && entity.ReservedDelta == 0m);
        Assert.Contains(reconciliationEntries, entity => entity.Asset == "SOL" && entity.AvailableDelta == -0.06m && entity.ReservedDelta == 0m);
        Assert.Equal("Applied", auditLog.Outcome);
    }

    [Fact]
    public async Task RunConsistencyCheckAsync_RefreshesStaleBotCounter_WhenPositionAlreadyCleared()
    {
        await using var harness = CreateHarness();
        var botId = Guid.NewGuid();
        harness.DbContext.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = "user-stale-counter",
            Name = "Demo Bot",
            StrategyKey = "demo-stale-counter",
            Symbol = "SOLUSDT",
            IsEnabled = true,
            OpenOrderCount = 0,
            OpenPositionCount = 1
        });
        harness.DbContext.DemoSessions.Add(new DemoSession
        {
            OwnerUserId = "user-stale-counter",
            SequenceNumber = 1,
            SeedAsset = "USDT",
            SeedAmount = 10000m,
            State = DemoSessionState.Active,
            ConsistencyStatus = DemoConsistencyStatus.InSync,
            StartedAtUtc = At(0)
        });
        harness.DbContext.DemoPositions.Add(new DemoPosition
        {
            OwnerUserId = "user-stale-counter",
            BotId = botId,
            PositionScopeKey = $"bot:{botId:N}",
            Symbol = "SOLUSDT",
            BaseAsset = "SOL",
            QuoteAsset = "USDT",
            Quantity = 0m,
            CostBasis = 0m,
            AverageEntryPrice = 0m,
            CreatedDate = At(2),
            UpdatedDate = At(3)
        });
        await harness.DbContext.SaveChangesAsync();

        var session = await harness.Service.RunConsistencyCheckAsync("user-stale-counter");

        var bot = await harness.DbContext.TradingBots.SingleAsync(entity => entity.Id == botId);
        Assert.NotNull(session);
        Assert.Equal(DemoConsistencyStatus.InSync, session!.ConsistencyStatus);
        Assert.Equal(0, bot.OpenPositionCount);
        Assert.Equal(0, bot.OpenOrderCount);
    }

    private static DateTime At(int minuteOffset)
    {
        return new DateTime(2026, 3, 22, 12, minuteOffset, 0, DateTimeKind.Utc);
    }

    private static void SeedSubmittedOrderWithConsumedReservation(
        TestHarness harness,
        string ownerUserId,
        DateTime fillLedgerCreatedAtUtc)
    {
        var botId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var reserveTransactionId = Guid.NewGuid();
        var fillTransactionId = Guid.NewGuid();

        harness.DbContext.DemoSessions.Add(new DemoSession
        {
            OwnerUserId = ownerUserId,
            SequenceNumber = 1,
            SeedAsset = "USDT",
            SeedAmount = 10000m,
            State = DemoSessionState.Active,
            ConsistencyStatus = DemoConsistencyStatus.InSync,
            StartedAtUtc = At(0)
        });
        harness.DbContext.DemoWallets.Add(new DemoWallet
        {
            OwnerUserId = ownerUserId,
            Asset = "USDT",
            AvailableBalance = -100m,
            ReservedBalance = 0m,
            LastActivityAtUtc = fillLedgerCreatedAtUtc,
            CreatedDate = fillLedgerCreatedAtUtc,
            UpdatedDate = fillLedgerCreatedAtUtc
        });
        harness.DbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = orderId,
            OwnerUserId = ownerUserId,
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            BotId = botId,
            StrategyKey = "demo-inflight-fill",
            Symbol = "SOLUSDT",
            Timeframe = "1m",
            BaseAsset = "SOL",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.06m,
            Price = 100m,
            ExecutionEnvironment = ExecutionEnvironment.Demo,
            ExecutorKind = ExecutionOrderExecutorKind.Virtual,
            State = ExecutionOrderState.Submitted,
            IdempotencyKey = $"submitted-{orderId:N}",
            RootCorrelationId = $"corr-{orderId:N}",
            SubmittedAtUtc = fillLedgerCreatedAtUtc,
            LastStateChangedAtUtc = fillLedgerCreatedAtUtc,
            CreatedDate = fillLedgerCreatedAtUtc,
            UpdatedDate = fillLedgerCreatedAtUtc
        });
        harness.DbContext.DemoLedgerTransactions.AddRange(
            new DemoLedgerTransaction
            {
                Id = reserveTransactionId,
                OwnerUserId = ownerUserId,
                OperationId = $"execution-reserve:{orderId:N}",
                TransactionType = DemoLedgerTransactionType.FundsReserved,
                PositionScopeKey = "portfolio",
                OrderId = orderId.ToString("N"),
                OccurredAtUtc = fillLedgerCreatedAtUtc,
                CreatedDate = fillLedgerCreatedAtUtc,
                UpdatedDate = fillLedgerCreatedAtUtc
            },
            new DemoLedgerTransaction
            {
                Id = fillTransactionId,
                OwnerUserId = ownerUserId,
                OperationId = $"execution-fill:{orderId:N}:1",
                TransactionType = DemoLedgerTransactionType.FillApplied,
                PositionScopeKey = $"bot:{botId:N}",
                OrderId = orderId.ToString("N"),
                Symbol = "SOLUSDT",
                BaseAsset = "SOL",
                QuoteAsset = "USDT",
                OccurredAtUtc = fillLedgerCreatedAtUtc,
                CreatedDate = fillLedgerCreatedAtUtc,
                UpdatedDate = fillLedgerCreatedAtUtc
            });
        harness.DbContext.DemoLedgerEntries.AddRange(
            new DemoLedgerEntry
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                DemoLedgerTransactionId = reserveTransactionId,
                Asset = "USDT",
                AvailableDelta = -100m,
                ReservedDelta = 100m,
                AvailableBalanceAfter = -100m,
                ReservedBalanceAfter = 100m,
                CreatedDate = fillLedgerCreatedAtUtc,
                UpdatedDate = fillLedgerCreatedAtUtc
            },
            new DemoLedgerEntry
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                DemoLedgerTransactionId = fillTransactionId,
                Asset = "USDT",
                AvailableDelta = 0m,
                ReservedDelta = -100m,
                AvailableBalanceAfter = -100m,
                ReservedBalanceAfter = 0m,
                CreatedDate = fillLedgerCreatedAtUtc,
                UpdatedDate = fillLedgerCreatedAtUtc
            });
    }

    private static TestHarness CreateHarness()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(At(0)));
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var auditLogService = new AuditLogService(dbContext, new CorrelationContextAccessor());
        var marketDataService = new FakeMarketDataService();
        var demoWalletValuationService = new DemoWalletValuationService(
            marketDataService,
            timeProvider,
            NullLogger<DemoWalletValuationService>.Instance);
        var service = new DemoSessionService(
            dbContext,
            new DemoConsistencyWatchdogService(
                dbContext,
                Options.Create(new DemoSessionOptions()),
                timeProvider,
                NullLogger<DemoConsistencyWatchdogService>.Instance),
            demoWalletValuationService,
            auditLogService,
            Options.Create(new DemoSessionOptions()),
            timeProvider,
            NullLogger<DemoSessionService>.Instance);

        return new TestHarness(dbContext, service, auditLogService, timeProvider);
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeMarketDataService : IMarketDataService
    {
        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<MarketPriceSnapshot?>(null);
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(null);
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class TestHarness(
        ApplicationDbContext dbContext,
        DemoSessionService service,
        IAuditLogService auditLogService,
        AdjustableTimeProvider timeProvider) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public DemoSessionService Service { get; } = service;

        public IAuditLogService AuditLogService { get; } = auditLogService;

        public AdjustableTimeProvider TimeProvider { get; } = timeProvider;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }
    }
}
