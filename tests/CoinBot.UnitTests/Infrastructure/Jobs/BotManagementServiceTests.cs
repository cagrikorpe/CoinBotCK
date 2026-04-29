using CoinBot.Application.Abstractions.Bots;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Strategies;
using Microsoft.AspNetCore.Identity;
using CoinBot.UnitTests.Infrastructure.Strategies;
using CoinBot.Infrastructure.Strategies;
using CoinBot.Contracts.Common;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Mfa;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Jobs;

public sealed class BotManagementServiceTests
{
    [Fact]
    public async Task CreateAsync_PersistsDirectionMode_OnTradingBot()
    {
        await using var dbContext = CreateDbContext();
        var ownerUserId = "user-bot-direction";
        var strategy = CreateStrategy(ownerUserId, "strategy-direction", hasPublishedVersion: true);
        var exchangeAccount = CreateExchangeAccount(ownerUserId, isActive: true, isWritable: true);
        dbContext.TradingStrategies.Add(strategy);
        dbContext.ExchangeAccounts.Add(exchangeAccount);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        var result = await service.CreateAsync(
            ownerUserId,
            new BotManagementSaveCommand(
                "Directional Bot",
                "strategy-direction",
                "BTCUSDT",
                [],
                0.01m,
                exchangeAccount.Id,
                1m,
                "ISOLATED",
                false,
                TradingBotDirectionMode.LongShort),
            "user:user-bot-direction");

        Assert.True(result.IsSuccessful);
        var bot = await dbContext.TradingBots.SingleAsync();
        Assert.Equal(TradingBotDirectionMode.LongShort, bot.DirectionMode);
    }
    [Fact]
    public async Task CreateAsync_PersistsPilotFields_AndStrategyAssignment()
    {
        await using var context = CreateContext();
        var ownerUserId = "user-bot-create";
        var exchangeAccountId = await SeedStrategyAndExchangeAccountAsync(context, ownerUserId, "pilot-create");
        var service = CreateService(context);

        var result = await service.CreateAsync(
            ownerUserId,
            new BotManagementSaveCommand(
                "Pilot Create",
                "pilot-create",
                "ETHUSDT",
                [],
                0.001m,
                exchangeAccountId,
                1m,
                "ISOLATED",
                false),
            $"user:{ownerUserId}");

        var bot = await context.TradingBots.SingleAsync();

        Assert.True(result.IsSuccessful);
        Assert.Equal("Pilot Create", bot.Name);
        Assert.Equal("pilot-create", bot.StrategyKey);
        Assert.Equal("ETHUSDT", bot.Symbol);
        Assert.Equal(0.001m, bot.Quantity);
        Assert.Equal(exchangeAccountId, bot.ExchangeAccountId);
        Assert.Equal(1m, bot.Leverage);
        Assert.Equal("ISOLATED", bot.MarginType);
        Assert.False(bot.IsEnabled);
    }

    [Fact]
    public async Task UpdateAsync_PersistsEditedFields_AndResetsSchedulerState_WhenBotRemainsEnabled()
    {
        await using var context = CreateContext();
        var ownerUserId = "user-bot-update";
        var exchangeAccountId = await SeedStrategyAndExchangeAccountAsync(context, ownerUserId, "pilot-update");
        var bot = await SeedBotAsync(context, ownerUserId, exchangeAccountId, isEnabled: true);
        var now = new DateTimeOffset(2026, 4, 2, 13, 0, 0, TimeSpan.Zero);
        context.BackgroundJobStates.Add(new BackgroundJobState
        {
            JobKey = $"bot-execution:{bot.Id:N}",
            JobType = BackgroundJobTypes.BotExecution,
            BotId = bot.Id,
            Status = BackgroundJobStatus.Failed,
            NextRunAtUtc = now.UtcDateTime.AddMinutes(10),
            LastErrorCode = "StrategyFailed",
            IdempotencyKey = "retry-key"
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, new FakeTimeProvider(now));

        var result = await service.UpdateAsync(
            ownerUserId,
            bot.Id,
            new BotManagementSaveCommand(
                "Pilot Update 2",
                "pilot-update",
                "BTCUSDT",
                [],
                0.002m,
                exchangeAccountId,
                1m,
                "ISOLATED",
                true),
            $"user:{ownerUserId}");

        var persistedBot = await context.TradingBots.SingleAsync(entity => entity.Id == bot.Id);
        var state = await context.BackgroundJobStates.SingleAsync(entity => entity.BotId == bot.Id);

        Assert.True(result.IsSuccessful);
        Assert.True(persistedBot.IsEnabled);
        Assert.Equal("Pilot Update 2", persistedBot.Name);
        Assert.Equal(0.002m, persistedBot.Quantity);
        Assert.Equal(BackgroundJobStatus.Pending, state.Status);
        Assert.Equal(now.UtcDateTime, state.NextRunAtUtc);
        Assert.Null(state.LastErrorCode);
        Assert.Null(state.IdempotencyKey);
    }


    [Fact]
    public async Task GetPageAsync_ExposesSingleScreenSignalOrderTraceParity()
    {
        await using var context = CreateContext();
        var ownerUserId = "user-op-parity";
        var exchangeAccountId = await SeedStrategyAndExchangeAccountAsync(context, ownerUserId, "pilot-op-parity");
        var bot = await SeedBotAsync(context, ownerUserId, exchangeAccountId, isEnabled: true);
        var strategy = await context.TradingStrategies.SingleAsync(entity => entity.OwnerUserId == ownerUserId && entity.StrategyKey == bot.StrategyKey);
        var entrySignalId = Guid.NewGuid();
        var exitSignalId = Guid.NewGuid();
        var entryGeneratedAtUtc = new DateTime(2026, 4, 2, 12, 10, 0, DateTimeKind.Utc);
        var exitGeneratedAtUtc = entryGeneratedAtUtc.AddMinutes(-1);

        context.TradingStrategySignals.AddRange(
            new TradingStrategySignal
            {
                Id = entrySignalId,
                OwnerUserId = ownerUserId,
                TradingStrategyId = strategy.Id,
                TradingStrategyVersionId = context.TradingStrategyVersions.Single(entity => entity.TradingStrategyId == strategy.Id).Id,
                StrategyVersionNumber = 1,
                StrategySchemaVersion = 1,
                SignalType = StrategySignalType.Entry,
                ExecutionEnvironment = ExecutionEnvironment.Live,
                Symbol = bot.Symbol!,
                Timeframe = "1m",
                IndicatorOpenTimeUtc = entryGeneratedAtUtc.AddMinutes(-1),
                IndicatorCloseTimeUtc = entryGeneratedAtUtc,
                IndicatorReceivedAtUtc = entryGeneratedAtUtc,
                GeneratedAtUtc = entryGeneratedAtUtc,
                IndicatorSnapshotJson = "{}",
                RuleResultSnapshotJson = "{}"
            },
            new TradingStrategySignal
            {
                Id = exitSignalId,
                OwnerUserId = ownerUserId,
                TradingStrategyId = strategy.Id,
                TradingStrategyVersionId = context.TradingStrategyVersions.Single(entity => entity.TradingStrategyId == strategy.Id).Id,
                StrategyVersionNumber = 1,
                StrategySchemaVersion = 1,
                SignalType = StrategySignalType.Exit,
                ExecutionEnvironment = ExecutionEnvironment.Live,
                Symbol = bot.Symbol!,
                Timeframe = "1m",
                IndicatorOpenTimeUtc = exitGeneratedAtUtc.AddMinutes(-1),
                IndicatorCloseTimeUtc = exitGeneratedAtUtc,
                IndicatorReceivedAtUtc = exitGeneratedAtUtc,
                GeneratedAtUtc = exitGeneratedAtUtc,
                IndicatorSnapshotJson = "{}",
                RuleResultSnapshotJson = "{}"
            });

        context.DecisionTraces.AddRange(
            new DecisionTrace
            {
                Id = Guid.NewGuid(),
                UserId = ownerUserId,
                CorrelationId = "corr-entry-op",
                DecisionId = "decision-entry-op",
                StrategySignalId = entrySignalId,
                Symbol = bot.Symbol!,
                Timeframe = "1m",
                StrategyVersion = "StrategyVersion:test",
                SignalType = StrategySignalType.Entry.ToString(),
                DecisionOutcome = "Skipped",
                DecisionReasonCode = "LongEntryRegimeFilterBlocked",
                DecisionSummary = "Entry blocked by regime filter.",
                DecisionAtUtc = entryGeneratedAtUtc.AddSeconds(1),
                SnapshotJson = "{}",
                CreatedAtUtc = entryGeneratedAtUtc.AddSeconds(1)
            },
            new DecisionTrace
            {
                Id = Guid.NewGuid(),
                UserId = ownerUserId,
                CorrelationId = "corr-exit-op",
                DecisionId = "decision-exit-op",
                StrategySignalId = exitSignalId,
                Symbol = bot.Symbol!,
                Timeframe = "1m",
                StrategyVersion = "StrategyVersion:test",
                SignalType = StrategySignalType.Exit.ToString(),
                DecisionOutcome = "Persisted",
                DecisionReasonCode = "Allowed",
                DecisionSummary = "Exit signal reached order stage.",
                DecisionAtUtc = exitGeneratedAtUtc.AddSeconds(1),
                SnapshotJson = "{}",
                CreatedAtUtc = exitGeneratedAtUtc.AddSeconds(1)
            });

        context.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            BotId = bot.Id,
            StrategySignalId = exitSignalId,
            TradingStrategyId = strategy.Id,
            TradingStrategyVersionId = context.TradingStrategyVersions.Single(entity => entity.TradingStrategyId == strategy.Id).Id,
            SignalType = StrategySignalType.Exit,
            StrategyKey = bot.StrategyKey,
            Symbol = bot.Symbol!,
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Sell,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.001m,
            Price = 62000m,
            State = ExecutionOrderState.Filled,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            SubmittedToBroker = true,
            IdempotencyKey = "idemp-op",
            RootCorrelationId = "corr-exit-op",
            CreatedDate = exitGeneratedAtUtc.AddSeconds(2),
            UpdatedDate = exitGeneratedAtUtc.AddSeconds(3),
            LastStateChangedAtUtc = exitGeneratedAtUtc.AddSeconds(3)
        });

