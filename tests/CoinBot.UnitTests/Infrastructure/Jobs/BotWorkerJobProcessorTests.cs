using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Ai;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.Features;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Ai;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.DemoPortfolio;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Features;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Risk;
using CoinBot.Infrastructure.Strategies;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Jobs;

public sealed class BotWorkerJobProcessorTests
{
    [Fact]
    public async Task ProcessAsync_GeneratesEntrySignal_AndSubmitsDevelopmentFuturesPilotOrder()
    {
        await using var harness = CreateHarness();
        var bot = await SeedBotGraphAsync(harness.DbContext);
        ConfigurePilotScope(harness, bot);
        harness.PilotOptions.PilotActivationEnabled = true;
        await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-bot-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: "corr-bot-2");

        var result = await harness.Processor.ProcessAsync(
            bot,
            "job-bot-1",
            CancellationToken.None);

        var persistedSignal = await harness.DbContext.TradingStrategySignals.SingleAsync();
        var persistedOrder = await harness.DbContext.ExecutionOrders.SingleAsync();

        Assert.True(result.IsSuccessful);
        Assert.Equal(StrategySignalType.Entry, persistedSignal.SignalType);
        Assert.Equal(ExecutionOrderState.Submitted, persistedOrder.State);
        Assert.Equal(ExecutionEnvironment.Live, persistedOrder.ExecutionEnvironment);
        Assert.Equal(ExecutionOrderExecutorKind.Binance, persistedOrder.ExecutorKind);
        Assert.Equal(ExchangeDataPlane.Futures, persistedOrder.Plane);
        Assert.Equal(ExchangeStateDriftStatus.Unknown, persistedOrder.ReconciliationStatus);
        Assert.Equal(0.002m, persistedOrder.Quantity);
        Assert.Equal(1, harness.PrivateRestClient.EnsureMarginTypeCalls);
        Assert.Equal(1, harness.PrivateRestClient.EnsureLeverageCalls);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(0, harness.SpotPrivateRestClient.PlaceOrderCalls);
        Assert.StartsWith("cbp0_", harness.PrivateRestClient.LastPlacedClientOrderId, StringComparison.Ordinal);
        Assert.Equal("BTCUSDT", persistedOrder.Symbol);
    }

    [Fact]
    public async Task ProcessAsync_SuppressesDuplicatePilotExecution_WhenTheSameBotRunsTwice()
    {
        await using var harness = CreateHarness();
        var bot = await SeedBotGraphAsync(harness.DbContext);
        ConfigurePilotScope(harness, bot);
        harness.PilotOptions.PilotActivationEnabled = true;
        await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-bot-dup-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: "corr-bot-dup-2");

        var firstResult = await harness.Processor.ProcessAsync(
            bot,
            "job-bot-dup-1",
            CancellationToken.None);
        var secondResult = await harness.Processor.ProcessAsync(
            bot,
            "job-bot-dup-2",
            CancellationToken.None);

        Assert.True(firstResult.IsSuccessful);
        Assert.True(secondResult.IsSuccessful);
        Assert.Equal(1, await harness.DbContext.ExecutionOrders.CountAsync());
        Assert.Equal(1, await harness.DbContext.TradingStrategySignals.CountAsync());
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Contains(
            await harness.DbContext.DecisionTraces.Select(entity => entity.DecisionOutcome).ToArrayAsync(),
            outcome => string.Equals(outcome, "SuppressedDuplicate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessAsync_FailsClosedBeforeExecution_WhenPilotNotionalHardCapIsExceeded()
    {
        await using var harness = CreateHarness();
        var bot = await SeedBotGraphAsync(harness.DbContext);
        ConfigurePilotScope(harness, bot);
        harness.PilotOptions.PilotActivationEnabled = true;
        harness.PilotOptions.MaxPilotOrderNotional = "100";
        await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-bot-cap-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: "corr-bot-cap-2");

        var result = await harness.Processor.ProcessAsync(
            bot,
            "job-bot-cap-1",
            CancellationToken.None);

        var persistedSignal = await harness.DbContext.TradingStrategySignals.SingleAsync();
        var persistedFeature = await harness.DbContext.TradingFeatureSnapshots.SingleAsync();

        Assert.False(result.IsSuccessful);
        Assert.False(result.IsRetryableFailure);
        Assert.Equal("UserExecutionPilotNotionalHardCapExceeded", result.ErrorCode);
        Assert.Equal(StrategySignalType.Entry, persistedSignal.SignalType);
        Assert.Equal(bot.Id, persistedFeature.BotId);
        Assert.Empty(harness.DbContext.ExecutionOrders);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task ProcessAsync_FailsClosedBeforeExecution_WhenPilotNotionalCapConfigurationIsMissing()
    {
        await using var harness = CreateHarness();
        var bot = await SeedBotGraphAsync(harness.DbContext);
        ConfigurePilotScope(harness, bot);
        harness.PilotOptions.PilotActivationEnabled = true;
        harness.PilotOptions.MaxPilotOrderNotional = null;
        await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-bot-cap-missing-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: "corr-bot-cap-missing-2");

        var result = await harness.Processor.ProcessAsync(
            bot,
            "job-bot-cap-missing-1",
            CancellationToken.None);

        Assert.False(result.IsSuccessful);
        Assert.False(result.IsRetryableFailure);
        Assert.Equal("UserExecutionPilotNotionalConfigurationMissing", result.ErrorCode);
        Assert.Empty(harness.DbContext.ExecutionOrders);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task ProcessAsync_DoesNotSubmit_WhenPilotActivationIsDisabled()
    {
        await using var harness = CreateHarness();
        var bot = await SeedBotGraphAsync(harness.DbContext);
        ConfigurePilotScope(harness, bot);
        await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-bot-activation-off-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: "corr-bot-activation-off-2");

        var result = await harness.Processor.ProcessAsync(
            bot,
            "job-bot-activation-off-1",
            CancellationToken.None);

        var persistedSignal = await harness.DbContext.TradingStrategySignals.SingleAsync();
        var persistedFeature = await harness.DbContext.TradingFeatureSnapshots.SingleAsync();

        Assert.True(result.IsSuccessful);
        Assert.Equal(StrategySignalType.Entry, persistedSignal.SignalType);
        Assert.Equal(bot.Id, persistedFeature.BotId);
        Assert.Empty(harness.DbContext.ExecutionOrders);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }
    [Fact]
    public async Task ProcessAsync_RefreshesSymbolScopedLatencyFromHistoricalBackfill_WhenHeartbeatWasNotPrimed()
    {
        await using var harness = CreateHarness();
        var bot = await SeedBotGraphAsync(harness.DbContext);
        ConfigurePilotScope(harness, bot);
        harness.PilotOptions.PilotActivationEnabled = true;
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: "corr-bot-rest-2");

        var result = await harness.Processor.ProcessAsync(
            bot,
            "job-bot-rest-1",
            CancellationToken.None);

        var stateId = DegradedModeDefaults.ResolveStateId("BTCUSDT", "1m");
        var scopedState = await harness.DbContext.DegradedModeStates.SingleAsync(entity => entity.Id == stateId);
        var persistedOrder = await harness.DbContext.ExecutionOrders.SingleAsync();

        Assert.True(result.IsSuccessful);
        Assert.Equal(ExecutionOrderState.Submitted, persistedOrder.State);
        Assert.Equal(DegradedModeStateCode.Normal, scopedState.StateCode);
        Assert.Equal(DegradedModeReasonCode.None, scopedState.ReasonCode);
        Assert.Equal("BTCUSDT", scopedState.LatestSymbol);
        Assert.Equal("1m", scopedState.LatestTimeframe);
        Assert.Equal("binance:rest-backfill", scopedState.LatestHeartbeatSource);
        Assert.Equal(0, scopedState.LatestContinuityGapCount);
        Assert.Equal(0, scopedState.LatestClockDriftMilliseconds);
        Assert.NotNull(scopedState.LatestHeartbeatReceivedAtUtc);
        Assert.NotNull(scopedState.LatestDataTimestampAtUtc);
    }

    [Fact]
    public async Task ProcessAsync_FailsClosed_WhenPilotSymbolIsNotAllowed()
    {
        await using var harness = CreateHarness();
        var bot = await SeedBotGraphAsync(harness.DbContext, symbol: "XRPUSDT");
        ConfigurePilotScope(harness, bot, "BTCUSDT");
        await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-bot-eth-1", symbol: "ETHUSDT");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: "corr-bot-eth-2");

        var result = await harness.Processor.ProcessAsync(
            bot,
            "job-bot-eth-1",
            CancellationToken.None);

        Assert.False(result.IsSuccessful);
        Assert.False(result.IsRetryableFailure);
        Assert.Equal("PilotSymbolNotAllowed", result.ErrorCode);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Empty(harness.DbContext.ExecutionOrders);
        Assert.Empty(harness.DbContext.TradingStrategySignals);
    }

    [Fact]
    public async Task ProcessAsync_FailsClosed_WhenTradeMasterIsDisarmed()
    {
        await using var harness = CreateHarness();
        var bot = await SeedBotGraphAsync(harness.DbContext);
        ConfigurePilotScope(harness, bot);
        harness.PilotOptions.PilotActivationEnabled = true;
        await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-bot-disarmed-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: "corr-bot-disarmed-2");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Disarmed,
            actor: "admin-bot",
            context: "Execution frozen",
            correlationId: "corr-bot-disarmed-3");

        var result = await harness.Processor.ProcessAsync(
            bot,
            "job-bot-disarmed-1",
            CancellationToken.None);

        var persistedOrder = await harness.DbContext.ExecutionOrders.SingleAsync();

        Assert.False(result.IsSuccessful);
        Assert.False(result.IsRetryableFailure);
        Assert.Equal("TradeMasterDisarmed", result.ErrorCode);
        Assert.Equal(ExecutionOrderState.Rejected, persistedOrder.State);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
        var persistedFeature = await harness.DbContext.TradingFeatureSnapshots.SingleAsync();
        Assert.Equal(bot.OwnerUserId, persistedFeature.OwnerUserId);
        Assert.Equal(bot.Id, persistedFeature.BotId);
        Assert.Equal("BTCUSDT", persistedFeature.Symbol);
        Assert.Equal("1m", persistedFeature.Timeframe);
    }

    [Fact]
    public async Task ProcessAsync_Submits_WhenPilotActivationEnabled_EvenIfAiShadowModeIsEnabled()
    {
        await using var harness = CreateHarness(CreateEnabledAiOptions(), aiSignalEvaluatorOverride: new FixedAiSignalEvaluator(AiSignalDirection.Long, 0.91m, false, null));
        var bot = await SeedBotGraphAsync(harness.DbContext);
        ConfigurePilotScope(harness, bot);
        harness.PilotOptions.PilotActivationEnabled = true;
        await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-bot-ai-live-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: "corr-bot-ai-live-2");
        var result = await harness.Processor.ProcessAsync(
            bot,
            "job-bot-ai-live-1",
            CancellationToken.None);
        var persistedOrder = await harness.DbContext.ExecutionOrders.SingleAsync();
        Assert.True(result.IsSuccessful);
        Assert.Equal(ExecutionOrderState.Submitted, persistedOrder.State);
        Assert.Equal(ExecutionEnvironment.Live, persistedOrder.ExecutionEnvironment);
        Assert.Equal(ExchangeDataPlane.Futures, persistedOrder.Plane);
        Assert.Empty(harness.DbContext.AiShadowDecisions);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(0, harness.SpotPrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task ProcessAsync_PersistsShadowOnlyDecision_AndSkipsExecution_WhenAiShadowModeEnabled()
    {
        await using var harness = CreateHarness(CreateEnabledAiOptions(), aiSignalEvaluatorOverride: new FixedAiSignalEvaluator(AiSignalDirection.Long, 0.91m, false, null));
        var bot = await SeedBotGraphAsync(harness.DbContext);
        ConfigurePilotScope(harness, bot);
        await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-bot-ai-shadow-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: "corr-bot-ai-shadow-2");

        var result = await harness.Processor.ProcessAsync(
            bot,
            "job-bot-ai-shadow-1",
            CancellationToken.None);

        var shadowDecision = await harness.DbContext.AiShadowDecisions.SingleAsync();

        Assert.True(result.IsSuccessful);
        Assert.Equal("ShadowOnly", shadowDecision.FinalAction);
        Assert.True(shadowDecision.HypotheticalSubmitAllowed);
        Assert.Equal("ShadowModeActive", shadowDecision.NoSubmitReason);
        Assert.False(shadowDecision.AiIsFallback);
        Assert.Equal("Long", shadowDecision.AiDirection);
        Assert.Empty(harness.DbContext.ExecutionOrders);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task ProcessAsync_PersistsNoSubmitShadowDecision_WhenTradeMasterIsDisarmed_InAiShadowMode()
    {
        await using var harness = CreateHarness(CreateEnabledAiOptions(), aiSignalEvaluatorOverride: new FixedAiSignalEvaluator(AiSignalDirection.Long, 0.91m, false, null));
        var bot = await SeedBotGraphAsync(harness.DbContext);
        ConfigurePilotScope(harness, bot);
        await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-bot-ai-disarmed-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: "corr-bot-ai-disarmed-2");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Disarmed,
            actor: "admin-bot",
            context: "Execution frozen",
            correlationId: "corr-bot-ai-disarmed-3");

        var result = await harness.Processor.ProcessAsync(
            bot,
            "job-bot-ai-disarmed-1",
            CancellationToken.None);

        var shadowDecision = await harness.DbContext.AiShadowDecisions.SingleAsync();

        Assert.True(result.IsSuccessful);
        Assert.Equal("NoSubmit", shadowDecision.FinalAction);
        Assert.False(shadowDecision.HypotheticalSubmitAllowed);
        Assert.Equal("TradeMasterDisarmed", shadowDecision.HypotheticalBlockReason);
        Assert.Equal("TradeMasterDisarmed", shadowDecision.NoSubmitReason);
        Assert.False(shadowDecision.PilotSafetyBlocked);
        Assert.Empty(harness.DbContext.ExecutionOrders);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task ProcessAsync_PersistsNoSubmitShadowDecision_WhenFeatureSnapshotIsUnavailable_InAiShadowMode()
    {
        await using var harness = CreateHarness(
            CreateEnabledAiOptions(),
            new ThrowingFeatureSnapshotService());
        var bot = await SeedBotGraphAsync(harness.DbContext);
        ConfigurePilotScope(harness, bot);
        await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-bot-ai-nosnapshot-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: "corr-bot-ai-nosnapshot-2");

        var result = await harness.Processor.ProcessAsync(
            bot,
            "job-bot-ai-nosnapshot-1",
            CancellationToken.None);

        var shadowDecision = await harness.DbContext.AiShadowDecisions.SingleAsync();

        Assert.True(result.IsSuccessful);
        Assert.Equal("NoSubmit", shadowDecision.FinalAction);
        Assert.True(shadowDecision.HypotheticalSubmitAllowed);
        Assert.Equal("AiFeatureSnapshotUnavailable", shadowDecision.NoSubmitReason);
        Assert.True(shadowDecision.AiIsFallback);
        Assert.Equal(nameof(AiSignalFallbackReason.FeatureSnapshotUnavailable), shadowDecision.AiFallbackReason);
        Assert.Null(shadowDecision.FeatureSnapshotId);
        Assert.Empty(harness.DbContext.ExecutionOrders);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }
    [Theory]
    [InlineData(AiSignalFallbackReason.ProviderUnavailable)]
    [InlineData(AiSignalFallbackReason.InvalidPayload)]
    public async Task ProcessAsync_PersistsNoSubmitShadowDecision_WhenAiFallbackOccurs_InAiShadowMode(AiSignalFallbackReason fallbackReason)
    {
        await using var harness = CreateHarness(
            CreateEnabledAiOptions(),
            aiSignalEvaluatorOverride: new FixedAiSignalEvaluator(AiSignalDirection.Neutral, 0m, true, fallbackReason));
        var bot = await SeedBotGraphAsync(harness.DbContext);
        ConfigurePilotScope(harness, bot);
        await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, $"corr-bot-ai-fallback-{fallbackReason}-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: $"corr-bot-ai-fallback-{fallbackReason}-2");

        var result = await harness.Processor.ProcessAsync(
            bot,
            $"job-bot-ai-fallback-{fallbackReason}-1",
            CancellationToken.None);

        var shadowDecision = await harness.DbContext.AiShadowDecisions.SingleAsync();

        Assert.True(result.IsSuccessful);
        Assert.Equal("NoSubmit", shadowDecision.FinalAction);
        Assert.Equal($"Ai{fallbackReason}", shadowDecision.NoSubmitReason);
        Assert.True(shadowDecision.AiIsFallback);
        Assert.Equal(fallbackReason.ToString(), shadowDecision.AiFallbackReason);
        Assert.NotNull(shadowDecision.FeatureSnapshotId);
        Assert.Empty(harness.DbContext.ExecutionOrders);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task ProcessAsync_PersistsNoSubmitShadowDecision_WhenFeatureSnapshotIsStale_InAiShadowMode()
    {
        await using var harness = CreateHarness(
            CreateEnabledAiOptions(),
            new StaleFeatureSnapshotService());
        var bot = await SeedBotGraphAsync(harness.DbContext);
        ConfigurePilotScope(harness, bot);
        await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-bot-ai-stale-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: "corr-bot-ai-stale-2");

        var result = await harness.Processor.ProcessAsync(
            bot,
            "job-bot-ai-stale-1",
            CancellationToken.None);

        var shadowDecision = await harness.DbContext.AiShadowDecisions.SingleAsync();

        Assert.True(result.IsSuccessful);
        Assert.Equal("NoSubmit", shadowDecision.FinalAction);
        Assert.True(shadowDecision.HypotheticalSubmitAllowed);
        Assert.Equal("AiFeatureSnapshotNotReady", shadowDecision.NoSubmitReason);
        Assert.True(shadowDecision.AiIsFallback);
        Assert.Equal(nameof(AiSignalFallbackReason.FeatureSnapshotNotReady), shadowDecision.AiFallbackReason);
        Assert.NotNull(shadowDecision.FeatureSnapshotId);
        Assert.Empty(harness.DbContext.ExecutionOrders);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }
    [Fact]
    public async Task ProcessAsync_FailsClosed_WhenSameOwnerHasMultipleEnabledBotsOnSameSymbol()
    {
        await using var harness = CreateHarness();
        var firstBot = await SeedBotGraphAsync(harness.DbContext);
        ConfigurePilotScope(harness, firstBot);
        _ = await SeedBotGraphAsync(harness.DbContext, ownerUserId: firstBot.OwnerUserId);
        await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-bot-multi-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: "corr-bot-multi-2");

        var result = await harness.Processor.ProcessAsync(
            firstBot,
            "job-bot-multi-1",
            CancellationToken.None);

        Assert.False(result.IsSuccessful);
        Assert.False(result.IsRetryableFailure);
        Assert.Equal("PilotSymbolConflictMultipleEnabledBots", result.ErrorCode);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Empty(harness.DbContext.ExecutionOrders);
    }

    [Fact]
    public async Task ProcessAsync_AllowsMultipleEnabledBotsAcrossDifferentSymbols()
    {
        await using var harness = CreateHarness();
        var firstBot = await SeedBotGraphAsync(harness.DbContext, symbol: "BTCUSDT");
        ConfigurePilotScope(harness, firstBot);
        harness.PilotOptions.PilotActivationEnabled = true;
        _ = await SeedBotGraphAsync(harness.DbContext, ownerUserId: firstBot.OwnerUserId, symbol: "ETHUSDT");
        await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-bot-eth-ok-1", symbol: "BTCUSDT");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: "corr-bot-eth-ok-2");

        var result = await harness.Processor.ProcessAsync(
            firstBot,
            "job-bot-eth-ok-1",
            CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
    }

    private static TestHarness CreateHarness(
        AiSignalOptions? aiSignalOptions = null,
        ITradingFeatureSnapshotService? featureSnapshotServiceOverride = null,
        IAiSignalEvaluator? aiSignalEvaluatorOverride = null)
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero));
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var correlationContextAccessor = new CorrelationContextAccessor();
        var auditLogService = new AuditLogService(dbContext, correlationContextAccessor);
        var switchService = new GlobalExecutionSwitchService(dbContext, auditLogService);
        var globalSystemStateService = new GlobalSystemStateService(dbContext, auditLogService, timeProvider);
        var marketDataService = new FakeMarketDataService(timeProvider);
        var demoWalletValuationService = new DemoWalletValuationService(
            marketDataService,
            timeProvider,
            NullLogger<DemoWalletValuationService>.Instance);
        var latencyOptions = Options.Create(new DataLatencyGuardOptions());
        var circuitBreaker = new DataLatencyCircuitBreaker(
            dbContext,
            new FakeAlertService(),
            latencyOptions,
            timeProvider,
            NullLogger<DataLatencyCircuitBreaker>.Instance);
        var tradingModeService = new TradingModeService(dbContext, auditLogService);
        var demoSessionService = new DemoSessionService(
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
        var hostEnvironment = new TestHostEnvironment(Environments.Development);
        var traceService = new TraceService(
            dbContext,
            correlationContextAccessor,
            timeProvider);
        var pilotOptions = new BotExecutionPilotOptions
        {
            Enabled = true,
            PilotActivationEnabled = false,
            SignalEvaluationMode = ExecutionEnvironment.Live,
            DefaultSymbol = "BTCUSDT",
            AllowedSymbols = ["BTCUSDT", "ETHUSDT", "SOLUSDT"],
            Timeframe = "1m",
            DefaultLeverage = 1m,
            DefaultMarginType = "ISOLATED",
            MaxOpenPositionsPerUser = 1,
            PerBotCooldownSeconds = 300,
            PerSymbolCooldownSeconds = 300,
            MaxOrderNotional = 250m,
            MaxDailyLossPercentage = 5m,
            PrivatePlaneFreshnessThresholdSeconds = 120,
            PrimeHistoricalCandleCount = 200
        };
        var resolvedAiSignalOptions = aiSignalOptions ?? new AiSignalOptions();
        var privateDataOptions = Options.Create(new BinancePrivateDataOptions
        {
            RestBaseUrl = "https://testnet.binance.example/futures-rest",
            WebSocketBaseUrl = "wss://testnet.binance.example/futures-private"
        });
        var marketDataOptions = Options.Create(new BinanceMarketDataOptions
        {
            RestBaseUrl = "https://testnet.binance.example/futures-market-rest",
            WebSocketBaseUrl = "wss://testnet.binance.example/futures-market-stream",
            KlineInterval = "1m"
        });
        var riskPolicyEvaluator = new RiskPolicyEvaluator(
            dbContext,
            timeProvider,
            NullLogger<RiskPolicyEvaluator>.Instance);
        var executionGate = new ExecutionGate(
            demoSessionService,
            globalSystemStateService,
            switchService,
            circuitBreaker,
            tradingModeService,
            auditLogService,
            NullLogger<ExecutionGate>.Instance,
            hostEnvironment,
            traceService,
            timeProvider,
            latencyOptions,
            dbContext,
            privateDataOptions,
            marketDataOptions,
            Options.Create(pilotOptions));
        var userExecutionOverrideGuard = new UserExecutionOverrideGuard(
            dbContext,
            tradingModeService,
            logger: NullLogger<UserExecutionOverrideGuard>.Instance,
            hostEnvironment: hostEnvironment,
            riskPolicyEvaluator: riskPolicyEvaluator,
            botExecutionPilotOptions: Options.Create(pilotOptions));
        var lifecycleService = new ExecutionOrderLifecycleService(
            dbContext,
            auditLogService,
            timeProvider,
            NullLogger<ExecutionOrderLifecycleService>.Instance);
        var demoPortfolioAccountingService = new DemoPortfolioAccountingService(
            dbContext,
            demoSessionService,
            demoWalletValuationService,
            timeProvider,
            NullLogger<DemoPortfolioAccountingService>.Instance);
        var demoFillSimulator = new DemoFillSimulator(
            marketDataService,
            Options.Create(new DemoFillSimulatorOptions()),
            timeProvider,
            NullLogger<DemoFillSimulator>.Instance);
        var credentialService = new FakeExchangeCredentialService();
        var privateRestClient = new FakePrivateRestClient(timeProvider);
        var spotPrivateRestClient = new FakeSpotPrivateRestClient(timeProvider);
        var strategySignalService = new StrategySignalService(
            dbContext,
            new StrategyEvaluatorService(new StrategyRuleParser()),
            riskPolicyEvaluator,
            traceService,
            correlationContextAccessor,
            aiSignalEvaluatorOverride ?? CreateAiSignalEvaluator(timeProvider, resolvedAiSignalOptions),
            Options.Create(resolvedAiSignalOptions),
            timeProvider,
            NullLogger<StrategySignalService>.Instance);
        var executionEngine = new ExecutionEngine(
            dbContext,
            executionGate,
            tradingModeService,
            traceService,
            userExecutionOverrideGuard,
            correlationContextAccessor,
            demoPortfolioAccountingService,
            demoFillSimulator,
            new VirtualExecutor(timeProvider, NullLogger<VirtualExecutor>.Instance),
            new BinanceExecutor(
                dbContext,
                credentialService,
                privateRestClient,
                NullLogger<BinanceExecutor>.Instance,
                marketDataService: marketDataService),
            new BinanceSpotExecutor(
                dbContext,
                credentialService,
                spotPrivateRestClient,
                NullLogger<BinanceSpotExecutor>.Instance,
                marketDataService: marketDataService),
            lifecycleService,
            timeProvider,
            NullLogger<ExecutionEngine>.Instance);
        var featureSnapshotService = featureSnapshotServiceOverride ?? new TradingFeatureSnapshotService(
            dbContext,
            circuitBreaker,
            tradingModeService,
            new FakeHistoricalKlineClient(timeProvider),
            Options.Create(pilotOptions),
            timeProvider,
            NullLogger<TradingFeatureSnapshotService>.Instance);
        var aiShadowDecisionService = new AiShadowDecisionService(dbContext, TimeProvider.System);
        var processor = new BotWorkerJobProcessor(
            dbContext,
            new IndicatorDataService(
                marketDataService,
                new IndicatorStreamHub(),
                Options.Create(new IndicatorEngineOptions()),
                NullLogger<IndicatorDataService>.Instance),
            marketDataService,
            new FakeExchangeInfoClient(marketDataService.SymbolMetadata),
            new FakeHistoricalKlineClient(timeProvider),
            strategySignalService,
            executionEngine,
            executionGate,
            userExecutionOverrideGuard,
            circuitBreaker,
            featureSnapshotService,
            aiShadowDecisionService,
            correlationContextAccessor,
            Options.Create(pilotOptions),
            Options.Create(resolvedAiSignalOptions),
            hostEnvironment,
            timeProvider,
            NullLogger<BotWorkerJobProcessor>.Instance);

        return new TestHarness(
            dbContext,
            processor,
            switchService,
            circuitBreaker,
            timeProvider,
            privateRestClient,
            spotPrivateRestClient,
            pilotOptions);
    }

    private static IAiSignalEvaluator CreateAiSignalEvaluator(TimeProvider timeProvider, AiSignalOptions aiSignalOptions)
    {
        return new AiSignalEvaluator(
            [new DeterministicStubAiSignalProviderAdapter(), new OfflineAiSignalProviderAdapter(), new OpenAiSignalProviderAdapter(), new GeminiAiSignalProviderAdapter()],
            Options.Create(aiSignalOptions),
            timeProvider,
            NullLogger<AiSignalEvaluator>.Instance);
    }
    private static AiSignalOptions CreateEnabledAiOptions()
    {
        return new AiSignalOptions
        {
            Enabled = true,
            ShadowModeEnabled = true,
            SelectedProvider = DeterministicStubAiSignalProviderAdapter.ProviderNameValue,
            MinimumConfidence = 0.70m
        };
    }

    private sealed class FixedAiSignalEvaluator(
        AiSignalDirection direction,
        decimal confidenceScore,
        bool isFallback,
        AiSignalFallbackReason? fallbackReason) : IAiSignalEvaluator
    {
        public Task<AiSignalEvaluationResult> EvaluateAsync(
            AiSignalEvaluationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nowUtc = request.FeatureSnapshot?.EvaluatedAtUtc ?? new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc);

            return Task.FromResult(
                isFallback
                    ? AiSignalEvaluationResult.NeutralFallback(
                        fallbackReason ?? AiSignalFallbackReason.EvaluationException,
                        "Fixed AI fallback.",
                        request.FeatureSnapshot?.Id,
                        "FixedAi",
                        "fixed-v1",
                        5,
                        nowUtc)
                    : new AiSignalEvaluationResult(
                        direction,
                        confidenceScore,
                        "Fixed AI evaluation.",
                        request.FeatureSnapshot?.Id,
                        "FixedAi",
                        "fixed-v1",
                        5,
                        IsFallback: false,
                        FallbackReason: null,
                        RawResponseCaptured: false,
                        nowUtc));
        }
    }
    private static async Task<TradingBot> SeedBotGraphAsync(
        ApplicationDbContext dbContext,
        string symbol = "BTCUSDT",
        string ownerUserId = "user-bot-pilot")
    {
        var observedAtUtc = new DateTime(2026, 4, 1, 11, 59, 0, DateTimeKind.Utc);
        var strategy = new TradingStrategy
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            StrategyKey = "pilot-core",
            DisplayName = "Pilot Core"
        };
        var version = new TradingStrategyVersion
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            TradingStrategyId = strategy.Id,
            SchemaVersion = 1,
            VersionNumber = 1,
            Status = StrategyVersionStatus.Published,
            DefinitionJson =
                """
                {
                  "schemaVersion": 1,
                  "entry": {
                    "operator": "all",
                    "rules": [
                      {
                        "path": "context.mode",
                        "comparison": "equals",
                        "value": "Live"
                      }
                    ]
                  },
                  "risk": {
                    "operator": "all",
                    "rules": [
                      {
                        "path": "indicator.sampleCount",
                        "comparison": "greaterThanOrEqual",
                        "value": 100
                      }
                    ]
                  }
                }
                """,
            PublishedAtUtc = new DateTime(2026, 4, 1, 11, 50, 0, DateTimeKind.Utc)
        };
        var exchangeAccountId = Guid.NewGuid();
        var bot = new TradingBot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = "Pilot Bot",
            StrategyKey = strategy.StrategyKey,
            Symbol = symbol,
            ExchangeAccountId = exchangeAccountId,
            IsEnabled = true
        };

        dbContext.RiskProfiles.Add(new RiskProfile
        {
            OwnerUserId = ownerUserId,
            ProfileName = "Pilot",
            MaxDailyLossPercentage = 10m,
            MaxPositionSizePercentage = 100m,
            MaxLeverage = 2m,
            MaxConcurrentPositions = 1
        });
        dbContext.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = ownerUserId,
            ExchangeName = "Binance",
            DisplayName = "Pilot Futures",
            IsReadOnly = false,
            CredentialStatus = ExchangeCredentialStatus.Active
        });
        dbContext.ExchangeBalances.Add(new ExchangeBalance
        {
            ExchangeAccountId = exchangeAccountId,
            OwnerUserId = ownerUserId,
            Plane = ExchangeDataPlane.Futures,
            Asset = "USDT",
            WalletBalance = 1000m,
            CrossWalletBalance = 1000m,
            AvailableBalance = 1000m,
            MaxWithdrawAmount = 1000m,
            ExchangeUpdatedAtUtc = observedAtUtc
        });
        dbContext.ApiCredentialValidations.Add(new ApiCredentialValidation
        {
            Id = Guid.NewGuid(),
            ApiCredentialId = Guid.NewGuid(),
            ExchangeAccountId = exchangeAccountId,
            OwnerUserId = ownerUserId,
            IsKeyValid = true,
            CanTrade = true,
            SupportsSpot = false,
            SupportsFutures = true,
            EnvironmentScope = "Demo",
            IsEnvironmentMatch = true,
            ValidationStatus = "Valid",
            PermissionSummary = "Trade=Y; Futures=Y; Testnet=Y",
            ValidatedAtUtc = observedAtUtc
        });
        dbContext.ExchangeAccountSyncStates.Add(new ExchangeAccountSyncState
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            ExchangeAccountId = exchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            PrivateStreamConnectionState = ExchangePrivateStreamConnectionState.Connected,
            DriftStatus = ExchangeStateDriftStatus.InSync,
            LastPrivateStreamEventAtUtc = observedAtUtc,
            LastBalanceSyncedAtUtc = observedAtUtc,
            LastPositionSyncedAtUtc = observedAtUtc,
            LastStateReconciledAtUtc = observedAtUtc
        });
        dbContext.TradingStrategies.Add(strategy);
        dbContext.TradingStrategyVersions.Add(version);
        dbContext.TradingBots.Add(bot);
        await dbContext.SaveChangesAsync();

        return bot;
    }

    private static void ConfigurePilotScope(TestHarness harness, TradingBot bot, string? allowedSymbol = null)
    {
        harness.PilotOptions.AllowedUserIds = [bot.OwnerUserId];
        harness.PilotOptions.AllowedBotIds = [bot.Id.ToString("N")];
        harness.PilotOptions.AllowedSymbols =
        [
            MarketDataSymbolNormalizer.Normalize(
                string.IsNullOrWhiteSpace(allowedSymbol)
                    ? bot.Symbol
                    : allowedSymbol)
        ];
    }

    private static async Task PrimeFreshMarketDataAsync(
        IDataLatencyCircuitBreaker circuitBreaker,
        AdjustableTimeProvider timeProvider,
        string correlationId,
        string symbol = "BTCUSDT",
        string timeframe = "1m")
    {
        await circuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "binance:kline",
                timeProvider.GetUtcNow().UtcDateTime,
                Symbol: symbol,
                Timeframe: timeframe,
                ExpectedOpenTimeUtc: timeProvider.GetUtcNow().UtcDateTime.AddMinutes(1),
                ContinuityGapCount: 0),
            correlationId);
    }

    private sealed class StaleFeatureSnapshotService : ITradingFeatureSnapshotService
    {
        public Task<TradingFeatureSnapshotModel> CaptureAsync(TradingFeatureCaptureRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateSnapshot(request));
        }

        public Task<TradingFeatureSnapshotModel?> GetLatestAsync(
            string userId,
            Guid botId,
            string symbol,
            string timeframe,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<TradingFeatureSnapshotModel?>(null);
        }

        public Task<IReadOnlyCollection<TradingFeatureSnapshotModel>> ListRecentAsync(
            string userId,
            Guid botId,
            string symbol,
            string timeframe,
            int take = 20,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyCollection<TradingFeatureSnapshotModel>>(Array.Empty<TradingFeatureSnapshotModel>());
        }

        private static TradingFeatureSnapshotModel CreateSnapshot(TradingFeatureCaptureRequest request)
        {
            var evaluatedAtUtc = request.EvaluatedAtUtc;
            return new TradingFeatureSnapshotModel(
                Guid.Parse("9b0fbf42-8d08-4f10-bdd6-92ed6404d1f8"),
                request.UserId,
                request.BotId,
                request.ExchangeAccountId,
                request.StrategyKey,
                request.Symbol,
                request.Timeframe,
                evaluatedAtUtc,
                evaluatedAtUtc.AddMinutes(-5),
                "AI-1.v1",
                FeatureSnapshotState.Stale,
                DegradedModeReasonCode.MarketDataLatencyBreached,
                200,
                200,
                50000m,
                new TradingTrendFeatureSnapshot(49980m, 49950m, 49800m, 49970m, 49910m),
                new TradingMomentumFeatureSnapshot(51m, 12m, 8m, 4m, 58m, 55m, 64m, 0.22m),
                new TradingVolatilityFeatureSnapshot(320m, 0.64m, 0.18m, 0.31m, 49750m, 49500m),
                new TradingVolumeFeatureSnapshot(1.2m, 1.1m, 2100m),
                new TradingContextFeatureSnapshot(
                    ExchangeDataPlane.Futures,
                    ExecutionEnvironment.Live,
                    HasOpenPosition: false,
                    IsInCooldown: false,
                    LastVetoReasonCode: null,
                    LastDecisionOutcome: "NoSignalCandidate",
                    LastDecisionCode: "NoSignalCandidate",
                    LastExecutionState: null,
                    LastFailureCode: null),
                "Stale feature snapshot.",
                "Freshness not ready.",
                "Range",
                "Neutral",
                "Elevated",
                null);
        }
    }
    private sealed class ThrowingFeatureSnapshotService : ITradingFeatureSnapshotService
    {
        public Task<TradingFeatureSnapshotModel> CaptureAsync(TradingFeatureCaptureRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Feature snapshot capture failed.");
        }

        public Task<TradingFeatureSnapshotModel?> GetLatestAsync(
            string userId,
            Guid botId,
            string symbol,
            string timeframe,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<TradingFeatureSnapshotModel?>(null);
        }

        public Task<IReadOnlyCollection<TradingFeatureSnapshotModel>> ListRecentAsync(
            string userId,
            Guid botId,
            string symbol,
            string timeframe,
            int take = 20,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyCollection<TradingFeatureSnapshotModel>>(Array.Empty<TradingFeatureSnapshotModel>());
        }
    }

    private sealed class FakeMarketDataService(TimeProvider timeProvider) : IMarketDataService
    {
        private readonly Dictionary<string, SymbolMetadataSnapshot> symbolMetadata = new(StringComparer.Ordinal)
        {
            ["BTCUSDT"] = CreateSymbolMetadata("BTCUSDT"),
            ["ETHUSDT"] = CreateSymbolMetadata("ETHUSDT"),
            ["SOLUSDT"] = CreateSymbolMetadata("SOLUSDT")
        };

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
            return ValueTask.FromResult<MarketPriceSnapshot?>(
                new MarketPriceSnapshot(
                    symbol.Trim().ToUpperInvariant(),
                    65000m,
                    timeProvider.GetUtcNow().UtcDateTime,
                    timeProvider.GetUtcNow().UtcDateTime,
                    "UnitTest"));
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            symbolMetadata.TryGetValue(symbol.Trim().ToUpperInvariant(), out var snapshot);
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(snapshot);
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        public IReadOnlyDictionary<string, SymbolMetadataSnapshot> SymbolMetadata => symbolMetadata;

        private static SymbolMetadataSnapshot CreateSymbolMetadata(string symbol)
        {
            return new SymbolMetadataSnapshot(
                symbol,
                "Binance",
                symbol[..^4],
                "USDT",
                0.1m,
                0.001m,
                "TRADING",
                true,
                DateTime.UtcNow)
            {
                MinQuantity = 0.001m,
                MinNotional = 100m,
                PricePrecision = 1,
                QuantityPrecision = 3
            };
        }
    }

    private sealed class FakeExchangeInfoClient(IReadOnlyDictionary<string, SymbolMetadataSnapshot> symbolMetadata) : IBinanceExchangeInfoClient
    {
        public Task<IReadOnlyCollection<SymbolMetadataSnapshot>> GetSymbolMetadataAsync(
            IReadOnlyCollection<string> symbols,
            CancellationToken cancellationToken = default)
        {
            var snapshots = symbols
                .Select(symbol => symbolMetadata[symbol.Trim().ToUpperInvariant()])
                .ToArray();
            return Task.FromResult<IReadOnlyCollection<SymbolMetadataSnapshot>>(snapshots);
        }

        public Task<DateTime?> GetServerTimeUtcAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<DateTime?>(DateTime.UtcNow);
        }
    }

    private sealed class FakeHistoricalKlineClient(TimeProvider timeProvider) : IBinanceHistoricalKlineClient
    {
        public Task<IReadOnlyCollection<MarketCandleSnapshot>> GetClosedCandlesAsync(
            string symbol,
            string interval,
            DateTime startOpenTimeUtc,
            DateTime endOpenTimeUtc,
            int limit,
            CancellationToken cancellationToken = default)
        {
            var snapshots = Enumerable.Range(0, limit)
                .Select(index =>
                {
                    var openTimeUtc = startOpenTimeUtc.AddMinutes(index);
                    var closeTimeUtc = openTimeUtc.AddMinutes(1).AddMilliseconds(-1);

                    return new MarketCandleSnapshot(
                        "BTCUSDT",
                        "1m",
                        openTimeUtc,
                        closeTimeUtc,
                        65000m,
                        65010m,
                        64990m,
                        65000m,
                        10m,
                        IsClosed: true,
                        timeProvider.GetUtcNow().UtcDateTime,
                        "UnitTest.History");
                })
                .ToArray();

            return Task.FromResult<IReadOnlyCollection<MarketCandleSnapshot>>(snapshots);
        }
    }

    private sealed class FakeExchangeCredentialService : IExchangeCredentialService
    {
        public Task<ExchangeCredentialStateSnapshot> StoreAsync(
            StoreExchangeCredentialsRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExchangeCredentialAccessResult> GetAsync(
            ExchangeCredentialAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                new ExchangeCredentialAccessResult(
                    "api-key",
                    "api-secret",
                    new ExchangeCredentialStateSnapshot(
                        request.ExchangeAccountId,
                        ExchangeCredentialStatus.Active,
                        "fingerprint",
                        "v1",
                        StoredAtUtc: new DateTime(2026, 4, 1, 11, 0, 0, DateTimeKind.Utc),
                        LastValidatedAtUtc: new DateTime(2026, 4, 1, 11, 5, 0, DateTimeKind.Utc),
                        LastAccessedAtUtc: new DateTime(2026, 4, 1, 11, 5, 0, DateTimeKind.Utc),
                        LastRotatedAtUtc: new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc),
                        RevalidateAfterUtc: new DateTime(2026, 5, 1, 11, 5, 0, DateTimeKind.Utc),
                        RotateAfterUtc: new DateTime(2026, 7, 1, 11, 5, 0, DateTimeKind.Utc))));
        }

        public Task<ExchangeCredentialStateSnapshot> SetValidationStateAsync(
            SetExchangeCredentialValidationStateRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExchangeCredentialStateSnapshot> GetStateAsync(
            Guid exchangeAccountId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakePrivateRestClient(TimeProvider timeProvider) : IBinancePrivateRestClient
    {
        public int EnsureMarginTypeCalls { get; private set; }

        public int EnsureLeverageCalls { get; private set; }

        public int PlaceOrderCalls { get; private set; }

        public string? LastPlacedClientOrderId { get; private set; }

        public Task EnsureMarginTypeAsync(
            Guid exchangeAccountId,
            string symbol,
            string marginType,
            string apiKey,
            string apiSecret,
            CancellationToken cancellationToken = default)
        {
            EnsureMarginTypeCalls++;
            return Task.CompletedTask;
        }

        public Task EnsureLeverageAsync(
            Guid exchangeAccountId,
            string symbol,
            decimal leverage,
            string apiKey,
            string apiSecret,
            CancellationToken cancellationToken = default)
        {
            EnsureLeverageCalls++;
            return Task.CompletedTask;
        }

        public Task<BinanceOrderPlacementResult> PlaceOrderAsync(
            BinanceOrderPlacementRequest request,
            CancellationToken cancellationToken = default)
        {
            PlaceOrderCalls++;
            LastPlacedClientOrderId = request.ClientOrderId;

            return Task.FromResult(
                new BinanceOrderPlacementResult(
                    $"binance-order-{PlaceOrderCalls}",
                    request.ClientOrderId,
                    timeProvider.GetUtcNow().UtcDateTime));
        }

        public Task<BinanceOrderStatusSnapshot> CancelOrderAsync(
            BinanceOrderCancelRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<BinanceOrderStatusSnapshot> GetOrderAsync(
            BinanceOrderQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string> StartListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task KeepAliveListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task CloseListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExchangeAccountSnapshot> GetAccountSnapshotAsync(
            Guid exchangeAccountId,
            string ownerUserId,
            string exchangeName,
            string apiKey,
            string apiSecret,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeSpotPrivateRestClient(TimeProvider timeProvider) : IBinanceSpotPrivateRestClient
    {
        public int PlaceOrderCalls { get; private set; }

        public Task<BinanceOrderPlacementResult> PlaceOrderAsync(
            BinanceOrderPlacementRequest request,
            CancellationToken cancellationToken = default)
        {
            PlaceOrderCalls++;

            var snapshot = new BinanceOrderStatusSnapshot(
                request.Symbol,
                "spot-order-1",
                request.ClientOrderId,
                "NEW",
                request.Quantity,
                0m,
                0m,
                0m,
                0m,
                0m,
                timeProvider.GetUtcNow().UtcDateTime,
                "Binance.SpotPrivateRest.OrderPlacement",
                Plane: ExchangeDataPlane.Spot);

            return Task.FromResult(new BinanceOrderPlacementResult("spot-order-1", request.ClientOrderId, timeProvider.GetUtcNow().UtcDateTime, snapshot));
        }

        public Task<ExchangeAccountSnapshot> GetAccountSnapshotAsync(
            Guid exchangeAccountId,
            string ownerUserId,
            string exchangeName,
            string apiKey,
            string apiSecret,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<BinanceOrderStatusSnapshot> GetOrderAsync(
            BinanceOrderQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyCollection<BinanceSpotTradeFillSnapshot>> GetTradeFillsAsync(
            BinanceOrderQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string> StartListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task KeepAliveListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task CloseListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeAlertService : CoinBot.Application.Abstractions.Alerts.IAlertService
    {
        public Task SendAsync(CoinBot.Application.Abstractions.Alerts.AlertNotification notification, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "CoinBot.UnitTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestHarness(
        ApplicationDbContext dbContext,
        BotWorkerJobProcessor processor,
        IGlobalExecutionSwitchService switchService,
        IDataLatencyCircuitBreaker circuitBreaker,
        AdjustableTimeProvider timeProvider,
        FakePrivateRestClient privateRestClient,
        FakeSpotPrivateRestClient spotPrivateRestClient,
        BotExecutionPilotOptions pilotOptions) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public BotWorkerJobProcessor Processor { get; } = processor;

        public IGlobalExecutionSwitchService SwitchService { get; } = switchService;

        public IDataLatencyCircuitBreaker CircuitBreaker { get; } = circuitBreaker;

        public AdjustableTimeProvider TimeProvider { get; } = timeProvider;

        public FakePrivateRestClient PrivateRestClient { get; } = privateRestClient;

        public FakeSpotPrivateRestClient SpotPrivateRestClient { get; } = spotPrivateRestClient;

        public BotExecutionPilotOptions PilotOptions { get; } = pilotOptions;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }
    }
}