        await context.SaveChangesAsync();
        var service = CreateService(context, new FakeTimeProvider(new DateTimeOffset(entryGeneratedAtUtc.AddMinutes(1))));

        var snapshot = await service.GetPageAsync(ownerUserId);
        var row = Assert.Single(snapshot.Bots);

        Assert.Equal("Entry", row.LatestSignalType);
        Assert.Equal(entryGeneratedAtUtc, row.LatestSignalGeneratedAtUtc);
        Assert.Equal("Skipped", row.LatestRuntimeDecisionOutcome);
        Assert.Equal("LongEntryRegimeFilterBlocked", row.LatestRuntimeDecisionReasonCode);
        Assert.Equal("Entry blocked by regime filter.", row.LatestRuntimeDecisionSummary);
        Assert.Null(row.LatestOrderState);
        Assert.Equal("LongEntryRegimeFilterBlocked", row.LatestOrderFailureCode);
        Assert.Equal(1, row.EntryGeneratedCount);
        Assert.Equal(1, row.EntrySkippedCount);
        Assert.Equal(0, row.EntryVetoedCount);
        Assert.Equal(0, row.EntryOrderedCount);
        Assert.Equal(0, row.EntryFilledCount);
        Assert.Equal(1, row.ExitGeneratedCount);
        Assert.Equal(0, row.ExitSkippedCount);
        Assert.Equal(0, row.ExitVetoedCount);
        Assert.Equal(1, row.ExitOrderedCount);
        Assert.Equal(1, row.ExitFilledCount);
    }

    [Fact]
    public async Task GetPageAsync_ExposesLongRegimePolicyAndLiveMetrics()
    {
        await using var context = CreateContext();
        var exchangeAccountId = await SeedStrategyAndExchangeAccountAsync(context, "user-regime-page", "pilot-regime-page");
        var bot = await SeedBotAsync(context, "user-regime-page", exchangeAccountId, isEnabled: true);
        context.TradingFeatureSnapshots.Add(new TradingFeatureSnapshot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = bot.OwnerUserId,
            BotId = bot.Id,
            ExchangeAccountId = exchangeAccountId,
            StrategyKey = bot.StrategyKey,
            Symbol = bot.Symbol!,
            Timeframe = "1m",
            EvaluatedAtUtc = new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc),
            MarketDataTimestampUtc = new DateTime(2026, 4, 2, 11, 59, 59, DateTimeKind.Utc),
            FeatureVersion = "v1",
            SnapshotState = FeatureSnapshotState.Ready,
            QualityReasonCode = FeatureSnapshotQualityReason.None,
            MarketDataReasonCode = DegradedModeReasonCode.None,
            SampleCount = 200,
            RequiredSampleCount = 200,
            ReferencePrice = 64000m,
            Rsi = 32m,
            MacdHistogram = -0.0077m,
            BollingerBandWidth = 0.0826m,
            LastDecisionOutcome = "Skipped",
            LastDecisionCode = "LongEntryRegimeFilterBlocked",
            LastExecutionState = "Skipped",
            FeatureSummary = "Long regime blocked."
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, new FakeTimeProvider(new DateTimeOffset(new DateTime(2026, 4, 2, 12, 0, 2, DateTimeKind.Utc))));

        var snapshot = await service.GetPageAsync("user-regime-page");
        var row = Assert.Single(snapshot.Bots);

        Assert.Equal("BLOCKED NOW", row.LongRegimeGateLabel);
        Assert.Equal("danger", row.LongRegimeGateTone);
        Assert.Contains("MACD hist >= 0", row.LongRegimePolicySummary, StringComparison.Ordinal);
        Assert.Contains("Bollinger width >= 0.2%", row.LongRegimePolicySummary, StringComparison.Ordinal);
        Assert.Contains("MACD hist=-0.0077", row.LongRegimeLiveSummary, StringComparison.Ordinal);
        Assert.Contains("Bollinger width=0.0826%", row.LongRegimeLiveSummary, StringComparison.Ordinal);
        Assert.Contains("MACD histogram -0.0077 < 0", row.LongRegimeExplainSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAsync_ReturnsNotFound_WhenBotBelongsToAnotherUser()
    {
        await using var context = CreateContext();
        var exchangeAccountId = await SeedStrategyAndExchangeAccountAsync(context, "owner-a", "pilot-owner");
        var bot = await SeedBotAsync(context, "owner-a", exchangeAccountId, isEnabled: false);
        var service = CreateService(context);

        var result = await service.UpdateAsync(
            "owner-b",
            bot.Id,
            new BotManagementSaveCommand(
                "Pilot",
                "pilot-owner",
                "BTCUSDT",
                [],
                null,
                exchangeAccountId,
                1m,
                "ISOLATED",
                false),
            "user:owner-b");

        Assert.False(result.IsSuccessful);
        Assert.Equal("BotNotFound", result.FailureCode);
    }

    [Fact]
    public async Task GetPageAsync_ReturnsOwnedBots_WithOperationalState()
    {
        await using var context = CreateContext();
        var exchangeAccountId = await SeedStrategyAndExchangeAccountAsync(context, "user-page", "pilot-page");
        var bot = await SeedBotAsync(context, "user-page", exchangeAccountId, isEnabled: true);
        var executionOrderId = Guid.NewGuid();
        context.BackgroundJobStates.Add(new BackgroundJobState
        {
            JobKey = $"bot-execution:{bot.Id:N}",
            JobType = BackgroundJobTypes.BotExecution,
            BotId = bot.Id,
            Status = BackgroundJobStatus.RetryPending,
            NextRunAtUtc = DateTime.UtcNow.AddMinutes(1),
            LastErrorCode = "ReferencePriceUnavailable"
        });
        context.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = executionOrderId,
            OwnerUserId = bot.OwnerUserId,
            BotId = bot.Id,
            StrategyKey = bot.StrategyKey,
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.001m,
            Price = 60000m,
            State = ExecutionOrderState.Rejected,
            FailureCode = "UserExecutionBotCooldownActive",
            FailureDetail = "Cache entry must specify a value for Size when SizeLimit is set.",
            RejectionStage = ExecutionRejectionStage.PreSubmit,
            SubmittedToBroker = false,
            RetryEligible = false,
            CooldownApplied = true,
            ReduceOnly = true,
            StopLossPrice = 59000m,
            DuplicateSuppressed = true,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            CreatedDate = DateTime.UtcNow.AddSeconds(-30),
            UpdatedDate = DateTime.UtcNow.AddSeconds(-29),
            LastStateChangedAtUtc = DateTime.UtcNow.AddSeconds(-29)
        });
        context.ExecutionOrderTransitions.Add(new ExecutionOrderTransition
        {
            Id = Guid.NewGuid(),
            OwnerUserId = bot.OwnerUserId,
            ExecutionOrderId = executionOrderId,
            SequenceNumber = 4,
            State = ExecutionOrderState.Rejected,
            EventCode = "UserExecutionOverrideBlocked",
            Detail = "Execution blocked because the bot cooldown is still active.\r\n",
            CorrelationId = "corr-user-page",
            OccurredAtUtc = DateTime.UtcNow.AddSeconds(-29)
        });
        await context.SaveChangesAsync();
        var persistedOrder = await context.ExecutionOrders
            .AsNoTracking()
            .SingleAsync(entity => entity.Id == executionOrderId);
        var utcNow = DateTime.SpecifyKind(
            persistedOrder.CreatedDate.AddSeconds(30),
            DateTimeKind.Utc);
        var service = CreateService(context, new FakeTimeProvider(new DateTimeOffset(utcNow)));

        var snapshot = await service.GetPageAsync("user-page");
        var row = Assert.Single(snapshot.Bots);

        Assert.Equal(bot.Id, row.BotId);
        Assert.Equal("RetryPending", row.LastJobStatus);
        Assert.Equal("ReferencePriceUnavailable", row.LastJobErrorCode);
        Assert.Equal("Rejected", row.LastExecutionState);
        Assert.Equal("UserExecutionBotCooldownActive", row.LastExecutionFailureCode);
        Assert.Equal("Execution blocked because the bot cooldown is still active.", row.LastExecutionBlockDetail);
        Assert.Equal("PreSubmit", row.LastExecutionRejectionStage);
        Assert.False(row.LastExecutionSubmittedToBroker);
        Assert.False(row.LastExecutionRetryEligible);
        Assert.True(row.LastExecutionCooldownApplied);
        Assert.True(row.LastExecutionReduceOnly);
        Assert.True(row.LastExecutionStopLossAttached);
        Assert.False(row.LastExecutionTakeProfitAttached);
        Assert.True(row.LastExecutionDuplicateSuppressed);
        Assert.Equal("UserExecutionOverrideBlocked", row.LastExecutionTransitionCode);
        Assert.Equal("corr-user-page", row.LastExecutionTransitionCorrelationId);
        Assert.Equal(DateTime.SpecifyKind(persistedOrder.CreatedDate.AddSeconds(120), DateTimeKind.Utc), row.CooldownBlockedUntilUtc);
        Assert.Equal(90, row.CooldownRemainingSeconds);
    }

    [Fact]
    public async Task GetPageAsync_UsesLatestFeatureSnapshotDecision_WhenNoExecutionOrderExists()
    {
        await using var context = CreateContext();
        var ownerUserId = "user-feature-veto";
        var exchangeAccountId = await SeedStrategyAndExchangeAccountAsync(context, ownerUserId, "pilot-feature-veto");
        var bot = await SeedBotAsync(context, ownerUserId, exchangeAccountId, isEnabled: true);
        var evaluatedAtUtc = new DateTime(2026, 4, 2, 14, 0, 0, DateTimeKind.Utc);
        context.BackgroundJobStates.Add(new BackgroundJobState
        {
            JobKey = $"bot-execution:{bot.Id:N}",
            JobType = BackgroundJobTypes.BotExecution,
            BotId = bot.Id,
            Status = BackgroundJobStatus.Succeeded,
            LastStartedAtUtc = evaluatedAtUtc,
            LastCompletedAtUtc = evaluatedAtUtc.AddSeconds(1),
            LastHeartbeatAtUtc = evaluatedAtUtc.AddSeconds(1)
        });
        context.TradingFeatureSnapshots.Add(new TradingFeatureSnapshot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            BotId = bot.Id,
            ExchangeAccountId = exchangeAccountId,
            StrategyKey = bot.StrategyKey,
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            EvaluatedAtUtc = evaluatedAtUtc,
            MarketDataTimestampUtc = evaluatedAtUtc.AddSeconds(-1),
            FeatureVersion = "test",
            SnapshotState = FeatureSnapshotState.Ready,
            MarketDataReasonCode = DegradedModeReasonCode.None,
            SampleCount = 240,
            RequiredSampleCount = 200,
            LastDecisionOutcome = "Blocked",
            LastDecisionCode = "RiskProfileMissing",
            LastExecutionState = "Vetoed",
            FeatureSummary = "State=Ready; LastVeto:RiskProfileMissing",
            CreatedDate = evaluatedAtUtc,
            UpdatedDate = evaluatedAtUtc.AddSeconds(1)
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, new FakeTimeProvider(new DateTimeOffset(evaluatedAtUtc.AddSeconds(2))));

        var snapshot = await service.GetPageAsync(ownerUserId);
        var row = Assert.Single(snapshot.Bots);

        Assert.Equal("Succeeded", row.LastJobStatus);
        Assert.Equal("Vetoed", row.LastExecutionState);
        Assert.Equal("Blocked", row.LastExecutionDecisionOutcome);
        Assert.Equal("RiskVeto", row.LastExecutionDecisionReasonType);
        Assert.Equal("RiskProfileMissing", row.LastExecutionDecisionReasonCode);
        Assert.Equal(evaluatedAtUtc, row.LastExecutionDecisionAtUtc);
        Assert.Equal(evaluatedAtUtc.AddSeconds(-1), row.LastExecutionLastCandleAtUtc);
        Assert.NotNull(row.LastExecutionUpdatedAtUtc);
    }

    [Fact]
    public async Task GetPageAsync_IgnoresOlderExecutionOrderFailure_WhenFeatureSnapshotIsNewer()
    {
        await using var context = CreateContext();
        var ownerUserId = "user-feature-current";
        var exchangeAccountId = await SeedStrategyAndExchangeAccountAsync(context, ownerUserId, "pilot-feature-current");
        var bot = await SeedBotAsync(context, ownerUserId, exchangeAccountId, isEnabled: true);
        var orderId = Guid.NewGuid();
        var orderCreatedAtUtc = new DateTime(2026, 4, 2, 13, 10, 0, DateTimeKind.Utc);
        var evaluatedAtUtc = orderCreatedAtUtc.AddMinutes(30);

        context.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = orderId,
            OwnerUserId = ownerUserId,
            BotId = bot.Id,
            StrategyKey = bot.StrategyKey,
            Symbol = "ETHUSDT",
            Timeframe = "1m",
            BaseAsset = "ETH",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.01m,
            Price = 3000m,
            State = ExecutionOrderState.Rejected,
            FailureCode = "PrivatePlaneStale",
            FailureDetail = "Private account snapshot is stale.",
            RejectionStage = ExecutionRejectionStage.PreSubmit,
            SubmittedToBroker = false,
            RetryEligible = false,
            CooldownApplied = true,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            CreatedDate = orderCreatedAtUtc,
            UpdatedDate = orderCreatedAtUtc.AddSeconds(1),
            LastStateChangedAtUtc = orderCreatedAtUtc.AddSeconds(1)
        });
        context.ExecutionOrderTransitions.Add(new ExecutionOrderTransition
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            ExecutionOrderId = orderId,
            SequenceNumber = 1,
            State = ExecutionOrderState.Rejected,
            EventCode = "PrivatePlaneStale",
            Detail = "Execution blocked because private account snapshot is stale.",
            CorrelationId = "corr-stale-order",
            OccurredAtUtc = orderCreatedAtUtc.AddSeconds(1)
        });
        context.TradingFeatureSnapshots.Add(new TradingFeatureSnapshot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            BotId = bot.Id,
            ExchangeAccountId = exchangeAccountId,
            StrategyKey = bot.StrategyKey,
            Symbol = "ETHUSDT",
            Timeframe = "1m",
            EvaluatedAtUtc = evaluatedAtUtc,
            MarketDataTimestampUtc = evaluatedAtUtc.AddSeconds(-1),
            FeatureVersion = "test",
            SnapshotState = FeatureSnapshotState.Ready,
            MarketDataReasonCode = DegradedModeReasonCode.None,
            SampleCount = 240,
            RequiredSampleCount = 200,
            LastDecisionOutcome = "None",
            FeatureSummary = "State=Ready; LastDecision:None",
            CreatedDate = evaluatedAtUtc,
            UpdatedDate = evaluatedAtUtc.AddSeconds(1)
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, new FakeTimeProvider(new DateTimeOffset(evaluatedAtUtc.AddSeconds(2))));

        var snapshot = await service.GetPageAsync(ownerUserId);
        var row = Assert.Single(snapshot.Bots);

        Assert.Null(row.LastExecutionState);
        Assert.Null(row.LastExecutionFailureCode);
        Assert.Null(row.LastExecutionDecisionReasonCode);
        Assert.Null(row.LastExecutionDecisionReasonType);
        Assert.Null(row.LastExecutionDecisionSummary);
        Assert.Equal("None", row.LastExecutionDecisionOutcome);
        Assert.Equal(evaluatedAtUtc, row.LastExecutionDecisionAtUtc);
        Assert.True(row.LastExecutionUpdatedAtUtc >= evaluatedAtUtc);
        Assert.Equal(evaluatedAtUtc.AddSeconds(-1), row.LastExecutionLastCandleAtUtc);
        Assert.False(row.LastExecutionCooldownApplied);
    }

    [Fact]
    public async Task GetPageAsync_ProjectsSanitizedMarketDataDiagnostics_FromLatestTransitionDetail()
    {
        await using var context = CreateContext();
        var exchangeAccountId = await SeedStrategyAndExchangeAccountAsync(context, "user-market-diag", "pilot-market-diag");
        var bot = await SeedBotAsync(context, "user-market-diag", exchangeAccountId, isEnabled: true);
        var executionOrderId = Guid.NewGuid();
        var orderCreatedAtUtc = new DateTime(2026, 4, 2, 23, 52, 20, DateTimeKind.Utc);

        context.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = executionOrderId,
            OwnerUserId = bot.OwnerUserId,
            BotId = bot.Id,
            StrategyKey = bot.StrategyKey,
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.001m,
            Price = 60000m,
            State = ExecutionOrderState.Rejected,
            FailureCode = "ClockDriftExceeded",
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            CreatedDate = orderCreatedAtUtc,
            UpdatedDate = orderCreatedAtUtc.AddSeconds(1),
            LastStateChangedAtUtc = orderCreatedAtUtc.AddSeconds(1)
        });
        context.ExecutionOrderTransitions.Add(new ExecutionOrderTransition
        {
            Id = Guid.NewGuid(),
            OwnerUserId = bot.OwnerUserId,
            ExecutionOrderId = executionOrderId,
            SequenceNumber = 2,
            State = ExecutionOrderState.Rejected,
            EventCode = "GateRejected",
            Detail = "Execution blocked because clock drift exceeded the safety threshold. LatencyReason=ClockDriftExceeded; HeartbeatSource=binance:rest-backfill; Symbol=BTCUSDT; Timeframe=1m; LastCandleAtUtc=2026-04-02T23:51:59.9990000Z; ExpectedOpenTimeUtc=2026-04-02T23:52:00.0000000Z; DataAgeMs=20265; ClockDriftMs=19724; ContinuityGapCount=0; DecisionSourceLayer=heartbeat-watchdog; DecisionMethodName=ExecutionGate.EvaluateDataLatencyAsync.",
            CorrelationId = "corr-market-diag",
            OccurredAtUtc = orderCreatedAtUtc.AddSeconds(1),
            CreatedDate = orderCreatedAtUtc.AddSeconds(1),
            UpdatedDate = orderCreatedAtUtc.AddSeconds(1)
        });
        context.DegradedModeStates.Add(new DegradedModeState
        {
            Id = DegradedModeDefaults.ResolveStateId("BTCUSDT", "1m"),
            StateCode = DegradedModeStateCode.Normal,
            ReasonCode = DegradedModeReasonCode.None,
            SignalFlowBlocked = false,
            ExecutionFlowBlocked = false,
            LatestSymbol = "BTCUSDT",
            LatestTimeframe = "1m",
            LatestDataTimestampAtUtc = new DateTime(2026, 4, 2, 23, 51, 59, 999, DateTimeKind.Utc),
            LatestExpectedOpenTimeUtc = new DateTime(2026, 4, 2, 23, 52, 0, DateTimeKind.Utc),
            LatestContinuityGapCount = 0,
            LatestContinuityGapStartedAtUtc = new DateTime(2026, 4, 2, 23, 45, 0, DateTimeKind.Utc),
            LatestContinuityGapLastSeenAtUtc = new DateTime(2026, 4, 2, 23, 50, 0, DateTimeKind.Utc),
            LatestContinuityRecoveredAtUtc = new DateTime(2026, 4, 2, 23, 50, 30, DateTimeKind.Utc)
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, new FakeTimeProvider(new DateTimeOffset(orderCreatedAtUtc.AddMinutes(5))));

        var snapshot = await service.GetPageAsync("user-market-diag");
        var row = Assert.Single(snapshot.Bots);

        Assert.Equal("Execution blocked because clock drift exceeded the safety threshold.", row.LastExecutionBlockDetail);
        Assert.Equal(new DateTime(2026, 4, 2, 23, 51, 59, 999, DateTimeKind.Utc), row.LastExecutionLastCandleAtUtc);
        Assert.Equal(20265, row.LastExecutionDataAgeMilliseconds);
        Assert.Equal("Continuity OK", row.LastExecutionContinuityState);
        Assert.Equal(0, row.LastExecutionContinuityGapCount);
        Assert.Equal("Clock drift exceeded", row.LastExecutionStaleReason);
        Assert.Equal("BTCUSDT", row.LastExecutionAffectedSymbol);
        Assert.Equal("1m", row.LastExecutionAffectedTimeframe);
        Assert.Equal("Block", row.LastExecutionDecisionOutcome);
        Assert.Equal("StaleData", row.LastExecutionDecisionReasonType);
        Assert.Equal("ClockDriftExceeded", row.LastExecutionDecisionReasonCode);
        Assert.Equal("Execution blocked because clock drift exceeded the safety threshold.", row.LastExecutionDecisionSummary);
        Assert.Equal(3000, row.LastExecutionStaleThresholdMilliseconds);
        Assert.Equal(new DateTime(2026, 4, 2, 23, 45, 0, DateTimeKind.Utc), row.LastExecutionContinuityGapStartedAtUtc);
        Assert.Equal(new DateTime(2026, 4, 2, 23, 50, 0, DateTimeKind.Utc), row.LastExecutionContinuityGapLastSeenAtUtc);
        Assert.Equal(new DateTime(2026, 4, 2, 23, 50, 30, DateTimeKind.Utc), row.LastExecutionContinuityRecoveredAtUtc);
    }

    [Fact]
    public async Task GetPageAsync_RejectsRequestedOwnerOutsideCurrentScope()
    {
        await using var context = CreateContext(currentUserId: "user-scope-a", hasIsolationBypass: false);
        var service = CreateService(context);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetPageAsync("user-scope-b"));

        Assert.Contains("outside the authenticated isolation boundary", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_FailsClosed_WhenCriticalOperationAuthorizerRejectsRequest()
    {
        await using var context = CreateContext();
        var ownerUserId = "user-bot-mfa";
        var exchangeAccountId = await SeedStrategyAndExchangeAccountAsync(context, ownerUserId, "pilot-mfa");
        var service = CreateService(
            context,
            criticalUserOperationAuthorizer: new FakeCriticalUserOperationAuthorizer
            {
                Result = new CriticalUserOperationAuthorizationResult(
                    false,
                    "MfaRequired",
                    "Bu islem icin MFA zorunludur.")
            });

        var result = await service.CreateAsync(
            ownerUserId,
            new BotManagementSaveCommand(
                "Pilot Mfa",
                "pilot-mfa",
                "BTCUSDT",
                [],
                0.001m,
                exchangeAccountId,
                1m,
                "ISOLATED",
                false),
            $"user:{ownerUserId}");

        Assert.False(result.IsSuccessful);
        Assert.Equal("MfaRequired", result.FailureCode);
        Assert.Empty(context.TradingBots.Where(entity => entity.OwnerUserId == ownerUserId));
    }

    [Fact]
    public async Task GetCreateEditorAsync_ReturnsOwnedStrategies_AndAdminSharedTemplates()
    {
        var ownerUserId = "normal-user-picker";
        var databaseName = Guid.NewGuid().ToString("N");
        var databaseRoot = new InMemoryDatabaseRoot();
        await using (var seedContext = CreateContext(databaseName: databaseName, databaseRoot: databaseRoot))
        {
            await SeedStrategyTemplateAsync(seedContext, "platform-admin", "shared-admin-template", "Shared Admin Template", isPlatformAdminOwner: true);
            await SeedStrategyTemplateAsync(seedContext, "other-user", "other-private-template", "Other Private Template", isPlatformAdminOwner: false);
        }

        await using var context = CreateContext(currentUserId: ownerUserId, hasIsolationBypass: false, databaseName: databaseName, databaseRoot: databaseRoot);
        _ = await SeedStrategyAndExchangeAccountAsync(context, ownerUserId, "own-strategy");
        var service = CreateService(context);

        var editor = await service.GetCreateEditorAsync(ownerUserId);

        Assert.Contains(editor.StrategyOptions, option => option.StrategyKey == "own-strategy");
        Assert.Contains(editor.StrategyOptions, option =>
            option.StrategyKey == "shared-admin-template" &&
            option.DisplayName.Contains("Shared", StringComparison.Ordinal) &&
            option.HasPublishedVersion);
        Assert.DoesNotContain(editor.StrategyOptions, option => option.StrategyKey == "other-private-template");
    }

    [Fact]
    public async Task GetCreateEditorAsync_UsesDirectSharedTemplateProjection_WithoutFullCatalogLoad()
    {
        var ownerUserId = "normal-user-picker-fast";
        var databaseName = Guid.NewGuid().ToString("N");
        var databaseRoot = new InMemoryDatabaseRoot();
        await using (var seedContext = CreateContext(databaseName: databaseName, databaseRoot: databaseRoot))
        {
            await SeedStrategyTemplateAsync(seedContext, "platform-admin", "shared-fast-template", "Shared Fast Template", isPlatformAdminOwner: true);
        }

        await using var context = CreateContext(currentUserId: ownerUserId, hasIsolationBypass: false, databaseName: databaseName, databaseRoot: databaseRoot);
        var catalog = new ThrowingTemplateCatalogService();
        var service = CreateService(context, strategyTemplateCatalogService: catalog);

        var editor = await service.GetCreateEditorAsync(ownerUserId);

        Assert.Contains(editor.StrategyOptions, option => option.StrategyKey == "shared-fast-template");
        Assert.Equal(0, catalog.ListAsyncCallCount);
    }

    [Fact]
    public async Task GetCreateEditorAsync_MapsConfiguredScannerUniverseSymbols()
    {
        var ownerUserId = "normal-user-scanner-universe";
        await using var context = CreateContext(currentUserId: ownerUserId, hasIsolationBypass: false);
        _ = await SeedStrategyAndExchangeAccountAsync(context, ownerUserId, "own-strategy");
        var service = CreateService(
            context,
            marketDataOptions: new BinanceMarketDataOptions
            {
                SeedSymbols = ["solusdt", "btcusdt", "ethusdt"]
            },
            historicalGapFillerOptions: new HistoricalGapFillerOptions
            {
                Symbols = ["ETHUSDT", "BNBUSDT", "XRPUSDT"]
            });

        var editor = await service.GetCreateEditorAsync(ownerUserId);

        Assert.Equal(["BNBUSDT", "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT"], editor.ScannerUniverseSymbols);
        Assert.Equal("BTCUSDT", editor.Draft.Symbol);
    }

    [Fact]
    public async Task GetCreateEditorAsync_ReturnsEmptyScannerUniverse_WhenConfigIsMissing()
    {
        var ownerUserId = "normal-user-empty-universe";
        await using var context = CreateContext(currentUserId: ownerUserId, hasIsolationBypass: false);
        _ = await SeedStrategyAndExchangeAccountAsync(context, ownerUserId, "own-strategy");
        var service = CreateService(
            context,
            marketDataOptions: new BinanceMarketDataOptions(),
            historicalGapFillerOptions: new HistoricalGapFillerOptions());

        var editor = await service.GetCreateEditorAsync(ownerUserId);

        Assert.Empty(editor.ScannerUniverseSymbols);
        Assert.Equal("BTCUSDT", editor.Draft.Symbol);
    }

    [Fact]
    public async Task CreateAsync_PersistsNormalizedAllowedSymbolsCsv()
    {
        await using var context = CreateContext();
        var ownerUserId = "user-bot-allowed-symbols";
        var exchangeAccountId = await SeedStrategyAndExchangeAccountAsync(context, ownerUserId, "pilot-allowed-symbols");
        var service = CreateService(
            context,
            marketDataOptions: new BinanceMarketDataOptions
            {
                SeedSymbols = ["solusdt", "ethusdt"]
            });

        var result = await service.CreateAsync(
            ownerUserId,
            new BotManagementSaveCommand(
                "Scoped Bot",
                "pilot-allowed-symbols",
                "SOLUSDT",
                [" ethusdt ", "SOLUSDT", "solusdt"],
                0.001m,
                exchangeAccountId,
                1m,
                "ISOLATED",
                false),
            $"user:{ownerUserId}");

        var bot = await context.TradingBots.SingleAsync(entity => entity.OwnerUserId == ownerUserId);

        Assert.True(result.IsSuccessful);
        Assert.Equal("ETHUSDT,SOLUSDT", bot.AllowedSymbolsCsv);
    }

    [Fact]
    public async Task CreateAsync_Fails_WhenAllowedSymbolsContainScannerUniverseOutsideValue()
    {
        await using var context = CreateContext();
        var ownerUserId = "user-bot-allowed-symbols-invalid";
        var exchangeAccountId = await SeedStrategyAndExchangeAccountAsync(context, ownerUserId, "pilot-allowed-symbols-invalid");
        var service = CreateService(
            context,
            marketDataOptions: new BinanceMarketDataOptions
            {
                SeedSymbols = ["solusdt", "ethusdt"]
            });

        var result = await service.CreateAsync(
            ownerUserId,
            new BotManagementSaveCommand(
                "Scoped Bot Invalid",
                "pilot-allowed-symbols-invalid",
                "SOLUSDT",
                ["SOLUSDT", "ADAUSDT"],
                0.001m,
                exchangeAccountId,
                1m,
                "ISOLATED",
                false),
            $"user:{ownerUserId}");

        Assert.False(result.IsSuccessful);
        Assert.Equal("BotAllowedSymbolOutsideScannerUniverse", result.FailureCode);
        Assert.DoesNotContain(context.TradingBots, entity => entity.OwnerUserId == ownerUserId);
    }

    [Fact]
    public async Task UpdateAsync_KeepsPrimarySymbolFallback_WhenAllowedSymbolsAreCleared()
    {
        await using var context = CreateContext();
        var ownerUserId = "user-bot-allowed-symbols-clear";
        var exchangeAccountId = await SeedStrategyAndExchangeAccountAsync(context, ownerUserId, "pilot-allowed-symbols-clear");
        var bot = await SeedBotAsync(context, ownerUserId, exchangeAccountId, isEnabled: true);
        bot.AllowedSymbolsCsv = "ETHUSDT,SOLUSDT";
        await context.SaveChangesAsync();
        var service = CreateService(
            context,
            marketDataOptions: new BinanceMarketDataOptions
            {
                SeedSymbols = ["btcusdt", "ethusdt", "solusdt"]
            });

        var result = await service.UpdateAsync(
            ownerUserId,
            bot.Id,
            new BotManagementSaveCommand(
                bot.Name,
                bot.StrategyKey,
                bot.Symbol!,
                [],
                bot.Quantity,
                exchangeAccountId,
                bot.Leverage,
                bot.MarginType ?? "ISOLATED",
                bot.IsEnabled,
                bot.DirectionMode),
            $"user:{ownerUserId}");

        var updatedBot = await context.TradingBots.SingleAsync(entity => entity.Id == bot.Id);
        var editor = await service.GetEditEditorAsync(ownerUserId, bot.Id);

        Assert.True(result.IsSuccessful);
        Assert.Null(updatedBot.AllowedSymbolsCsv);
        Assert.NotNull(editor);
        Assert.Equal(["BTCUSDT"], editor!.Draft.AllowedSymbols);
    }

    [Fact]
    public async Task CreateAsync_UsesOwnedStrategySelection_WithoutMaterialization()
    {
        var ownerUserId = "normal-user-own-strategy";
        await using var context = CreateContext(currentUserId: ownerUserId, hasIsolationBypass: false);
        var exchangeAccountId = await SeedStrategyAndExchangeAccountAsync(context, ownerUserId, "own-create-strategy");
        var catalog = new ThrowingTemplateCatalogService();
        var service = CreateService(context, strategyTemplateCatalogService: catalog);

        var result = await service.CreateAsync(
            ownerUserId,
            new BotManagementSaveCommand(
                "Own Strategy Bot",
                "own-create-strategy",
                "BTCUSDT",
                [],
                0.001m,
                exchangeAccountId,
                1m,
                "ISOLATED",
                false),
            $"user:{ownerUserId}");

        Assert.True(result.IsSuccessful);
        Assert.Equal(0, catalog.GetAsyncCallCount);
        Assert.Equal(1, await context.TradingStrategies.CountAsync(entity => entity.OwnerUserId == ownerUserId));
        Assert.Equal(1, await context.TradingStrategyVersions.CountAsync(entity => entity.OwnerUserId == ownerUserId));
        Assert.Equal("own-create-strategy", await context.TradingBots
            .Where(entity => entity.OwnerUserId == ownerUserId)
            .Select(entity => entity.StrategyKey)
            .SingleAsync());
    }

    [Fact]
    public async Task CreateAsync_MaterializesAdminSharedTemplate_ForUserBotBinding_WithoutCircularDependency()
    {
        var ownerUserId = "normal-user-bind";
        var databaseName = Guid.NewGuid().ToString("N");
        var databaseRoot = new InMemoryDatabaseRoot();
        await using (var seedContext = CreateContext(databaseName: databaseName, databaseRoot: databaseRoot))
        {
            await SeedStrategyTemplateAsync(seedContext, "platform-admin", "shared-bind-template", "Shared Bind Template", isPlatformAdminOwner: true);
        }

        await using var context = CreateContext(currentUserId: ownerUserId, hasIsolationBypass: false, databaseName: databaseName, databaseRoot: databaseRoot);
        var exchangeAccountId = await SeedExchangeAccountAsync(context, ownerUserId);
        var service = CreateService(context);

        var result = await service.CreateAsync(
            ownerUserId,
            new BotManagementSaveCommand(
                "Shared Template Bot",
                "shared-bind-template",
                "BTCUSDT",
                [],
                0.001m,
                exchangeAccountId,
                1m,
                "ISOLATED",
                false),
            $"user:{ownerUserId}");

        var bot = await context.TradingBots.SingleAsync(entity => entity.OwnerUserId == ownerUserId);
        var strategy = await context.TradingStrategies.SingleAsync(entity =>
            entity.OwnerUserId == ownerUserId &&
            entity.StrategyKey == "shared-bind-template");
        var version = await context.TradingStrategyVersions.SingleAsync(entity =>
            entity.OwnerUserId == ownerUserId &&
            entity.TradingStrategyId == strategy.Id);

        Assert.True(result.IsSuccessful);
        Assert.Equal("shared-bind-template", bot.StrategyKey);
        Assert.Equal("Shared Bind Template", strategy.DisplayName);
        Assert.Equal(StrategyVersionStatus.Published, version.Status);
        Assert.Equal(version.Id, strategy.ActiveTradingStrategyVersionId);
    }

    [Fact]
    public async Task CreateAsync_DoesNotMaterializeOtherUserPrivateTemplate()
    {
        var ownerUserId = "normal-user-private-block";
        var databaseName = Guid.NewGuid().ToString("N");
        var databaseRoot = new InMemoryDatabaseRoot();
        await using (var seedContext = CreateContext(databaseName: databaseName, databaseRoot: databaseRoot))
        {
            await SeedStrategyTemplateAsync(seedContext, "other-user", "other-private-template", "Other Private Template", isPlatformAdminOwner: false);
        }

        await using var context = CreateContext(currentUserId: ownerUserId, hasIsolationBypass: false, databaseName: databaseName, databaseRoot: databaseRoot);
        var exchangeAccountId = await SeedExchangeAccountAsync(context, ownerUserId);
        var service = CreateService(context);

        var result = await service.CreateAsync(
            ownerUserId,
            new BotManagementSaveCommand(
                "Private Leak Bot",
                "other-private-template",
                "BTCUSDT",
                [],
                0.001m,
                exchangeAccountId,
                1m,
                "ISOLATED",
                false),
            $"user:{ownerUserId}");

        Assert.False(result.IsSuccessful);
        Assert.Equal("StrategyNotFound", result.FailureCode);
        Assert.DoesNotContain(context.TradingBots, entity => entity.OwnerUserId == ownerUserId);
        Assert.DoesNotContain(context.TradingStrategies, entity =>
            entity.OwnerUserId == ownerUserId &&
            entity.StrategyKey == "other-private-template");
    }

    private static BotManagementService CreateService(
        ApplicationDbContext context,
        TimeProvider? timeProvider = null,
        ICriticalUserOperationAuthorizer? criticalUserOperationAuthorizer = null,
        IStrategyTemplateCatalogService? strategyTemplateCatalogService = null,
        BinanceMarketDataOptions? marketDataOptions = null,
        HistoricalGapFillerOptions? historicalGapFillerOptions = null)
    {
        return new BotManagementService(
            context,
            new BotPilotControlService(
                context,
                criticalUserOperationAuthorizer ?? new FakeCriticalUserOperationAuthorizer(),
                timeProvider ?? new FakeTimeProvider(),
                Options.Create(CreatePilotOptions())),
            criticalUserOperationAuthorizer ?? new FakeCriticalUserOperationAuthorizer(),
            timeProvider ?? new FakeTimeProvider(),
            Options.Create(CreatePilotOptions()),
            Options.Create(new DataLatencyGuardOptions()),
            Options.Create(marketDataOptions ?? new BinanceMarketDataOptions()),
            Options.Create(historicalGapFillerOptions ?? new HistoricalGapFillerOptions()),
            strategyTemplateCatalogService: strategyTemplateCatalogService ?? new StrategyTemplateCatalogService(
                new StrategyRuleParser(),
                new StrategyDefinitionValidator(),
                context));
    }

    private static ApplicationDbContext CreateDbContext()
    {
        return CreateContext();
    }

    private static TradingStrategy CreateStrategy(string ownerUserId, string strategyKey, bool hasPublishedVersion = true)
    {
        return new TradingStrategy
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            StrategyKey = strategyKey,
            DisplayName = $"{strategyKey}-display",
            UsesExplicitVersionLifecycle = false,
            PromotionState = hasPublishedVersion ? StrategyPromotionState.LivePublished : StrategyPromotionState.Draft,
            PublishedAtUtc = hasPublishedVersion ? DateTime.UtcNow : null,
            PublishedMode = hasPublishedVersion ? ExecutionEnvironment.Live : null
        };
    }

    private static ExchangeAccount CreateExchangeAccount(string ownerUserId, bool isActive, bool isWritable)
    {
        return new ExchangeAccount
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            ExchangeName = "Binance",
            DisplayName = "Pilot Futures",
            IsReadOnly = !isWritable,
            CredentialStatus = isActive ? ExchangeCredentialStatus.Active : ExchangeCredentialStatus.Missing
        };
    }

    private static async Task<Guid> SeedStrategyAndExchangeAccountAsync(ApplicationDbContext context, string ownerUserId, string strategyKey)
    {
        var strategy = new TradingStrategy
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            StrategyKey = strategyKey,
            DisplayName = $"{strategyKey}-display"
        };
        var exchangeAccountId = Guid.NewGuid();

        context.TradingStrategies.Add(strategy);
        context.TradingStrategyVersions.Add(new TradingStrategyVersion
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            TradingStrategyId = strategy.Id,
            VersionNumber = 1,
            SchemaVersion = 1,
            Status = StrategyVersionStatus.Published,
            DefinitionJson = "{}",
            PublishedAtUtc = DateTime.UtcNow
        });
        context.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = ownerUserId,
            ExchangeName = "Binance",
            DisplayName = "Pilot Futures",
            IsReadOnly = false,
            CredentialStatus = ExchangeCredentialStatus.Active
        });
        await context.SaveChangesAsync();

        return exchangeAccountId;
    }

    private static async Task<Guid> SeedExchangeAccountAsync(ApplicationDbContext context, string ownerUserId)
    {
        var exchangeAccountId = Guid.NewGuid();
        context.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = ownerUserId,
            ExchangeName = "Binance",
            DisplayName = "Pilot Futures",
            IsReadOnly = false,
            CredentialStatus = ExchangeCredentialStatus.Active
        });
        await context.SaveChangesAsync();

        return exchangeAccountId;
    }

    private static async Task SeedStrategyTemplateAsync(
        ApplicationDbContext context,
        string ownerUserId,
        string templateKey,
        string templateName,
        bool isPlatformAdminOwner)
    {
        if (isPlatformAdminOwner)
        {
            context.UserClaims.Add(new IdentityUserClaim<string>
            {
                UserId = ownerUserId,
                ClaimType = ApplicationClaimTypes.Permission,
                ClaimValue = ApplicationPermissions.PlatformAdministration
            });
        }

        var templateId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var utcNow = DateTime.UtcNow;
        var definitionJson = CreateTemplateDefinitionJson(templateKey, templateName);
        context.TradingStrategyTemplates.Add(new TradingStrategyTemplate
        {
            Id = templateId,
            OwnerUserId = ownerUserId,
            TemplateKey = templateKey,
            TemplateName = templateName,
            Description = $"{templateName} description.",
            Category = "Custom",
            SchemaVersion = 2,
            DefinitionJson = definitionJson,
            IsActive = true,
            ActiveTradingStrategyTemplateRevisionId = revisionId,
            PublishedTradingStrategyTemplateRevisionId = revisionId,
            LatestTradingStrategyTemplateRevisionId = revisionId,
            CreatedDate = utcNow,
            UpdatedDate = utcNow
        });
        context.TradingStrategyTemplateRevisions.Add(new TradingStrategyTemplateRevision
        {
            Id = revisionId,
            OwnerUserId = ownerUserId,
            TradingStrategyTemplateId = templateId,
            RevisionNumber = 1,
            SchemaVersion = 2,
            DefinitionJson = definitionJson,
            ValidationStatusCode = "Valid",
            ValidationSummary = "Valid",
            CreatedDate = utcNow,
            UpdatedDate = utcNow
        });
        await context.SaveChangesAsync();
    }

    private static string CreateTemplateDefinitionJson(string templateKey, string templateName)
    {
        return StrategyContractJson.Reference
            .Replace("\"templateKey\": \"bollinger-rsi-reversal\"", $"\"templateKey\": \"{templateKey}\"", StringComparison.Ordinal)
            .Replace("\"templateName\": \"Bollinger RSI Reversal\"", $"\"templateName\": \"{templateName}\"", StringComparison.Ordinal);
    }

    private static async Task<TradingBot> SeedBotAsync(ApplicationDbContext context, string ownerUserId, Guid exchangeAccountId, bool isEnabled)
    {
        var bot = new TradingBot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = "Pilot Bot",
            StrategyKey = context.TradingStrategies.Single(entity => entity.OwnerUserId == ownerUserId).StrategyKey,
            Symbol = "BTCUSDT",
            ExchangeAccountId = exchangeAccountId,
            Leverage = 1m,
            MarginType = "ISOLATED",
            IsEnabled = isEnabled
        };
        context.TradingBots.Add(bot);
        await context.SaveChangesAsync();
        return bot;
    }

    private static ApplicationDbContext CreateContext(
        string? currentUserId = null,
        bool hasIsolationBypass = true,
        string? databaseName = null,
        InMemoryDatabaseRoot? databaseRoot = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString("N"), databaseRoot)
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext(currentUserId, hasIsolationBypass));
    }

    private sealed class TestDataScopeContext(string? userId, bool hasIsolationBypass) : IDataScopeContext
    {
        public string? UserId => userId;

        public bool HasIsolationBypass => hasIsolationBypass;
    }

    private sealed class FakeTimeProvider(DateTimeOffset? utcNow = null) : TimeProvider
    {
        private readonly DateTimeOffset current = utcNow ?? new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            return current;
        }
    }

    private static BotExecutionPilotOptions CreatePilotOptions()
    {
        return new BotExecutionPilotOptions
        {
            Enabled = true,
            DefaultSymbol = "BTCUSDT",
            AllowedSymbols = ["BTCUSDT", "ETHUSDT", "SOLUSDT"],
            DefaultLeverage = 1m,
            DefaultMarginType = "ISOLATED",
            PerBotCooldownSeconds = 120,
            PerSymbolCooldownSeconds = 60
        };
    }

    private sealed class ThrowingTemplateCatalogService : IStrategyTemplateCatalogService
    {
        public int ListAsyncCallCount { get; private set; }

        public int GetAsyncCallCount { get; private set; }

        public Task<IReadOnlyCollection<StrategyTemplateSnapshot>> ListAsync(CancellationToken cancellationToken = default)
        {
            ListAsyncCallCount++;
            throw new InvalidOperationException("Full catalog load should not be used by bot editor shared template projection.");
        }

        public Task<IReadOnlyCollection<StrategyTemplateSnapshot>> ListAllAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<StrategyTemplateSnapshot> GetAsync(string templateKey, CancellationToken cancellationToken = default)
        {
            GetAsyncCallCount++;
            throw new InvalidOperationException("Template lookup should not be used for owned strategy selection.");
        }

        public Task<StrategyTemplateSnapshot> GetIncludingArchivedAsync(string templateKey, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<StrategyTemplateSnapshot> CreateCustomAsync(
            string ownerUserId,
            string templateKey,
            string templateName,
            string description,
            string category,
            string definitionJson,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<StrategyTemplateSnapshot> CloneAsync(
            string ownerUserId,
            string sourceTemplateKey,
            string templateKey,
            string templateName,
            string description,
            string category,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<StrategyTemplateSnapshot> UpdateCurrentAsync(
            string templateKey,
            string templateName,
            string description,
            string category,
            string definitionJson,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        

        public Task<StrategyTemplateSnapshot> ReviseAsync(
            string templateKey,
            string templateName,
            string description,
            string category,
            string definitionJson,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<StrategyTemplateSnapshot> PublishAsync(
            string templateKey,
            int revisionNumber,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyCollection<StrategyTemplateRevisionSnapshot>> ListRevisionsAsync(
            string templateKey,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<StrategyTemplateSnapshot> ArchiveAsync(string templateKey, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeCriticalUserOperationAuthorizer : ICriticalUserOperationAuthorizer
    {
        public CriticalUserOperationAuthorizationResult Result { get; set; } =
            new(true, null, null);

        public Task<CriticalUserOperationAuthorizationResult> AuthorizeAsync(
            CriticalUserOperationAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result);
        }
    }
}
