using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Ai;
using CoinBot.Application.Abstractions.DemoPortfolio;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Ai;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Risk;
using CoinBot.Infrastructure.Strategies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoinBot.UnitTests.Infrastructure.MarketData;

public sealed class MarketScannerHandoffServiceTests
{
    [Fact]
    public async Task RunOnceAsync_PreparesDeterministicTopCandidateAndPassesSelectedSymbolToStrategyAndGuards()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        await using var harness = CreateHarness(
            nowUtc,
            new BotExecutionPilotOptions
            {
                SignalEvaluationMode = ExecutionEnvironment.Live,
                ExecutionDispatchMode = ExecutionEnvironment.Live,
                PilotActivationEnabled = true,
                PrimeHistoricalCandleCount = 34
            });
        var scanCycleId = Guid.NewGuid();
        var btcBot = await SeedBotGraphAsync(harness.DbContext, "user-btc", "BTCUSDT", "pilot-btc");
        _ = await SeedBotGraphAsync(harness.DbContext, "user-eth", "ETHUSDT", "pilot-eth");
        SeedScanCycle(harness.DbContext, scanCycleId);
        SeedCandidate(harness.DbContext, scanCycleId, "ETHUSDT", rank: 1, score: 10_000m);
        SeedCandidate(harness.DbContext, scanCycleId, "BTCUSDT", rank: 1, score: 10_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("BTCUSDT", "BTC", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", harness.NowUtc));
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("ETHUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(btcBot.TradingStrategyId, btcBot.TradingStrategyVersionId, "BTCUSDT", "1m", harness.NowUtc));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        var persistedAttempt = await harness.DbContext.MarketScannerHandoffAttempts.SingleAsync(entity => entity.Id == attempt.Id);
        Assert.Equal("BTCUSDT", persistedAttempt.SelectedSymbol);
        Assert.Equal(1, persistedAttempt.CandidateRank);
        Assert.Equal(10_000m, persistedAttempt.CandidateScore);
        Assert.Equal("Prepared", persistedAttempt.ExecutionRequestStatus);
        Assert.Equal("Persisted", persistedAttempt.StrategyDecisionOutcome);
        Assert.Equal(btcBot.BotId, persistedAttempt.BotId);
        Assert.Equal(btcBot.TradingStrategyVersionId, persistedAttempt.TradingStrategyVersionId);
        Assert.Equal(ExecutionEnvironment.Live, persistedAttempt.ExecutionEnvironment);
        Assert.Equal(ExecutionOrderSide.Buy, persistedAttempt.ExecutionSide);
        Assert.Equal(ExecutionOrderType.Market, persistedAttempt.ExecutionOrderType);
        Assert.Null(persistedAttempt.BlockerCode);
        Assert.Equal("Allowed: execution request prepared.", persistedAttempt.BlockerSummary);
        Assert.Contains("Top-ranked eligible candidate selected", persistedAttempt.SelectionReason, StringComparison.Ordinal);
        Assert.Contains("ExecutionGate=Allowed", persistedAttempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("ExecutionDispatch=Dispatched", persistedAttempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("LatencyReason=None", persistedAttempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("LastCandleAtUtc=", persistedAttempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("DataAgeMs=", persistedAttempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("ContinuityGapCount=0", persistedAttempt.GuardSummary, StringComparison.Ordinal);
        Assert.Equal("BTCUSDT", harness.StrategySignalService.LastRequest?.EvaluationContext.IndicatorSnapshot.Symbol);
        Assert.Equal(ExecutionEnvironment.Live, harness.StrategySignalService.LastRequest?.EffectiveExecutionEnvironment);
        Assert.Equal("BTCUSDT", harness.ExecutionGate.LastRequest?.Symbol);
        Assert.Equal("1m", harness.ExecutionGate.LastRequest?.Timeframe);
        Assert.Equal(btcBot.ExchangeAccountId, harness.ExecutionGate.LastRequest?.ExchangeAccountId);
        Assert.Equal(ExchangeDataPlane.Futures, harness.ExecutionGate.LastRequest?.Plane);
        Assert.Contains("DevelopmentFuturesTestnetPilot=True", harness.ExecutionGate.LastRequest?.Context, StringComparison.Ordinal);
        Assert.Equal("BTCUSDT", harness.UserExecutionOverrideGuard.LastRequest?.Symbol);
        Assert.NotEqual(string.Empty, persistedAttempt.CorrelationId);
        Assert.Equal(persistedAttempt.CorrelationId, harness.ExecutionGate.LastRequest?.CorrelationId);
        Assert.Equal(persistedAttempt.CorrelationId, harness.ExecutionEngine.LastCommand?.CorrelationId);
        var order = await harness.DbContext.ExecutionOrders.SingleAsync(entity => entity.StrategySignalId == persistedAttempt.StrategySignalId);
        Assert.Equal(persistedAttempt.CorrelationId, order.RootCorrelationId);
        Assert.Equal(ExecutionEnvironment.Live, order.ExecutionEnvironment);
        Assert.Equal(ExecutionOrderExecutorKind.Binance, order.ExecutorKind);
        Assert.Equal(persistedAttempt.BotId, order.BotId);
        Assert.Equal(btcBot.ExchangeAccountId, order.ExchangeAccountId);
        Assert.Equal(btcBot.ExchangeAccountId, harness.ExecutionEngine.LastCommand?.ExchangeAccountId);
        Assert.Equal("scanner-handoff", order.IdempotencyKey.Split(':')[0]);
    }

    [Fact]
    public async Task RunOnceAsync_UsesBotLeverageAndMarginType_InLiveHandoffContext_InsteadOfPilotDefaults()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        await using var harness = CreateHarness(
            nowUtc,
            new BotExecutionPilotOptions
            {
                SignalEvaluationMode = ExecutionEnvironment.Live,
                ExecutionDispatchMode = ExecutionEnvironment.Live,
                PilotActivationEnabled = true,
                DefaultLeverage = 5m,
                DefaultMarginType = "CROSS",
                PrimeHistoricalCandleCount = 34
            });
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-btc", "BTCUSDT", "pilot-btc-source");
        var botEntity = await harness.DbContext.TradingBots.SingleAsync(entity => entity.Id == bot.BotId);
        botEntity.Leverage = 1m;
        botEntity.MarginType = "isolated";
        await harness.DbContext.SaveChangesAsync();
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "BTCUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "BTCUSDT", rank: 1, score: 10_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("BTCUSDT", "BTC", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "BTCUSDT", "1m", harness.NowUtc));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Prepared", attempt.ExecutionRequestStatus);
        Assert.Contains("PilotMarginType=ISOLATED", harness.ExecutionGate.LastRequest?.Context, StringComparison.Ordinal);
        Assert.Contains("PilotLeverage=1", harness.ExecutionGate.LastRequest?.Context, StringComparison.Ordinal);
        Assert.DoesNotContain("PilotMarginType=CROSS", harness.ExecutionGate.LastRequest?.Context, StringComparison.Ordinal);
        Assert.DoesNotContain("PilotLeverage=5", harness.ExecutionGate.LastRequest?.Context, StringComparison.Ordinal);
        Assert.Contains("PilotMarginType=ISOLATED", harness.ExecutionEngine.LastCommand?.Context, StringComparison.Ordinal);
        Assert.Contains("PilotLeverage=1", harness.ExecutionEngine.LastCommand?.Context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnceAsync_BlocksLegacyBot_WhenPilotLeverageIsInvalid()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-legacy", "SOLUSDT", "pilot-legacy");
        var botEntity = await harness.DbContext.TradingBots.SingleAsync(entity => entity.Id == bot.BotId);
        botEntity.Leverage = 5m;
        await harness.DbContext.SaveChangesAsync();
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 10_000m);
        await harness.DbContext.SaveChangesAsync();

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("PilotLeverageMustBeOne", attempt.BlockerCode);
        Assert.Equal("NotEvaluated", attempt.StrategyDecisionOutcome);
        Assert.Contains("leverage must resolve to 1x", attempt.BlockerDetail, StringComparison.Ordinal);
        Assert.Null(harness.ExecutionGate.LastRequest);
        Assert.Null(harness.UserExecutionOverrideGuard.LastRequest);
        Assert.Null(harness.ExecutionEngine.LastCommand);
        Assert.False(await harness.DbContext.ExecutionOrders.AnyAsync(entity => entity.StrategySignalId == attempt.StrategySignalId));
    }

    [Fact]
    public async Task RunOnceAsync_UsesProtectedMinNotionalSizingParity_ForEntryQuantity()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-sizing", "BTCUSDT", "pilot-sizing");
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "BTCUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "BTCUSDT", rank: 1, score: 10_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("BTCUSDT", "BTC", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "BTCUSDT", "1m", harness.NowUtc));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        var order = await harness.DbContext.ExecutionOrders.SingleAsync(entity => entity.StrategySignalId == attempt.StrategySignalId);
        Assert.Equal("Prepared", attempt.ExecutionRequestStatus);
        Assert.Equal(1.020m, order.Quantity);
        Assert.Equal(1.020m, harness.ExecutionEngine.LastCommand?.Quantity);
        Assert.Equal(1.020m, harness.UserExecutionOverrideGuard.LastRequest?.Quantity);
    }

    [Fact]
    public async Task RunOnceAsync_FallsBackToExchangeInfoMetadata_WhenCacheMetadataIsMissing()
    {
        var metadata = new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc))
        {
            MinQuantity = 0.001m,
            MinNotional = 100m,
            QuantityPrecision = 3
        };
        await using var harness = CreateHarness(
            new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero),
            exchangeInfoClient: new FakeExchangeInfoClient(new Dictionary<string, SymbolMetadataSnapshot>(StringComparer.Ordinal)
            {
                ["BTCUSDT"] = metadata
            }));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-fallback", "BTCUSDT", "pilot-fallback");
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "BTCUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "BTCUSDT", rank: 1, score: 10_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetPrice("BTCUSDT", 100m);
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "BTCUSDT", "1m", harness.NowUtc));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Prepared", attempt.ExecutionRequestStatus);
        Assert.Equal(1.020m, harness.ExecutionEngine.LastCommand?.Quantity);
        Assert.Equal("BTC", harness.ExecutionEngine.LastCommand?.BaseAsset);
        Assert.Equal("USDT", harness.ExecutionEngine.LastCommand?.QuoteAsset);
    }

    [Fact]
    public async Task RunOnceAsync_PropagatesBotExchangeAccountId_ToDemoExecutionOrder_WhenResolvedTradingModeIsDemo()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        await using var harness = CreateHarness(
            nowUtc,
            new BotExecutionPilotOptions
            {
                SignalEvaluationMode = ExecutionEnvironment.Live,
                ExecutionDispatchMode = ExecutionEnvironment.Live,
                PrimeHistoricalCandleCount = 34
            },
            ExecutionEnvironment.Demo);
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-demo", "SOLUSDT", "pilot-demo");
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 10_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "SOLUSDT", "1m", harness.NowUtc));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Prepared", attempt.ExecutionRequestStatus);
        Assert.Equal(ExecutionEnvironment.Demo, attempt.ExecutionEnvironment);
        Assert.Equal(ExecutionEnvironment.Live, harness.StrategySignalService.LastRequest?.EvaluationContext.Mode);
        Assert.Equal(ExecutionEnvironment.Demo, harness.StrategySignalService.LastRequest?.EffectiveExecutionEnvironment);
        Assert.Equal(ExecutionEnvironment.Demo, harness.ExecutionGate.LastRequest?.Environment);
        Assert.Equal(bot.ExchangeAccountId, harness.ExecutionGate.LastRequest?.ExchangeAccountId);
        Assert.DoesNotContain("DevelopmentFuturesTestnetPilot=True", harness.ExecutionGate.LastRequest?.Context, StringComparison.Ordinal);
        Assert.Equal(ExecutionEnvironment.Demo, harness.UserExecutionOverrideGuard.LastRequest?.Environment);
        var order = await harness.DbContext.ExecutionOrders.SingleAsync(entity => entity.StrategySignalId == attempt.StrategySignalId);
        Assert.Equal(ExecutionEnvironment.Demo, order.ExecutionEnvironment);
        Assert.Equal(bot.ExchangeAccountId, order.ExchangeAccountId);
        Assert.Equal(ExecutionOrderExecutorKind.Virtual, order.ExecutorKind);
        Assert.Equal(ExecutionEnvironment.Demo, harness.ExecutionEngine.LastCommand?.RequestedEnvironment);
        Assert.Null(harness.ExecutionEngine.LastCommand?.IsDemo);
        Assert.Equal(bot.ExchangeAccountId, harness.ExecutionEngine.LastCommand?.ExchangeAccountId);
    }

    [Fact]
    public async Task RunOnceAsync_UsesConfiguredLiveDispatchMode_WhenPilotIsActive_EvenIfTradingModeResolverReturnsDemo()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        await using var harness = CreateHarness(
            nowUtc,
            new BotExecutionPilotOptions
            {
                SignalEvaluationMode = ExecutionEnvironment.Live,
                ExecutionDispatchMode = ExecutionEnvironment.Live,
                PilotActivationEnabled = true,
                PrimeHistoricalCandleCount = 34
            },
            ExecutionEnvironment.Demo);
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-live-pilot", "BTCUSDT", "pilot-live-dispatch");
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "BTCUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "BTCUSDT", rank: 1, score: 10_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("BTCUSDT", "BTC", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "BTCUSDT", "1m", harness.NowUtc));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Prepared", attempt.ExecutionRequestStatus);
        Assert.Equal(ExecutionEnvironment.Live, attempt.ExecutionEnvironment);
        Assert.Equal(ExecutionEnvironment.Live, harness.StrategySignalService.LastRequest?.EffectiveExecutionEnvironment);
        Assert.Equal(ExecutionEnvironment.Live, harness.ExecutionGate.LastRequest?.Environment);
        Assert.Contains("DevelopmentFuturesTestnetPilot=True", harness.ExecutionGate.LastRequest?.Context, StringComparison.Ordinal);
        Assert.Equal(ExecutionEnvironment.Live, harness.UserExecutionOverrideGuard.LastRequest?.Environment);
        var order = await harness.DbContext.ExecutionOrders.SingleAsync(entity => entity.StrategySignalId == attempt.StrategySignalId);
        Assert.Equal(ExecutionEnvironment.Live, order.ExecutionEnvironment);
        Assert.Equal(ExecutionEnvironment.Live, harness.ExecutionEngine.LastCommand?.RequestedEnvironment);
        Assert.Null(harness.ExecutionEngine.LastCommand?.IsDemo);
    }

    [Fact]
    public async Task RunOnceAsync_UsesConfiguredDemoDispatchMode_WhenPilotIsActive_EvenIfTradingModeResolverReturnsLive()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        await using var harness = CreateHarness(
            nowUtc,
            new BotExecutionPilotOptions
            {
                SignalEvaluationMode = ExecutionEnvironment.Live,
                ExecutionDispatchMode = ExecutionEnvironment.Demo,
                PilotActivationEnabled = true,
                PrimeHistoricalCandleCount = 34
            },
            ExecutionEnvironment.Live);
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-demo-pilot", "SOLUSDT", "pilot-demo-dispatch");
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 10_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "SOLUSDT", "1m", harness.NowUtc));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Prepared", attempt.ExecutionRequestStatus);
        Assert.Equal(ExecutionEnvironment.Demo, attempt.ExecutionEnvironment);
        Assert.Equal(ExecutionEnvironment.Live, harness.StrategySignalService.LastRequest?.EvaluationContext.Mode);
        Assert.Equal(ExecutionEnvironment.Demo, harness.StrategySignalService.LastRequest?.EffectiveExecutionEnvironment);
        Assert.Equal(ExecutionEnvironment.Demo, harness.ExecutionGate.LastRequest?.Environment);
        Assert.Equal(ExecutionEnvironment.Demo, harness.UserExecutionOverrideGuard.LastRequest?.Environment);
        var order = await harness.DbContext.ExecutionOrders.SingleAsync(entity => entity.StrategySignalId == attempt.StrategySignalId);
        Assert.Equal(ExecutionEnvironment.Demo, order.ExecutionEnvironment);
        Assert.Equal(ExecutionOrderExecutorKind.Virtual, order.ExecutorKind);
        Assert.Equal(ExecutionEnvironment.Demo, harness.ExecutionEngine.LastCommand?.RequestedEnvironment);
        Assert.Null(harness.ExecutionEngine.LastCommand?.IsDemo);
    }

    [Fact]
    public async Task RunOnceAsync_UsesConfiguredBinanceTestnetDispatchMode_WhenPilotIsActive_EvenIfTradingModeResolverReturnsLive()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        await using var harness = CreateHarness(
            nowUtc,
            new BotExecutionPilotOptions
            {
                SignalEvaluationMode = ExecutionEnvironment.Live,
                ExecutionDispatchMode = ExecutionEnvironment.BinanceTestnet,
                PilotActivationEnabled = true,
                PrimeHistoricalCandleCount = 34
            },
            ExecutionEnvironment.Live);
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-testnet-pilot", "SOLUSDT", "pilot-testnet-dispatch");
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 10_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "SOLUSDT", "1m", harness.NowUtc));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Prepared", attempt.ExecutionRequestStatus);
        Assert.Equal(ExecutionEnvironment.BinanceTestnet, attempt.ExecutionEnvironment);
        Assert.Equal(ExecutionEnvironment.Live, harness.StrategySignalService.LastRequest?.EvaluationContext.Mode);
        Assert.Equal(ExecutionEnvironment.BinanceTestnet, harness.StrategySignalService.LastRequest?.EffectiveExecutionEnvironment);
        Assert.Equal(ExecutionEnvironment.BinanceTestnet, harness.ExecutionGate.LastRequest?.Environment);
        Assert.Contains("DevelopmentFuturesTestnetPilot=True", harness.ExecutionGate.LastRequest?.Context, StringComparison.Ordinal);
        Assert.Equal(ExecutionEnvironment.BinanceTestnet, harness.UserExecutionOverrideGuard.LastRequest?.Environment);
        var order = await harness.DbContext.ExecutionOrders.SingleAsync(entity => entity.StrategySignalId == attempt.StrategySignalId);
        Assert.Equal(ExecutionEnvironment.BinanceTestnet, order.ExecutionEnvironment);
        Assert.Equal(ExecutionOrderExecutorKind.BinanceTestnet, order.ExecutorKind);
        Assert.Equal(ExecutionEnvironment.BinanceTestnet, harness.ExecutionEngine.LastCommand?.RequestedEnvironment);
        Assert.Null(harness.ExecutionEngine.LastCommand?.IsDemo);
    }

    [Fact]
    public async Task RunOnceAsync_PreparesShortEntryOrder_WhenStrategyDirectionIsShort()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-short", "SOLUSDT", "pilot-short");
        var botEntity = await harness.DbContext.TradingBots.SingleAsync(entity => entity.Id == bot.BotId);
        botEntity.DirectionMode = TradingBotDirectionMode.LongShort;
        await harness.DbContext.SaveChangesAsync();
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 10_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(
            bot.TradingStrategyId,
            bot.TradingStrategyVersionId,
            "SOLUSDT",
            "1m",
            harness.NowUtc,
            direction: StrategyTradeDirection.Short));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        var order = await harness.DbContext.ExecutionOrders.SingleAsync(entity => entity.StrategySignalId == attempt.StrategySignalId);
        Assert.Equal("Prepared", attempt.ExecutionRequestStatus);
        Assert.Equal(ExecutionOrderSide.Sell, attempt.ExecutionSide);
        Assert.Equal(ExecutionOrderSide.Sell, order.Side);
        Assert.False(order.ReduceOnly);
        Assert.Equal(ExecutionOrderSide.Sell, harness.ExecutionEngine.LastCommand?.Side);
        Assert.False(harness.ExecutionEngine.LastCommand?.ReduceOnly);
    }

    [Fact]
    public async Task RunOnceAsync_PreparesShortEntryOrder_WhenBotDirectionModeIsShortOnly()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-short-only", "SOLUSDT", "pilot-short-only");
        var botEntity = await harness.DbContext.TradingBots.SingleAsync(entity => entity.Id == bot.BotId);
        botEntity.DirectionMode = TradingBotDirectionMode.ShortOnly;
        await harness.DbContext.SaveChangesAsync();
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 10_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(
            bot.TradingStrategyId,
            bot.TradingStrategyVersionId,
            "SOLUSDT",
            "1m",
            harness.NowUtc,
            direction: StrategyTradeDirection.Short));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        var order = await harness.DbContext.ExecutionOrders.SingleAsync(entity => entity.StrategySignalId == attempt.StrategySignalId);
        Assert.Equal("Prepared", attempt.ExecutionRequestStatus);
        Assert.Equal(ExecutionOrderSide.Sell, attempt.ExecutionSide);
        Assert.Equal(ExecutionOrderSide.Sell, order.Side);
        Assert.False(order.ReduceOnly);
        Assert.Equal(ExecutionOrderSide.Sell, harness.ExecutionEngine.LastCommand?.Side);
        Assert.False(harness.ExecutionEngine.LastCommand?.ReduceOnly);
    }

    [Fact]
    public async Task RunOnceAsync_BlocksShortEntry_WhenBotDirectionModeIsLongOnly()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-short-block", "SOLUSDT", "pilot-short-block");
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 10_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(
            bot.TradingStrategyId,
            bot.TradingStrategyVersionId,
            "SOLUSDT",
            "1m",
            harness.NowUtc,
            direction: StrategyTradeDirection.Short));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("EntryDirectionModeBlocked", attempt.BlockerCode);
        Assert.Equal("Persisted", attempt.StrategyDecisionOutcome);
        Assert.Equal(ExecutionOrderSide.Sell, attempt.ExecutionSide);
        Assert.Contains("does not allow short entries", attempt.BlockerDetail, StringComparison.Ordinal);
        Assert.Null(harness.ExecutionGate.LastRequest);
        Assert.Null(harness.UserExecutionOverrideGuard.LastRequest);
        Assert.Null(harness.ExecutionEngine.LastCommand);
        Assert.False(await harness.DbContext.ExecutionOrders.AnyAsync(entity => entity.StrategySignalId == attempt.StrategySignalId));
    }

    [Fact]
    public async Task RunOnceAsync_PersistsAiShadowDecision_WhenContextRichEntryDirectionModeBlocked()
    {
        await using var harness = CreateHarness(
            new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero),
            aiSignalOptions: new AiSignalOptions
            {
                Enabled = true,
                ShadowModeEnabled = false,
                SelectedProvider = ShadowLinearAiSignalProviderAdapter.ProviderNameValue
            },
            aiShadowDecisionService: new ThrowingAiShadowDecisionService());
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-direction-block-shadow", "SOLUSDT", "pilot-direction-block-shadow");
        var botEntity = await harness.DbContext.TradingBots.SingleAsync(entity => entity.Id == bot.BotId);
        botEntity.DirectionMode = TradingBotDirectionMode.ShortOnly;
        await harness.DbContext.SaveChangesAsync();
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 10_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(
            bot.TradingStrategyId,
            bot.TradingStrategyVersionId,
            "SOLUSDT",
            "1m",
            harness.NowUtc,
            direction: StrategyTradeDirection.Long,
            aiEvaluation: CreateShadowAiEvaluation()));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);
        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("EntryDirectionModeBlocked", attempt.BlockerCode);
        Assert.Equal(bot.BotId, attempt.BotId);
        Assert.Equal("SOLUSDT", attempt.SelectedSymbol);
        Assert.Equal(ExecutionEnvironment.Live, attempt.ExecutionEnvironment);
        Assert.Equal(ExecutionOrderSide.Buy, attempt.ExecutionSide);
        Assert.Equal("Persisted", attempt.StrategyDecisionOutcome);
        Assert.NotNull(attempt.StrategySignalId);
        Assert.False(string.IsNullOrWhiteSpace(attempt.CorrelationId));
        var shadow = await harness.DbContext.AiShadowDecisions.SingleAsync();

        Assert.Equal(bot.BotId, shadow.BotId);
        Assert.Equal(attempt.StrategySignalId, shadow.StrategySignalId);
        Assert.Equal(attempt.CorrelationId, shadow.CorrelationId);
        Assert.Equal("SOLUSDT", shadow.Symbol);
        Assert.Equal("1m", shadow.Timeframe);
        Assert.Equal("EntryDirectionModeBlocked", shadow.NoSubmitReason);
        Assert.False(shadow.HypotheticalSubmitAllowed);
        Assert.Equal("NoSubmit", shadow.FinalAction);
        Assert.Equal("EntryDirectionModeBlocked", shadow.HypotheticalBlockReason);
        Assert.Equal(ShadowLinearAiSignalProviderAdapter.ProviderNameValue, shadow.AiProviderName);
        Assert.Equal("shadow-linear-v1", shadow.AiProviderModel);
        Assert.Equal(0.25m, shadow.AiAdvisoryScore);
        Assert.Contains("CompressionBreakoutSetupDetected +0.25", shadow.AiContributionSummary, StringComparison.Ordinal);
        Assert.False(await harness.DbContext.ExecutionOrders.AnyAsync(entity => entity.StrategySignalId == attempt.StrategySignalId));
        Assert.Null(harness.ExecutionGate.LastRequest);
        Assert.Null(harness.UserExecutionOverrideGuard.LastRequest);
        Assert.Null(harness.ExecutionEngine.LastCommand);
    }

    [Fact]
    public async Task RunOnceAsync_PersistsAiShadowDecisionOutcome_WhenContextRichEntryDirectionModeBlocked_IsMature()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var signalTimeUtc = nowUtc.UtcDateTime.AddMinutes(-2);
        await using var harness = CreateHarness(
            nowUtc,
            aiSignalOptions: new AiSignalOptions
            {
                Enabled = true,
                ShadowModeEnabled = false,
                SelectedProvider = ShadowLinearAiSignalProviderAdapter.ProviderNameValue
            },
            aiShadowDecisionService: new ThrowingAiShadowDecisionService());
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-direction-block-shadow-outcome", "SOLUSDT", "pilot-direction-block-shadow-outcome");
        var botEntity = await harness.DbContext.TradingBots.SingleAsync(entity => entity.Id == bot.BotId);
        botEntity.DirectionMode = TradingBotDirectionMode.ShortOnly;
        await harness.DbContext.SaveChangesAsync();
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 10_000m);
        harness.DbContext.HistoricalMarketCandles.AddRange(
            new HistoricalMarketCandle
            {
                Id = Guid.NewGuid(),
                Symbol = "SOLUSDT",
                Interval = "1m",
                OpenTimeUtc = signalTimeUtc.AddMinutes(-1),
                CloseTimeUtc = signalTimeUtc,
                OpenPrice = 100m,
                HighPrice = 100.5m,
                LowPrice = 99.5m,
                ClosePrice = 100m,
                Volume = 1000m,
                ReceivedAtUtc = signalTimeUtc,
                Source = "unit-test"
            },
            new HistoricalMarketCandle
            {
                Id = Guid.NewGuid(),
                Symbol = "SOLUSDT",
                Interval = "1m",
                OpenTimeUtc = signalTimeUtc,
                CloseTimeUtc = signalTimeUtc.AddMinutes(1),
                OpenPrice = 101m,
                HighPrice = 101.5m,
                LowPrice = 100.5m,
                ClosePrice = 101m,
                Volume = 1010m,
                ReceivedAtUtc = signalTimeUtc.AddMinutes(1),
                Source = "unit-test"
            });
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", nowUtc.UtcDateTime));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(
            bot.TradingStrategyId,
            bot.TradingStrategyVersionId,
            "SOLUSDT",
            "1m",
            signalTimeUtc,
            direction: StrategyTradeDirection.Long,
            aiEvaluation: CreateShadowAiEvaluation()));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);
        var shadowDecision = await harness.DbContext.AiShadowDecisions.SingleAsync();
        var outcome = await harness.DbContext.AiShadowDecisionOutcomes.SingleAsync();

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("EntryDirectionModeBlocked", attempt.BlockerCode);
        Assert.Equal(shadowDecision.Id, outcome.AiShadowDecisionId);
        Assert.Equal(AiShadowOutcomeState.Scored, outcome.OutcomeState);
        Assert.Equal(AiShadowFutureDataAvailability.Available, outcome.FutureDataAvailability);
        Assert.False(await harness.DbContext.ExecutionOrders.AnyAsync(entity => entity.StrategySignalId == attempt.StrategySignalId));
    }

    [Fact]
    public async Task RunOnceAsync_LogsCaptureSkippedReason_WhenAiShadowDecisionServiceMissing()
    {
        var logger = new RecordingLogger<MarketScannerHandoffService>();
        await using var harness = CreateHarness(
            new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero),
            aiSignalOptions: new AiSignalOptions
            {
                Enabled = true,
                ShadowModeEnabled = false,
                SelectedProvider = ShadowLinearAiSignalProviderAdapter.ProviderNameValue
            },
            logger: logger,
            registerAiShadowDecisionService: false,
            aiShadowDecisionService: null);
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-direction-block-no-shadow-service", "SOLUSDT", "pilot-direction-block-no-shadow-service");
        var botEntity = await harness.DbContext.TradingBots.SingleAsync(entity => entity.Id == bot.BotId);
        botEntity.DirectionMode = TradingBotDirectionMode.ShortOnly;
        await harness.DbContext.SaveChangesAsync();
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 10_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(
            bot.TradingStrategyId,
            bot.TradingStrategyVersionId,
            "SOLUSDT",
            "1m",
            harness.NowUtc,
            direction: StrategyTradeDirection.Long,
            aiEvaluation: CreateShadowAiEvaluation()));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("EntryDirectionModeBlocked", attempt.BlockerCode);
        Assert.Empty(harness.DbContext.AiShadowDecisions);
        Assert.Contains(
            logger.Records,
            record => record.Level == LogLevel.Information &&
                      record.Message.Contains("AiShadowDecisionCaptureSkipped", StringComparison.Ordinal) &&
                      record.Message.Contains("ServiceMissing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunOnceAsync_SuppressesSameDirectionLongEntry_WhenLiveLongPositionAlreadyExists()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-long-open", "BTCUSDT", "pilot-long-open");
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "BTCUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "BTCUSDT", rank: 1, score: 10_000m);
        await SeedExchangePositionAsync(harness.DbContext, bot, "BTCUSDT", quantity: 0.020m, entryPrice: 100m, positionSide: "BOTH", observedAtUtc: harness.NowUtc);
        harness.MarketDataService.SetMetadata("BTCUSDT", "BTC", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "BTCUSDT", "1m", harness.NowUtc));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("SameDirectionLongEntrySuppressed", attempt.BlockerCode);
        Assert.Equal("Persisted", attempt.StrategyDecisionOutcome);
        Assert.Contains("open long position already exists", attempt.BlockerDetail, StringComparison.Ordinal);
        Assert.Null(harness.ExecutionGate.LastRequest);
        Assert.Null(harness.UserExecutionOverrideGuard.LastRequest);
        Assert.Null(harness.ExecutionEngine.LastCommand);
        Assert.Empty(harness.DbContext.ExecutionOrders);
    }

    [Fact]
    public async Task RunOnceAsync_SuppressesSameDirectionShortEntry_WhenLiveShortPositionAlreadyExists()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-short-open", "SOLUSDT", "pilot-short-open");
        var botEntity = await harness.DbContext.TradingBots.SingleAsync(entity => entity.Id == bot.BotId);
        botEntity.DirectionMode = TradingBotDirectionMode.LongShort;
        await harness.DbContext.SaveChangesAsync();
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 10_000m);
        await SeedExchangePositionAsync(harness.DbContext, bot, "SOLUSDT", quantity: 0.030m, entryPrice: 100m, positionSide: "SHORT", observedAtUtc: harness.NowUtc);
        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(
            bot.TradingStrategyId,
            bot.TradingStrategyVersionId,
            "SOLUSDT",
            "1m",
            harness.NowUtc,
            direction: StrategyTradeDirection.Short));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("SameDirectionShortEntrySuppressed", attempt.BlockerCode);
        Assert.Equal("Persisted", attempt.StrategyDecisionOutcome);
        Assert.Contains("open short position already exists", attempt.BlockerDetail, StringComparison.Ordinal);
        Assert.Null(harness.ExecutionGate.LastRequest);
        Assert.Null(harness.UserExecutionOverrideGuard.LastRequest);
        Assert.Null(harness.ExecutionEngine.LastCommand);
        Assert.Empty(harness.DbContext.ExecutionOrders);
    }

    [Fact]
    public async Task RunOnceAsync_ConvertsOpposingLongEntryIntoCloseOnlyBuyExit_WhenLiveShortPositionExists()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-reverse", "BTCUSDT", "pilot-reverse");
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "BTCUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "BTCUSDT", rank: 1, score: 10_000m);
        await SeedExchangePositionAsync(harness.DbContext, bot, "BTCUSDT", quantity: 0.020m, entryPrice: 100m, positionSide: "SHORT", observedAtUtc: harness.NowUtc);
        harness.MarketDataService.SetMetadata("BTCUSDT", "BTC", "USDT");
        harness.MarketDataService.SetPrice("BTCUSDT", 99m);
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "BTCUSDT", "1m", harness.NowUtc));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Prepared", attempt.ExecutionRequestStatus);
        Assert.Equal("Persisted", attempt.StrategyDecisionOutcome);
        Assert.Equal(ExecutionOrderSide.Buy, attempt.ExecutionSide);
        Assert.Equal(0.020m, attempt.ExecutionQuantity);
        Assert.Contains("ExecutionIntent=ExitCloseOnly", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("OpenPositionQuantity=-0.02", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("CloseQuantity=0.02", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("CloseSide=Buy", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("ReduceOnly=True", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("AutoReverse=False", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("ExitReason=ReverseSignal", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("ExitPnlGuard=Allowed", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("ReasonCode=ExitCloseOnlyAllowedTakeProfit", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.NotNull(harness.ExecutionGate.LastRequest);
        Assert.NotNull(harness.UserExecutionOverrideGuard.LastRequest);
        Assert.Equal(StrategySignalType.Exit, harness.ExecutionEngine.LastCommand?.SignalType);
        Assert.Equal(ExecutionOrderSide.Buy, harness.ExecutionEngine.LastCommand?.Side);
        Assert.True(harness.ExecutionEngine.LastCommand?.ReduceOnly);
        Assert.Contains("ExecutionIntent=ExitCloseOnly", harness.ExecutionEngine.LastCommand?.Context, StringComparison.Ordinal);
        var order = await harness.DbContext.ExecutionOrders.SingleAsync(entity => entity.StrategySignalId == attempt.StrategySignalId);
        Assert.Equal(StrategySignalType.Exit, order.SignalType);
        Assert.True(order.ReduceOnly);
        Assert.Equal(ExecutionOrderSide.Buy, order.Side);
    }

    [Fact]
    public async Task RunOnceAsync_ConvertsOpposingShortEntryIntoCloseOnlySellExit_WhenLiveLongPositionExists()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-reverse-long", "SOLUSDT", "pilot-reverse-long");
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 10_000m);
        await SeedExchangePositionAsync(harness.DbContext, bot, "SOLUSDT", quantity: 0.030m, entryPrice: 100m, positionSide: "BOTH", observedAtUtc: harness.NowUtc);
        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.MarketDataService.SetPrice("SOLUSDT", 101m);
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(
            bot.TradingStrategyId,
            bot.TradingStrategyVersionId,
            "SOLUSDT",
            "1m",
            harness.NowUtc,
            direction: StrategyTradeDirection.Short));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Prepared", attempt.ExecutionRequestStatus);
        Assert.Equal("Persisted", attempt.StrategyDecisionOutcome);
        Assert.Equal(ExecutionOrderSide.Sell, attempt.ExecutionSide);
        Assert.Equal(0.030m, attempt.ExecutionQuantity);
        Assert.Contains("ExecutionIntent=ExitCloseOnly", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("OpenPositionQuantity=0.03", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("CloseQuantity=0.03", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("CloseSide=Sell", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("ReduceOnly=True", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("ExitReason=ReverseSignal", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("ExitPnlGuard=Allowed", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("ReasonCode=ExitCloseOnlyAllowedTakeProfit", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Equal(StrategySignalType.Exit, harness.ExecutionEngine.LastCommand?.SignalType);
        Assert.Equal(ExecutionOrderSide.Sell, harness.ExecutionEngine.LastCommand?.Side);
        Assert.True(harness.ExecutionEngine.LastCommand?.ReduceOnly);
    }

    [Fact]
    public async Task RunOnceAsync_BlocksCloseOnlySellExit_WhenOpenLongPositionWouldCloseAtALoss()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-closeonly-loss-long", "SOLUSDT", "pilot-closeonly-loss-long");
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 10_000m);
        await SeedExchangePositionAsync(harness.DbContext, bot, "SOLUSDT", quantity: 0.030m, entryPrice: 100m, positionSide: "BOTH", observedAtUtc: harness.NowUtc);
        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.MarketDataService.SetPrice("SOLUSDT", 99m);
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(
            bot.TradingStrategyId,
            bot.TradingStrategyVersionId,
            "SOLUSDT",
            "1m",
            harness.NowUtc,
            direction: StrategyTradeDirection.Short));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("ExitCloseOnlyBlockedUnprofitableLong", attempt.BlockerCode);
        Assert.Contains("ExitReason=BlockedUnprofitable", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("ExitPnlGuard=Blocked", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("ReasonCode=ExitCloseOnlyBlockedUnprofitableLong", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("EstimatedPnlQuote=-0.03", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Null(harness.ExecutionGate.LastRequest);
        Assert.Null(harness.UserExecutionOverrideGuard.LastRequest);
        Assert.Null(harness.ExecutionEngine.LastCommand);
        Assert.Empty(harness.DbContext.ExecutionOrders);
    }

    [Fact]
    public async Task RunOnceAsync_BlocksCloseOnlyBuyExit_WhenOpenShortPositionWouldCloseAtALoss()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-closeonly-loss-short", "BTCUSDT", "pilot-closeonly-loss-short");
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "BTCUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "BTCUSDT", rank: 1, score: 10_000m);
        await SeedExchangePositionAsync(harness.DbContext, bot, "BTCUSDT", quantity: 0.020m, entryPrice: 100m, positionSide: "SHORT", observedAtUtc: harness.NowUtc);
        harness.MarketDataService.SetMetadata("BTCUSDT", "BTC", "USDT");
        harness.MarketDataService.SetPrice("BTCUSDT", 101m);
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(
            bot.TradingStrategyId,
            bot.TradingStrategyVersionId,
            "BTCUSDT",
            "1m",
            harness.NowUtc));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("ExitCloseOnlyBlockedUnprofitableShort", attempt.BlockerCode);
        Assert.Contains("ExitReason=BlockedUnprofitable", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("ExitPnlGuard=Blocked", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("ReasonCode=ExitCloseOnlyBlockedUnprofitableShort", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("EstimatedPnlQuote=-0.02", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Null(harness.ExecutionGate.LastRequest);
        Assert.Null(harness.UserExecutionOverrideGuard.LastRequest);
        Assert.Null(harness.ExecutionEngine.LastCommand);
        Assert.Empty(harness.DbContext.ExecutionOrders);
    }

    [Fact]
    public async Task RunOnceAsync_BlocksShortEntry_WhenBullishScannerAdvisoryConflicts()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-short-conflict", "SOLUSDT", "pilot-short-conflict");
        var botEntity = await harness.DbContext.TradingBots.SingleAsync(entity => entity.Id == bot.BotId);
        botEntity.DirectionMode = TradingBotDirectionMode.LongShort;
        await harness.DbContext.SaveChangesAsync();
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(
            harness.DbContext,
            scanCycleId,
            "SOLUSDT",
            rank: 1,
            score: 10_000m,
            scoringSummary: "StrategyScore=92; ScannerLabels=HasTrendBreakoutUp; ScannerReasonCodes=TrendBreakoutConfirmed");
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(
            bot.TradingStrategyId,
            bot.TradingStrategyVersionId,
            "SOLUSDT",
            "1m",
            harness.NowUtc,
            direction: StrategyTradeDirection.Short));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("DirectionalConflictShortAgainstBullishScanner", attempt.BlockerCode);
        Assert.Contains("bullish scanner advisory conflicts", attempt.BlockerDetail, StringComparison.Ordinal);
        Assert.Contains("ScannerDirectionalConflict=ShortAgainstBullish", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Null(harness.ExecutionGate.LastRequest);
        Assert.Null(harness.UserExecutionOverrideGuard.LastRequest);
        Assert.Null(harness.ExecutionEngine.LastCommand);
        Assert.Empty(harness.DbContext.ExecutionOrders);
    }

    [Fact]
    public async Task RunOnceAsync_BlocksLongEntry_WhenBearishScannerAdvisoryConflicts()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-long-conflict", "BTCUSDT", "pilot-long-conflict");
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "BTCUSDT");
        SeedCandidate(
            harness.DbContext,
            scanCycleId,
            "BTCUSDT",
            rank: 1,
            score: 10_000m,
            scoringSummary: "StrategyScore=90; ScannerLabels=HasTrendBreakoutDown; ScannerReasonCodes=TrendBreakdownConfirmed");
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("BTCUSDT", "BTC", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(
            bot.TradingStrategyId,
            bot.TradingStrategyVersionId,
            "BTCUSDT",
            "1m",
            harness.NowUtc));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("DirectionalConflictLongAgainstBearishScanner", attempt.BlockerCode);
        Assert.Contains("bearish scanner advisory conflicts", attempt.BlockerDetail, StringComparison.Ordinal);
        Assert.Contains("ScannerDirectionalConflict=LongAgainstBearish", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Null(harness.ExecutionGate.LastRequest);
        Assert.Null(harness.UserExecutionOverrideGuard.LastRequest);
        Assert.Null(harness.ExecutionEngine.LastCommand);
        Assert.Empty(harness.DbContext.ExecutionOrders);
    }

    [Fact]
    public async Task RunOnceAsync_AllowsCloseOnlyExit_WhenOpposingEntryWouldConflictWithScannerDirection()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-closeonly-conflict", "BTCUSDT", "pilot-closeonly-conflict");
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "BTCUSDT");
        SeedCandidate(
            harness.DbContext,
            scanCycleId,
            "BTCUSDT",
            rank: 1,
            score: 10_000m,
            scoringSummary: "StrategyScore=89; ScannerLabels=HasTrendBreakoutDown; ScannerReasonCodes=TrendBreakdownConfirmed");
        await SeedExchangePositionAsync(harness.DbContext, bot, "BTCUSDT", quantity: 0.020m, entryPrice: 100m, positionSide: "SHORT", observedAtUtc: harness.NowUtc);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("BTCUSDT", "BTC", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "BTCUSDT", "1m", harness.NowUtc));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Prepared", attempt.ExecutionRequestStatus);
        Assert.Equal("Persisted", attempt.StrategyDecisionOutcome);
        Assert.Equal(ExecutionOrderSide.Buy, attempt.ExecutionSide);
        Assert.Contains("ExecutionIntent=ExitCloseOnly", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("DirectionalConflict", attempt.BlockerCode ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(StrategySignalType.Exit, harness.ExecutionEngine.LastCommand?.SignalType);
        Assert.True(harness.ExecutionEngine.LastCommand?.ReduceOnly);
    }

    [Fact]
    public async Task RunOnceAsync_UsesStableCloseOnlyPrivatePlaneStaleBlocker_WhenGateRejectsCloseOnlyExit()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-closeonly-stale", "SOLUSDT", "pilot-closeonly-stale");
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 10_000m);
        await SeedExchangePositionAsync(harness.DbContext, bot, "SOLUSDT", quantity: 0.030m, entryPrice: 100m, positionSide: "SHORT", observedAtUtc: harness.NowUtc);
        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "SOLUSDT", "1m", harness.NowUtc));
        harness.ExecutionGate.BlockSymbol(
            "SOLUSDT",
            ExecutionGateBlockedReason.PrivatePlaneStale,
            "Execution blocked because private plane is stale. PrivatePlaneFreshness=Stale; LastPrivateSyncAtUtc=2026-04-03T11:54:00.0000000Z");

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("ExitCloseOnlyBlockedPrivatePlaneStale", attempt.BlockerCode);
        Assert.Contains("ExecutionIntent=ExitCloseOnly", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("ExitReason=PrivatePlaneStale", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Contains("ReduceOnly=True", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Null(harness.ExecutionEngine.LastCommand);
        Assert.Empty(harness.DbContext.ExecutionOrders);
    }

    [Fact]
    public async Task RunOnceAsync_BlocksLongReentry_WhenEntryHysteresisIsActive()
    {
        await using var harness = CreateHarness(
            new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero),
            new BotExecutionPilotOptions
            {
                SignalEvaluationMode = ExecutionEnvironment.Live,
                PrimeHistoricalCandleCount = 34,
                EnableEntryHysteresis = true,
                EntryHysteresisCooldownMinutes = 15,
                EntryHysteresisReentryBufferPercentage = 0m,
                LongEntryHysteresisCooldownMinutes = 15,
                LongEntryHysteresisReentryBufferPercentage = 0.20m
            });
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-long-hysteresis", "BTCUSDT", "pilot-long-hysteresis");
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "BTCUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "BTCUSDT", rank: 1, score: 10_000m);
        await SeedFilledReduceOnlyExitOrderAsync(
            harness.DbContext,
            bot,
            "BTCUSDT",
            price: 100m,
            createdAtUtc: harness.NowUtc.AddMinutes(-10));
        await SeedExchangeAccountSyncStateAsync(
            harness.DbContext,
            bot.OwnerUserId,
            bot.ExchangeAccountId,
            harness.NowUtc);
        harness.MarketDataService.SetMetadata("BTCUSDT", "BTC", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "BTCUSDT", "1m", harness.NowUtc));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("LongEntryHysteresisActive", attempt.BlockerCode);
        Assert.Equal("Persisted", attempt.StrategyDecisionOutcome);
        Assert.Contains("cooldown is still active", attempt.BlockerDetail, StringComparison.Ordinal);
        Assert.Null(harness.ExecutionGate.LastRequest);
        Assert.Null(harness.UserExecutionOverrideGuard.LastRequest);
        Assert.Null(harness.ExecutionEngine.LastCommand);
        Assert.False(await harness.DbContext.ExecutionOrders.AnyAsync(entity => entity.StrategySignalId == attempt.StrategySignalId));
    }

    [Fact]
    public async Task RunOnceAsync_BlocksShortReentry_WhenEntryHysteresisIsActive()
    {
        await using var harness = CreateHarness(
            new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero),
            new BotExecutionPilotOptions
            {
                SignalEvaluationMode = ExecutionEnvironment.Live,
                PrimeHistoricalCandleCount = 34,
                EnableEntryHysteresis = true,
                EntryHysteresisCooldownMinutes = 15,
                EntryHysteresisReentryBufferPercentage = 0m,
                ShortEntryHysteresisCooldownMinutes = 15,
                ShortEntryHysteresisReentryBufferPercentage = 0.20m
            });
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-short-hysteresis", "SOLUSDT", "pilot-short-hysteresis");
        var botEntity = await harness.DbContext.TradingBots.SingleAsync(entity => entity.Id == bot.BotId);
        botEntity.DirectionMode = TradingBotDirectionMode.LongShort;
        await harness.DbContext.SaveChangesAsync();
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 10_000m);
        await SeedFilledReduceOnlyExitOrderAsync(
            harness.DbContext,
            bot,
            "SOLUSDT",
            price: 100m,
            createdAtUtc: harness.NowUtc.AddMinutes(-10),
            side: ExecutionOrderSide.Buy);
        await SeedExchangeAccountSyncStateAsync(
            harness.DbContext,
            bot.OwnerUserId,
            bot.ExchangeAccountId,
            harness.NowUtc);
        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(
            bot.TradingStrategyId,
            bot.TradingStrategyVersionId,
            "SOLUSDT",
            "1m",
            harness.NowUtc,
            direction: StrategyTradeDirection.Short));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("ShortEntryHysteresisActive", attempt.BlockerCode);
        Assert.Equal("Persisted", attempt.StrategyDecisionOutcome);
        Assert.Contains("cooldown is still active", attempt.BlockerDetail, StringComparison.Ordinal);
        Assert.Null(harness.ExecutionGate.LastRequest);
        Assert.Null(harness.UserExecutionOverrideGuard.LastRequest);
        Assert.Null(harness.ExecutionEngine.LastCommand);
        Assert.False(await harness.DbContext.ExecutionOrders.AnyAsync(entity => entity.StrategySignalId == attempt.StrategySignalId));
    }

    [Fact]
    public async Task RunOnceAsync_PersistsAiShadowDecision_WhenShadowModeActiveAndShortEntryHysteresisBlocks()
    {
        await using var harness = CreateHarness(
            new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero),
            new BotExecutionPilotOptions
            {
                SignalEvaluationMode = ExecutionEnvironment.Live,
                ExecutionDispatchMode = ExecutionEnvironment.Demo,
                PrimeHistoricalCandleCount = 34,
                EnableEntryHysteresis = true,
                EntryHysteresisCooldownMinutes = 0,
                EntryHysteresisReentryBufferPercentage = 0m,
                ShortEntryHysteresisCooldownMinutes = 0,
                ShortEntryHysteresisReentryBufferPercentage = 0.20m
            },
            resolvedTradingMode: ExecutionEnvironment.Demo,
            aiSignalOptions: CreateShadowAiOptions());
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-short-shadow", "SOLUSDT", "pilot-short-shadow");
        var botEntity = await harness.DbContext.TradingBots.SingleAsync(entity => entity.Id == bot.BotId);
        botEntity.DirectionMode = TradingBotDirectionMode.LongShort;
        await harness.DbContext.SaveChangesAsync();
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 10_000m);
        await SeedFilledReduceOnlyExitOrderAsync(
            harness.DbContext,
            bot,
            "SOLUSDT",
            price: 100m,
            createdAtUtc: harness.NowUtc.AddMinutes(-10),
            side: ExecutionOrderSide.Buy);
        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(
            bot.TradingStrategyId,
            bot.TradingStrategyVersionId,
            "SOLUSDT",
            "1m",
            harness.NowUtc,
            direction: StrategyTradeDirection.Short,
            aiEvaluation: CreateShadowAiEvaluation()));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);
        var shadowDecision = await harness.DbContext.AiShadowDecisions.SingleAsync();

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("ShortEntryHysteresisActive", attempt.BlockerCode);
        Assert.Equal("NoSubmit", shadowDecision.FinalAction);
        Assert.False(shadowDecision.HypotheticalSubmitAllowed);
        Assert.Equal("ShortEntryHysteresisActive", shadowDecision.HypotheticalBlockReason);
        Assert.Equal("ShortEntryHysteresisActive", shadowDecision.NoSubmitReason);
        Assert.Equal(ShadowLinearAiSignalProviderAdapter.ProviderNameValue, shadowDecision.AiProviderName);
        Assert.Equal("shadow-linear-v1", shadowDecision.AiProviderModel);
        Assert.Equal(0.25m, shadowDecision.AiAdvisoryScore);
        Assert.Contains("CompressionBreakoutSetupDetected +0.25", shadowDecision.AiContributionSummary, StringComparison.Ordinal);
        Assert.Equal(ExecutionEnvironment.Demo, shadowDecision.TradingMode);
        Assert.False(await harness.DbContext.ExecutionOrders.AnyAsync(entity => entity.StrategySignalId == attempt.StrategySignalId));
        Assert.Null(harness.ExecutionGate.LastRequest);
        Assert.Null(harness.UserExecutionOverrideGuard.LastRequest);
        Assert.Null(harness.ExecutionEngine.LastCommand);
    }

    [Fact]
    public async Task RunOnceAsync_PersistsShadowOnlyAiDecision_WhenShadowModeActiveAndHandoffWouldProceed()
    {
        await using var harness = CreateHarness(
            new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero),
            new BotExecutionPilotOptions
            {
                SignalEvaluationMode = ExecutionEnvironment.Live,
                ExecutionDispatchMode = ExecutionEnvironment.Demo,
                PrimeHistoricalCandleCount = 34
            },
            resolvedTradingMode: ExecutionEnvironment.Demo,
            aiSignalOptions: CreateShadowAiOptions());
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-shadow-only", "BTCUSDT", "pilot-shadow-only");
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "BTCUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "BTCUSDT", rank: 1, score: 10_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("BTCUSDT", "BTC", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(
            bot.TradingStrategyId,
            bot.TradingStrategyVersionId,
            "BTCUSDT",
            "1m",
            harness.NowUtc,
            aiEvaluation: CreateShadowAiEvaluation()));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);
        var shadowDecision = await harness.DbContext.AiShadowDecisions.SingleAsync();

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("ShadowModeActive", attempt.BlockerCode);
        Assert.Equal("ShadowOnly", shadowDecision.FinalAction);
        Assert.True(shadowDecision.HypotheticalSubmitAllowed);
        Assert.Null(shadowDecision.HypotheticalBlockReason);
        Assert.Equal("ShadowModeActive", shadowDecision.NoSubmitReason);
        Assert.Equal(ShadowLinearAiSignalProviderAdapter.ProviderNameValue, shadowDecision.AiProviderName);
        Assert.Equal(0.25m, shadowDecision.AiAdvisoryScore);
        Assert.Equal(ExecutionEnvironment.Demo, shadowDecision.TradingMode);
        Assert.NotNull(harness.ExecutionGate.LastRequest);
        Assert.NotNull(harness.UserExecutionOverrideGuard.LastRequest);
        Assert.Null(harness.ExecutionEngine.LastCommand);
        Assert.Empty(harness.DbContext.ExecutionOrders);
    }

    [Fact]
    public async Task RunOnceAsync_SkipsCooldownBlockedSymbolAndPreparesNextCandidate_WithoutCrossSymbolLeak()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var solBot = await SeedBotGraphAsync(harness.DbContext, "user-sol", "SOLUSDT", "pilot-sol");
        var btcBot = await SeedBotGraphAsync(harness.DbContext, "user-btc", "BTCUSDT", "pilot-btc");
        SeedScanCycle(harness.DbContext, scanCycleId);
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 9_000m);
        SeedCandidate(harness.DbContext, scanCycleId, "BTCUSDT", rank: 2, score: 8_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.MarketDataService.SetMetadata("BTCUSDT", "BTC", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(solBot.TradingStrategyId, solBot.TradingStrategyVersionId, "SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(btcBot.TradingStrategyId, btcBot.TradingStrategyVersionId, "BTCUSDT", "1m", harness.NowUtc));
        harness.UserExecutionOverrideGuard.BlockSymbol("SOLUSDT", "UserExecutionSymbolCooldownActive", "Execution blocked because the symbol cooldown is still active.");

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        var attempts = await harness.DbContext.MarketScannerHandoffAttempts.OrderBy(entity => entity.SelectedSymbol).ToListAsync();
        var btcAttempt = Assert.Single(attempts, entity => entity.SelectedSymbol == "BTCUSDT");
        var solAttempt = Assert.Single(attempts, entity => entity.SelectedSymbol == "SOLUSDT");
        Assert.Equal(attempt.Id, btcAttempt.Id);
        Assert.Equal("Prepared", btcAttempt.ExecutionRequestStatus);
        Assert.Null(btcAttempt.BlockerCode);
        Assert.Equal("Blocked", solAttempt.ExecutionRequestStatus);
        Assert.Equal("UserExecutionSymbolCooldownActive", solAttempt.BlockerCode);
        Assert.Equal(new[] { "SOLUSDT", "BTCUSDT" }, harness.UserExecutionOverrideGuard.RequestedSymbols);
    }

    [Fact]
    public async Task RunOnceAsync_SkipsLegacyDirtyMarketScoreCandidate_AndPreparesNextCleanCandidate()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var btcBot = await SeedBotGraphAsync(harness.DbContext, "user-btc", "BTCUSDT", "pilot-btc");
        var ethBot = await SeedBotGraphAsync(harness.DbContext, "user-eth", "ETHUSDT", "pilot-eth");
        SeedScanCycle(harness.DbContext, scanCycleId, eligibleCandidateCount: 2, bestCandidateSymbol: "BTCUSDT", bestCandidateScore: 95m);
        harness.DbContext.MarketScannerCandidates.Add(new MarketScannerCandidate
        {
            Id = Guid.NewGuid(),
            ScanCycleId = scanCycleId,
            Symbol = "BTCUSDT",
            UniverseSource = "unit-test",
            ObservedAtUtc = harness.NowUtc,
            LastCandleAtUtc = harness.NowUtc,
            LastPrice = 100m,
            QuoteVolume24h = 123456m,
            MarketScore = 123456m,
            StrategyScore = 91,
            IsEligible = true,
            Score = 95m,
            Rank = 1,
            IsTopCandidate = true
        });
        harness.DbContext.MarketScannerCandidates.Add(new MarketScannerCandidate
        {
            Id = Guid.NewGuid(),
            ScanCycleId = scanCycleId,
            Symbol = "ETHUSDT",
            UniverseSource = "unit-test",
            ObservedAtUtc = harness.NowUtc,
            LastCandleAtUtc = harness.NowUtc,
            LastPrice = 90m,
            QuoteVolume24h = 100000m,
            MarketScore = 100m,
            StrategyScore = 88,
            IsEligible = true,
            Score = 80m,
            Rank = 2,
            IsTopCandidate = true
        });
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("BTCUSDT", "BTC", "USDT");
        harness.MarketDataService.SetMetadata("ETHUSDT", "ETH", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", harness.NowUtc));
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("ETHUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(btcBot.TradingStrategyId, btcBot.TradingStrategyVersionId, "BTCUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(ethBot.TradingStrategyId, ethBot.TradingStrategyVersionId, "ETHUSDT", "1m", harness.NowUtc));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("ETHUSDT", attempt.SelectedSymbol);
        Assert.Equal("Prepared", attempt.ExecutionRequestStatus);
        Assert.Equal("ETHUSDT", harness.ExecutionGate.LastRequest?.Symbol);
        Assert.Equal("ETHUSDT", harness.UserExecutionOverrideGuard.LastRequest?.Symbol);
        Assert.Equal("ETHUSDT", harness.StrategySignalService.LastRequest?.EvaluationContext.IndicatorSnapshot.Symbol);
    }

    [Fact]
    public async Task RunOnceAsync_PersistsStrategyVetoReason_WhenNoActionableSignalExists()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-btc", "BTCUSDT", "pilot-btc");
        SeedScanCycle(harness.DbContext, scanCycleId);
        SeedCandidate(harness.DbContext, scanCycleId, "BTCUSDT", rank: 1, score: 9_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("BTCUSDT", "BTC", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetVeto(CreateVeto(bot.TradingStrategyId, bot.TradingStrategyVersionId, "BTCUSDT", "1m", harness.NowUtc, RiskVetoReasonCode.ExposureLimitBreached, "Exposure limit breached by strategy risk check."));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("BTCUSDT", attempt.SelectedSymbol);
        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("StrategyVetoed", attempt.BlockerCode);
        Assert.Equal("Exposure limit breached by strategy risk check.", attempt.BlockerDetail);
        Assert.Equal("Vetoed", attempt.StrategyDecisionOutcome);
        Assert.Equal("ExposureLimitBreached", attempt.StrategyVetoReasonCode);
    }

    [Fact]
    public async Task RunOnceAsync_PersistsExecutionGateBlocker_ForSelectedSymbolOnly_WhenLatencyGuardRejects()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-btc", "BTCUSDT", "pilot-btc");
        SeedScanCycle(harness.DbContext, scanCycleId);
        SeedCandidate(harness.DbContext, scanCycleId, "BTCUSDT", rank: 1, score: 9_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("BTCUSDT", "BTC", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "BTCUSDT", "1m", harness.NowUtc));
        harness.ExecutionGate.BlockSymbol("BTCUSDT", ExecutionGateBlockedReason.StaleMarketData, "Execution blocked because market data is stale. LatencyReason=MarketDataLatencyBreached; Symbol=BTCUSDT; Timeframe=1m");

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("StaleMarketData", attempt.BlockerCode);
        Assert.Contains("Execution blocked because market data is stale.", attempt.BlockerDetail, StringComparison.Ordinal);
        Assert.Contains("LatencyReason=MarketDataLatencyBreached", attempt.BlockerDetail, StringComparison.Ordinal);
        Assert.Equal("Persisted", attempt.StrategyDecisionOutcome);
        Assert.Contains("ExecutionGate=StaleMarketData; Symbol=BTCUSDT; Timeframe=1m", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Equal("BTCUSDT", harness.ExecutionGate.LastRequest?.Symbol);
    }

    [Fact]
    public async Task RunOnceAsync_PersistsContinuityGapBlocker_DistinctFromStaleMarketData()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-btc", "BTCUSDT", "pilot-btc");
        SeedScanCycle(harness.DbContext, scanCycleId);
        SeedCandidate(harness.DbContext, scanCycleId, "BTCUSDT", rank: 1, score: 9_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("BTCUSDT", "BTC", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "BTCUSDT", "1m", harness.NowUtc));
        harness.ExecutionGate.BlockSymbol("BTCUSDT", ExecutionGateBlockedReason.ContinuityGap, "Execution blocked because the candle continuity guard is active. LatencyReason=CandleDataGapDetected; Symbol=BTCUSDT; Timeframe=1m; LastCandleAtUtc=2026-04-03T11:59:00.0000000Z; DataAgeMs=60000; ContinuityGapCount=2");

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("ContinuityGap", attempt.BlockerCode);
        Assert.Contains("Execution blocked because the candle continuity guard is active.", attempt.BlockerDetail, StringComparison.Ordinal);
        Assert.Contains("LatencyReason=CandleDataGapDetected", attempt.BlockerDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("StaleMarketData", attempt.BlockerCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnceAsync_PersistsDuplicateSignalSuppressed_WhenStrategyReportsDuplicateSuppression()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        _ = await SeedBotGraphAsync(harness.DbContext, "user-btc", "BTCUSDT", "pilot-btc");
        SeedScanCycle(harness.DbContext, scanCycleId);
        SeedCandidate(harness.DbContext, scanCycleId, "BTCUSDT", rank: 1, score: 9_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("BTCUSDT", "BTC", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetDuplicateSuppressed("BTCUSDT", "1m", 1);

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("DuplicateSignalSuppressed", attempt.BlockerCode);
        Assert.Equal("Scanner handoff skipped execution request creation because the strategy signal was duplicate-suppressed.", attempt.BlockerDetail);
        Assert.Equal("SuppressedDuplicate", attempt.StrategyDecisionOutcome);
        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
    }

    [Fact]
    public async Task RunOnceAsync_ReusesPersistedDuplicateSignal_WhenNoPreparedHandoffOrOrderExists()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-sol", "SOLUSDT", "pilot-sol");
        var existingSignal = CreateEntrySignal(
            bot.TradingStrategyId,
            bot.TradingStrategyVersionId,
            "SOLUSDT",
            "1m",
            harness.NowUtc,
            ExecutionEnvironment.Live);
        SeedPersistedSignal(harness.DbContext, bot.OwnerUserId, existingSignal);
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 9_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetDuplicateSuppressed("SOLUSDT", "1m", 1);

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Prepared", attempt.ExecutionRequestStatus);
        Assert.Null(attempt.BlockerCode);
        Assert.Equal("Persisted", attempt.StrategyDecisionOutcome);
        Assert.Equal(existingSignal.StrategySignalId, attempt.StrategySignalId);
        Assert.Equal("SOLUSDT", harness.ExecutionGate.LastRequest?.Symbol);
        Assert.Equal("SOLUSDT", harness.UserExecutionOverrideGuard.LastRequest?.Symbol);
    }

    [Fact]
    public async Task RunOnceAsync_BlocksDuplicateExecutionRequest_WhenPersistedSignalAlreadyHasPreparedHandoff()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-sol", "SOLUSDT", "pilot-sol");
        var existingSignal = CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "SOLUSDT", "1m", harness.NowUtc);
        SeedPersistedSignal(harness.DbContext, bot.OwnerUserId, existingSignal);
        harness.DbContext.MarketScannerHandoffAttempts.Add(new MarketScannerHandoffAttempt
        {
            Id = Guid.NewGuid(),
            ScanCycleId = Guid.NewGuid(),
            SelectedSymbol = "SOLUSDT",
            SelectedTimeframe = "1m",
            SelectedAtUtc = harness.NowUtc.AddSeconds(-15),
            SelectionReason = "unit-test",
            OwnerUserId = bot.OwnerUserId,
            BotId = bot.BotId,
            StrategyKey = "pilot-sol",
            TradingStrategyId = bot.TradingStrategyId,
            TradingStrategyVersionId = bot.TradingStrategyVersionId,
            StrategySignalId = existingSignal.StrategySignalId,
            StrategyDecisionOutcome = "Persisted",
            ExecutionRequestStatus = "Prepared",
            ExecutionEnvironment = ExecutionEnvironment.Live,
            CorrelationId = Guid.NewGuid().ToString("N"),
            CompletedAtUtc = harness.NowUtc.AddSeconds(-15)
        });
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 9_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetDuplicateSuppressed("SOLUSDT", "1m", 1);

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("DuplicateExecutionRequestSuppressed", attempt.BlockerCode);
        Assert.Equal(existingSignal.StrategySignalId, attempt.StrategySignalId);
        Assert.Null(harness.ExecutionGate.LastRequest);
        Assert.Null(harness.UserExecutionOverrideGuard.LastRequest);
    }

    [Fact]
    public async Task RunOnceAsync_ReusesExistingPreparedAttempt_WhenSameSignalReplaysSameExecutionIntent()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var firstScanCycleId = Guid.NewGuid();
        var replayScanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-sol", "SOLUSDT", "pilot-sol");
        var signal = CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "SOLUSDT", "1m", harness.NowUtc);

        SeedScanCycle(harness.DbContext, firstScanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, firstScanCycleId, "SOLUSDT", rank: 1, score: 9_000m);
        SeedScanCycle(harness.DbContext, replayScanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, replayScanCycleId, "SOLUSDT", rank: 1, score: 9_100m);
        await harness.DbContext.SaveChangesAsync();

        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(signal);

        var firstAttempt = await harness.Service.RunOnceAsync(firstScanCycleId);
        var replayAttempt = await harness.Service.RunOnceAsync(replayScanCycleId);

        var preparedAttempts = await harness.DbContext.MarketScannerHandoffAttempts
            .Where(entity =>
                entity.StrategySignalId == signal.StrategySignalId &&
                entity.ExecutionRequestStatus == "Prepared" &&
                entity.ExecutionEnvironment == ExecutionEnvironment.Live &&
                entity.ExecutionSide == ExecutionOrderSide.Buy)
            .ToListAsync();

        Assert.Equal(firstAttempt.Id, replayAttempt.Id);
        Assert.Single(preparedAttempts);
        Assert.Single(harness.DbContext.ExecutionOrders.Where(entity => entity.StrategySignalId == signal.StrategySignalId));
    }

    [Fact]
    public async Task RunOnceAsync_ReusesExistingPreparedAttempt_WhenSameSignalReplayOccursAfterNewerBlockedAttempt()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var firstScanCycleId = Guid.NewGuid();
        var replayScanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-sol", "SOLUSDT", "pilot-sol");
        var signal = CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "SOLUSDT", "1m", harness.NowUtc);

        SeedScanCycle(harness.DbContext, firstScanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, firstScanCycleId, "SOLUSDT", rank: 1, score: 9_000m);
        SeedScanCycle(harness.DbContext, replayScanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, replayScanCycleId, "SOLUSDT", rank: 1, score: 9_100m);
        await harness.DbContext.SaveChangesAsync();

        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(signal);

        var firstAttempt = await harness.Service.RunOnceAsync(firstScanCycleId);
        harness.DbContext.MarketScannerHandoffAttempts.Add(new MarketScannerHandoffAttempt
        {
            Id = Guid.NewGuid(),
            ScanCycleId = Guid.NewGuid(),
            SelectedSymbol = "SOLUSDT",
            SelectedTimeframe = "1m",
            SelectedAtUtc = harness.NowUtc.AddSeconds(30),
            SelectionReason = "unit-test-newer-blocked",
            OwnerUserId = bot.OwnerUserId,
            BotId = bot.BotId,
            StrategyKey = "pilot-sol",
            TradingStrategyId = bot.TradingStrategyId,
            TradingStrategyVersionId = bot.TradingStrategyVersionId,
            StrategySignalId = signal.StrategySignalId,
            StrategyDecisionOutcome = "Persisted",
            ExecutionRequestStatus = "Blocked",
            ExecutionSide = ExecutionOrderSide.Buy,
            ExecutionOrderType = ExecutionOrderType.Market,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            BlockerCode = "UserExecutionBotCooldownActive",
            BlockerDetail = "unit-test",
            CorrelationId = Guid.NewGuid().ToString("N"),
            CompletedAtUtc = harness.NowUtc.AddSeconds(30)
        });
        await harness.DbContext.SaveChangesAsync();

        var replayAttempt = await harness.Service.RunOnceAsync(replayScanCycleId);

        var preparedAttempts = await harness.DbContext.MarketScannerHandoffAttempts
            .Where(entity =>
                entity.StrategySignalId == signal.StrategySignalId &&
                entity.ExecutionRequestStatus == "Prepared" &&
                entity.ExecutionEnvironment == ExecutionEnvironment.Live &&
                entity.ExecutionSide == ExecutionOrderSide.Buy)
            .ToListAsync();

        Assert.Equal(firstAttempt.Id, replayAttempt.Id);
        Assert.Single(preparedAttempts);
        Assert.Single(harness.DbContext.ExecutionOrders.Where(entity => entity.StrategySignalId == signal.StrategySignalId));
    }

    [Fact]
    public async Task RunOnceAsync_BlocksDuplicateExecutionRequest_WhenSameSignalReplayAlreadyHasExecutionOrderIntent()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-sol", "SOLUSDT", "pilot-sol");
        var signal = CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "SOLUSDT", "1m", harness.NowUtc);

        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 9_000m);
        harness.DbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = bot.OwnerUserId,
            TradingStrategyId = bot.TradingStrategyId,
            TradingStrategyVersionId = bot.TradingStrategyVersionId,
            StrategySignalId = signal.StrategySignalId,
            SignalType = StrategySignalType.Entry,
            BotId = bot.BotId,
            ExchangeAccountId = bot.ExchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            StrategyKey = "pilot-sol",
            Symbol = "SOLUSDT",
            Timeframe = "1m",
            BaseAsset = "SOL",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 1m,
            Price = 100m,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            State = ExecutionOrderState.Submitted,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            RootCorrelationId = Guid.NewGuid().ToString("N"),
            LastStateChangedAtUtc = harness.NowUtc,
            CreatedDate = harness.NowUtc,
            UpdatedDate = harness.NowUtc
        });
        await harness.DbContext.SaveChangesAsync();

        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(signal);

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("DuplicateExecutionRequestSuppressed", attempt.BlockerCode);
        Assert.Equal(signal.StrategySignalId, attempt.StrategySignalId);
        Assert.Null(harness.ExecutionGate.LastRequest);
        Assert.Null(harness.UserExecutionOverrideGuard.LastRequest);
        Assert.Null(harness.ExecutionEngine.LastCommand);
        Assert.Single(harness.DbContext.ExecutionOrders.Where(entity => entity.StrategySignalId == signal.StrategySignalId));
    }

    [Fact]
    public async Task RunOnceAsync_AllowsNewPreparedAttempt_WhenSignalIdChanges()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var firstScanCycleId = Guid.NewGuid();
        var secondScanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-sol", "SOLUSDT", "pilot-sol");
        var firstSignal = CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "SOLUSDT", "1m", harness.NowUtc);
        var secondSignal = CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "SOLUSDT", "1m", harness.NowUtc.AddMinutes(1));

        SeedScanCycle(harness.DbContext, firstScanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, firstScanCycleId, "SOLUSDT", rank: 1, score: 9_000m);
        SeedScanCycle(harness.DbContext, secondScanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, secondScanCycleId, "SOLUSDT", rank: 1, score: 9_200m);
        await harness.DbContext.SaveChangesAsync();

        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(firstSignal);

        var firstAttempt = await harness.Service.RunOnceAsync(firstScanCycleId);

        harness.StrategySignalService.SetSignal(secondSignal);
        var secondAttempt = await harness.Service.RunOnceAsync(secondScanCycleId);

        var preparedAttempts = await harness.DbContext.MarketScannerHandoffAttempts
            .Where(entity =>
                entity.ExecutionRequestStatus == "Prepared" &&
                entity.ExecutionEnvironment == ExecutionEnvironment.Live &&
                entity.SelectedSymbol == "SOLUSDT")
            .OrderBy(entity => entity.CreatedDate)
            .ToListAsync();

        Assert.NotEqual(firstAttempt.Id, secondAttempt.Id);
        Assert.Equal(firstSignal.StrategySignalId, firstAttempt.StrategySignalId);
        Assert.Equal(secondSignal.StrategySignalId, secondAttempt.StrategySignalId);
        Assert.Equal(2, preparedAttempts.Count);
        Assert.Equal(2, harness.DbContext.ExecutionOrders.Count(entity => entity.Symbol == "SOLUSDT"));
    }

    [Fact]
    public async Task RunOnceAsync_ReusesBlockedAttempt_WhenSameSignalReplaysSameDuplicateExecutionBlocker_EvenIfNewerPreparedAttemptExists()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-sol", "SOLUSDT", "pilot-sol");
        var existingSignal = CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "SOLUSDT", "1m", harness.NowUtc);
        SeedPersistedSignal(harness.DbContext, bot.OwnerUserId, existingSignal);
        harness.DbContext.MarketScannerHandoffAttempts.Add(new MarketScannerHandoffAttempt
        {
            Id = Guid.NewGuid(),
            ScanCycleId = Guid.NewGuid(),
            SelectedSymbol = "SOLUSDT",
            SelectedTimeframe = "1m",
            SelectedAtUtc = harness.NowUtc.AddSeconds(-30),
            SelectionReason = "unit-test",
            OwnerUserId = bot.OwnerUserId,
            BotId = bot.BotId,
            StrategyKey = "pilot-sol",
            TradingStrategyId = bot.TradingStrategyId,
            TradingStrategyVersionId = bot.TradingStrategyVersionId,
            StrategySignalId = existingSignal.StrategySignalId,
            StrategyDecisionOutcome = "Persisted",
            ExecutionRequestStatus = "Prepared",
            ExecutionEnvironment = ExecutionEnvironment.Live,
            CorrelationId = Guid.NewGuid().ToString("N"),
            CompletedAtUtc = harness.NowUtc.AddSeconds(-30)
        });
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 9_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetDuplicateSuppressed("SOLUSDT", "1m", 1);

        var firstAttempt = await harness.Service.RunOnceAsync(scanCycleId);
        harness.DbContext.MarketScannerHandoffAttempts.Add(new MarketScannerHandoffAttempt
        {
            Id = Guid.NewGuid(),
            ScanCycleId = Guid.NewGuid(),
            SelectedSymbol = "SOLUSDT",
            SelectedTimeframe = "1m",
            SelectedAtUtc = harness.NowUtc.AddSeconds(30),
            SelectionReason = "unit-test-replay-prepared",
            OwnerUserId = bot.OwnerUserId,
            BotId = bot.BotId,
            StrategyKey = "pilot-sol",
            TradingStrategyId = bot.TradingStrategyId,
            TradingStrategyVersionId = bot.TradingStrategyVersionId,
            StrategySignalId = existingSignal.StrategySignalId,
            StrategyDecisionOutcome = "Persisted",
            ExecutionRequestStatus = "Prepared",
            ExecutionEnvironment = ExecutionEnvironment.Live,
            CorrelationId = Guid.NewGuid().ToString("N"),
            CompletedAtUtc = harness.NowUtc.AddSeconds(30)
        });
        await harness.DbContext.SaveChangesAsync();

        var replayAttempt = await harness.Service.RunOnceAsync(scanCycleId);

        var blockedAttempts = await harness.DbContext.MarketScannerHandoffAttempts
            .Where(entity =>
                entity.StrategySignalId == existingSignal.StrategySignalId &&
                entity.BlockerCode == "DuplicateExecutionRequestSuppressed")
            .ToListAsync();

        Assert.Equal(firstAttempt.Id, replayAttempt.Id);
        Assert.Single(blockedAttempts);
    }

    [Fact]
    public async Task RunOnceAsync_PersistsNewBlockedAttempt_WhenSameSignalTransitionsToDifferentBlocker()
    {
        await using var harness = CreateHarness(
            new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero),
            new BotExecutionPilotOptions
            {
                SignalEvaluationMode = ExecutionEnvironment.Live,
                PrimeHistoricalCandleCount = 34,
                EnableEntryHysteresis = true,
                EntryHysteresisCooldownMinutes = 15,
                EntryHysteresisReentryBufferPercentage = 0m,
                LongEntryHysteresisCooldownMinutes = 15,
                LongEntryHysteresisReentryBufferPercentage = 0.20m
            });
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-transition", "BTCUSDT", "pilot-transition");
        var signal = CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "BTCUSDT", "1m", harness.NowUtc);
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "BTCUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "BTCUSDT", rank: 1, score: 10_000m);
        await SeedExchangePositionAsync(harness.DbContext, bot, "BTCUSDT", quantity: 0.020m, entryPrice: 100m, positionSide: "BOTH", observedAtUtc: harness.NowUtc);
        harness.MarketDataService.SetMetadata("BTCUSDT", "BTC", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetSignal(signal);

        var firstAttempt = await harness.Service.RunOnceAsync(scanCycleId);

        harness.DbContext.ExchangePositions.RemoveRange(harness.DbContext.ExchangePositions);
        await harness.DbContext.SaveChangesAsync();
        await SeedFilledReduceOnlyExitOrderAsync(
            harness.DbContext,
            bot,
            "BTCUSDT",
            price: 100m,
            createdAtUtc: harness.NowUtc.AddMinutes(-10));
        await SeedExchangeAccountSyncStateAsync(
            harness.DbContext,
            bot.OwnerUserId,
            bot.ExchangeAccountId,
            harness.NowUtc);

        var secondAttempt = await harness.Service.RunOnceAsync(scanCycleId);

        var blockedAttempts = await harness.DbContext.MarketScannerHandoffAttempts
            .Where(entity => entity.StrategySignalId == signal.StrategySignalId && entity.ExecutionRequestStatus == "Blocked")
            .OrderBy(entity => entity.CreatedDate)
            .ToListAsync();

        Assert.NotEqual(firstAttempt.Id, secondAttempt.Id);
        Assert.Equal(new[] { "SameDirectionLongEntrySuppressed", "LongEntryHysteresisActive" }, blockedAttempts.Select(entity => entity.BlockerCode).ToArray());
    }

    [Fact]
    public async Task RunOnceAsync_PersistsNewBlockedAttempt_WhenNewSignalHitsSameBlocker()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        var nextScanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-new-signal", "BTCUSDT", "pilot-new-signal");
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "BTCUSDT");
        SeedScanCycle(harness.DbContext, nextScanCycleId, bestCandidateSymbol: "BTCUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "BTCUSDT", rank: 1, score: 10_000m);
        SeedCandidate(harness.DbContext, nextScanCycleId, "BTCUSDT", rank: 1, score: 10_000m);
        await SeedExchangePositionAsync(harness.DbContext, bot, "BTCUSDT", quantity: 0.020m, entryPrice: 100m, positionSide: "BOTH", observedAtUtc: harness.NowUtc);
        harness.MarketDataService.SetMetadata("BTCUSDT", "BTC", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", harness.NowUtc));
        var firstSignal = CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "BTCUSDT", "1m", harness.NowUtc);
        harness.StrategySignalService.SetSignal(firstSignal);

        var firstAttempt = await harness.Service.RunOnceAsync(scanCycleId);

        var nextSignalTimeUtc = harness.NowUtc.AddMinutes(1);
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", nextSignalTimeUtc));
        var secondSignal = CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "BTCUSDT", "1m", nextSignalTimeUtc);
        harness.StrategySignalService.SetSignal(secondSignal);

        var secondAttempt = await harness.Service.RunOnceAsync(nextScanCycleId);

        var blockedAttempts = await harness.DbContext.MarketScannerHandoffAttempts
            .Where(entity => entity.BlockerCode == "SameDirectionLongEntrySuppressed")
            .OrderBy(entity => entity.CreatedDate)
            .ToListAsync();

        Assert.NotEqual(firstAttempt.Id, secondAttempt.Id);
        Assert.Equal(2, blockedAttempts.Count);
        Assert.Equal(new[] { firstSignal.StrategySignalId, secondSignal.StrategySignalId }, blockedAttempts.Select(entity => entity.StrategySignalId!.Value).ToArray());
    }

    [Fact]
    public async Task RunOnceAsync_DoesNotTreatLiveExecutionRequestAsDuplicate_WhenDemoHandoffUsesSameSignal()
    {
        await using var harness = CreateHarness(
            new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero),
            new BotExecutionPilotOptions
            {
                SignalEvaluationMode = ExecutionEnvironment.Live,
                ExecutionDispatchMode = ExecutionEnvironment.Live,
                PrimeHistoricalCandleCount = 34
            },
            ExecutionEnvironment.Demo);
        var scanCycleId = Guid.NewGuid();
        var bot = await SeedBotGraphAsync(harness.DbContext, "user-sol", "SOLUSDT", "pilot-sol");
        var existingSignal = CreateEntrySignal(
            bot.TradingStrategyId,
            bot.TradingStrategyVersionId,
            "SOLUSDT",
            "1m",
            harness.NowUtc,
            ExecutionEnvironment.Demo);
        SeedPersistedSignal(harness.DbContext, bot.OwnerUserId, existingSignal);
        harness.DbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = bot.OwnerUserId,
            TradingStrategyId = bot.TradingStrategyId,
            TradingStrategyVersionId = bot.TradingStrategyVersionId,
            StrategySignalId = existingSignal.StrategySignalId,
            SignalType = StrategySignalType.Entry,
            BotId = bot.BotId,
            ExchangeAccountId = bot.ExchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            StrategyKey = "pilot-sol",
            Symbol = "SOLUSDT",
            Timeframe = "1m",
            BaseAsset = "SOL",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 1m,
            Price = 100m,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            State = ExecutionOrderState.Submitted,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            RootCorrelationId = Guid.NewGuid().ToString("N"),
            LastStateChangedAtUtc = harness.NowUtc,
            CreatedDate = harness.NowUtc,
            UpdatedDate = harness.NowUtc
        });
        SeedScanCycle(harness.DbContext, scanCycleId, bestCandidateSymbol: "SOLUSDT");
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: 1, score: 9_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("SOLUSDT", "SOL", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", harness.NowUtc));
        harness.StrategySignalService.SetDuplicateSuppressed("SOLUSDT", "1m", 1);

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Prepared", attempt.ExecutionRequestStatus);
        Assert.Null(attempt.BlockerCode);
        Assert.Equal(existingSignal.StrategySignalId, attempt.StrategySignalId);
        Assert.Equal(ExecutionEnvironment.Demo, attempt.ExecutionEnvironment);
    }

    [Fact]
    public async Task RunOnceAsync_PersistsNoEligibleCandidateBlocker_WhenLatestCycleHasNoEligibleCandidate()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        SeedScanCycle(harness.DbContext, scanCycleId, eligibleCandidateCount: 0, bestCandidateSymbol: null, bestCandidateScore: null);
        SeedCandidate(harness.DbContext, scanCycleId, "DOGEUSDT", rank: null, score: 0m, isEligible: false, rejectionReason: "LowQuoteVolume");
        await harness.DbContext.SaveChangesAsync();

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("NoEligibleCandidate", attempt.BlockerCode);
        Assert.Equal("No eligible candidate available.", attempt.SelectionReason);
        Assert.Equal("NoEligibleCandidate: Scanner handoff did not find an eligible candidate in the latest scan cycle.", attempt.BlockerSummary);
        Assert.Equal("CandidateSelection=None", attempt.GuardSummary);
        Assert.Null(attempt.SelectedSymbol);
    }

    [Fact]
    public async Task RunOnceAsync_DoesNotPersistAiShadowDecision_ForNoEligibleCandidate()
    {
        await using var harness = CreateHarness(
            new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero),
            aiSignalOptions: CreateShadowAiOptions());
        var scanCycleId = Guid.NewGuid();
        SeedScanCycle(harness.DbContext, scanCycleId, eligibleCandidateCount: 0, bestCandidateSymbol: null, bestCandidateScore: null);
        SeedCandidate(harness.DbContext, scanCycleId, "DOGEUSDT", rank: null, score: 0m, isEligible: false, rejectionReason: "LowQuoteVolume");
        await harness.DbContext.SaveChangesAsync();

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("NoEligibleCandidate", attempt.BlockerCode);
        Assert.Empty(await harness.DbContext.AiShadowDecisions.ToListAsync());
        Assert.Empty(await harness.DbContext.AiShadowDecisionOutcomes.ToListAsync());
    }

    [Fact]
    public async Task RunOnceAsync_ClosesMatureAiShadowOutcomeCoverageBacklog_WithoutNewShadowCapture()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        await using var harness = CreateHarness(nowUtc, aiSignalOptions: CreateShadowAiOptions());
        var ownerUserId = "user-coverage-backlog";
        var botGraph = await SeedBotGraphAsync(harness.DbContext, ownerUserId, "SOLUSDT", "coverage-backlog");
        var decisionId = Guid.NewGuid();
        var evaluatedAtUtc = nowUtc.UtcDateTime.AddMinutes(-12);

        harness.DbContext.HistoricalMarketCandles.AddRange(
            new HistoricalMarketCandle
            {
                Id = Guid.NewGuid(),
                Symbol = "SOLUSDT",
                Interval = "1m",
                OpenTimeUtc = evaluatedAtUtc.AddMinutes(-1),
                CloseTimeUtc = evaluatedAtUtc,
                OpenPrice = 100m,
                HighPrice = 100.5m,
                LowPrice = 99.5m,
                ClosePrice = 100m,
                Volume = 1000m,
                ReceivedAtUtc = evaluatedAtUtc,
                Source = "unit-test"
            },
            new HistoricalMarketCandle
            {
                Id = Guid.NewGuid(),
                Symbol = "SOLUSDT",
                Interval = "1m",
                OpenTimeUtc = evaluatedAtUtc,
                CloseTimeUtc = evaluatedAtUtc.AddMinutes(1),
                OpenPrice = 101m,
                HighPrice = 101.5m,
                LowPrice = 100.5m,
                ClosePrice = 101m,
                Volume = 1010m,
                ReceivedAtUtc = evaluatedAtUtc.AddMinutes(1),
                Source = "unit-test"
            });
        harness.DbContext.AiShadowDecisions.Add(new AiShadowDecision
        {
            Id = decisionId,
            OwnerUserId = ownerUserId,
            BotId = botGraph.BotId,
            ExchangeAccountId = botGraph.ExchangeAccountId,
            TradingStrategyId = botGraph.TradingStrategyId,
            TradingStrategyVersionId = botGraph.TradingStrategyVersionId,
            CorrelationId = Guid.NewGuid().ToString("N"),
            StrategyKey = "coverage-backlog",
            Symbol = "SOLUSDT",
            Timeframe = "1m",
            EvaluatedAtUtc = evaluatedAtUtc,
            MarketDataTimestampUtc = evaluatedAtUtc,
            StrategyDirection = "Short",
            StrategyDecisionOutcome = "Persisted",
            AiDirection = "Short",
            AiConfidence = 0.81m,
            AiReasonSummary = "Backlog coverage test",
            AiProviderName = ShadowLinearAiSignalProviderAdapter.ProviderNameValue,
            AiProviderModel = "shadow-linear-v1",
            TradingMode = ExecutionEnvironment.Live,
            Plane = ExchangeDataPlane.Futures,
            FinalAction = "NoSubmit",
            HypotheticalSubmitAllowed = false,
            HypotheticalBlockReason = "EntryDirectionModeBlocked",
            NoSubmitReason = "EntryDirectionModeBlocked",
            AgreementState = "Agreement"
        });
        await harness.DbContext.SaveChangesAsync();

        var scanCycleId = Guid.NewGuid();
        SeedScanCycle(harness.DbContext, scanCycleId, eligibleCandidateCount: 0, bestCandidateSymbol: null, bestCandidateScore: null);

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);
        var outcome = await harness.DbContext.AiShadowDecisionOutcomes.SingleAsync();

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("NoEligibleCandidate", attempt.BlockerCode);
        Assert.Equal(decisionId, outcome.AiShadowDecisionId);
        Assert.Equal(AiShadowOutcomeState.Scored, outcome.OutcomeState);
        Assert.Equal(AiShadowFutureDataAvailability.Available, outcome.FutureDataAvailability);
        Assert.Single(await harness.DbContext.AiShadowDecisions.ToListAsync());
    }

    [Fact]
    public async Task RunOnceAsync_RunsDemoConsistencyForCandidateSymbolOwners_WhenNoEligibleCandidate()
    {
        await using var harness = CreateHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        _ = await SeedBotGraphAsync(harness.DbContext, "user-sol", "SOLUSDT", "pilot-sol");
        SeedScanCycle(harness.DbContext, scanCycleId, eligibleCandidateCount: 0, bestCandidateSymbol: null, bestCandidateScore: null);
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: null, score: 0m, isEligible: false, rejectionReason: "StrategyRiskVetoedRiskLatencySoft");
        await harness.DbContext.SaveChangesAsync();

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("NoEligibleCandidate", attempt.BlockerCode);
        Assert.Contains("user-sol", harness.DemoSessionService.CheckedOwnerUserIds);
        Assert.Null(harness.ExecutionGate.LastRequest);
    }

    [Fact]
    public async Task RunOnceAsync_SkipsDemoConsistency_WhenInternalDemoExecutionIsDisabled_AndNoEligibleCandidate()
    {
        await using var harness = CreateHarness(
            new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero),
            allowInternalDemoExecution: false);
        var scanCycleId = Guid.NewGuid();
        _ = await SeedBotGraphAsync(harness.DbContext, "user-sol", "SOLUSDT", "pilot-sol");
        SeedScanCycle(harness.DbContext, scanCycleId, eligibleCandidateCount: 0, bestCandidateSymbol: null, bestCandidateScore: null);
        SeedCandidate(harness.DbContext, scanCycleId, "SOLUSDT", rank: null, score: 0m, isEligible: false, rejectionReason: "StrategyNoSignalLongEntryRsiThreshold");
        await harness.DbContext.SaveChangesAsync();

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("NoEligibleCandidate", attempt.BlockerCode);
        Assert.Empty(harness.DemoSessionService.CheckedOwnerUserIds);
    }

    [Fact]
    public async Task RunOnceAsync_BlocksAndSkipsExecution_WhenAiEnabledWithoutFeatureSnapshot()
    {
        await using var harness = CreateAiEnabledHarness(new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero));
        var scanCycleId = Guid.NewGuid();
        _ = await SeedBotGraphAsync(harness.DbContext, "user-ai", "BTCUSDT", "pilot-ai", CreateAiOverlayDefinitionJson());
        SeedScanCycle(harness.DbContext, scanCycleId);
        SeedCandidate(harness.DbContext, scanCycleId, "BTCUSDT", rank: 1, score: 9_000m);
        await harness.DbContext.SaveChangesAsync();
        harness.MarketDataService.SetMetadata("BTCUSDT", "BTC", "USDT");
        harness.IndicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", harness.NowUtc));

        var attempt = await harness.Service.RunOnceAsync(scanCycleId);
        var decisionTrace = await harness.DbContext.DecisionTraces.SingleAsync();

        Assert.Equal("Blocked", attempt.ExecutionRequestStatus);
        Assert.Equal("NoActionableSignal", attempt.BlockerCode);
        Assert.Equal("NoSignalCandidate", attempt.StrategyDecisionOutcome);
        Assert.Null(harness.ExecutionGate.LastRequest);
        Assert.Null(harness.UserExecutionOverrideGuard.LastRequest);
        Assert.Empty(harness.DbContext.ExecutionOrders);
        Assert.Equal("SuppressedByAi", decisionTrace.DecisionOutcome);
        Assert.Equal("AiFeatureSnapshotUnavailable", decisionTrace.DecisionReasonCode);
        Assert.Contains("aiEvaluation", decisionTrace.SnapshotJson, StringComparison.OrdinalIgnoreCase);
    }

    private static AiEnabledTestHarness CreateAiEnabledHarness(DateTimeOffset nowUtc)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContextAccessor());
        var marketDataService = new FakeMarketDataService(nowUtc.UtcDateTime);
        var indicatorDataService = new FakeIndicatorDataService(nowUtc.UtcDateTime);
        var sharedSymbolRegistry = new FakeSharedSymbolRegistry();
        var circuitBreaker = new FakeDataLatencyCircuitBreaker(nowUtc.UtcDateTime);
        var executionGate = new FakeExecutionGate(nowUtc.UtcDateTime);
        var userExecutionOverrideGuard = new FakeUserExecutionOverrideGuard();
        var executionEngine = new FakeExecutionEngine(options, nowUtc.UtcDateTime);
        var timeProvider = new FixedTimeProvider(nowUtc);
        var aiOptions = new AiSignalOptions
        {
            Enabled = true,
            SelectedProvider = DeterministicStubAiSignalProviderAdapter.ProviderNameValue,
            MinimumConfidence = 0.70m
        };

        var services = new ServiceCollection();
        services.AddScoped<IDataScopeContextAccessor, TestDataScopeContextAccessor>();
        services.AddScoped(provider => new ApplicationDbContext(options, provider.GetRequiredService<IDataScopeContextAccessor>()));
        services.AddSingleton<ICorrelationContextAccessor, CorrelationContextAccessor>();
        services.AddScoped<IStrategySignalService>(provider =>
        {
            var scopedContext = provider.GetRequiredService<ApplicationDbContext>();
            var correlationContextAccessor = provider.GetRequiredService<ICorrelationContextAccessor>();
            return new StrategySignalService(
                scopedContext,
                new StrategyEvaluatorService(new StrategyRuleParser()),
                new RiskPolicyEvaluator(scopedContext, timeProvider, NullLogger<RiskPolicyEvaluator>.Instance),
                new TraceService(scopedContext, correlationContextAccessor, timeProvider),
                correlationContextAccessor,
                new AiSignalEvaluator(
                    [new ShadowLinearAiSignalProviderAdapter(), new DeterministicStubAiSignalProviderAdapter(), new OfflineAiSignalProviderAdapter(), new OpenAiSignalProviderAdapter(), new GeminiAiSignalProviderAdapter()],
                    Options.Create(aiOptions),
                    timeProvider,
                    NullLogger<AiSignalEvaluator>.Instance),
                Options.Create(aiOptions),
                timeProvider,
                NullLogger<StrategySignalService>.Instance);
        });
        services.AddSingleton<IExecutionGate>(executionGate);
        services.AddSingleton<IUserExecutionOverrideGuard>(userExecutionOverrideGuard);
        services.AddSingleton<IExecutionEngine>(executionEngine);
        services.AddSingleton<ITradingModeResolver>(new FakeTradingModeResolver(ExecutionEnvironment.Live));
        var serviceProvider = services.BuildServiceProvider();
        var service = new MarketScannerHandoffService(
            dbContext,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            marketDataService,
            indicatorDataService,
            sharedSymbolRegistry,
            circuitBreaker,
            Options.Create(new MarketScannerOptions { HandoffEnabled = true, AllowedQuoteAssets = ["USDT"] }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m" }),
            Options.Create(new BotExecutionPilotOptions { SignalEvaluationMode = ExecutionEnvironment.Live, PrimeHistoricalCandleCount = 34 }),
            timeProvider,
            NullLogger<MarketScannerHandoffService>.Instance);

        return new AiEnabledTestHarness(dbContext, service, serviceProvider, marketDataService, indicatorDataService, executionGate, userExecutionOverrideGuard, nowUtc.UtcDateTime);
    }

    private static string CreateAiOverlayDefinitionJson()
    {
        return
            """
            {
              "schemaVersion": 2,
              "metadata": {
                "templateKey": "handoff-ai-template",
                "templateName": "Handoff AI Template"
              },
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
                    "value": 34
                  }
                ]
              }
            }
            """;
    }
    private static TestHarness CreateHarness(
        DateTimeOffset nowUtc,
        BotExecutionPilotOptions? pilotOptions = null,
        ExecutionEnvironment resolvedTradingMode = ExecutionEnvironment.Live,
        bool allowInternalDemoExecution = true,
        FakeExchangeInfoClient? exchangeInfoClient = null,
        AiSignalOptions? aiSignalOptions = null,
        ILogger<MarketScannerHandoffService>? logger = null,
        bool registerAiShadowDecisionService = true,
        IAiShadowDecisionService? aiShadowDecisionService = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContextAccessor());
        var marketDataService = new FakeMarketDataService(nowUtc.UtcDateTime);
        var indicatorDataService = new FakeIndicatorDataService(nowUtc.UtcDateTime);
        var sharedSymbolRegistry = new FakeSharedSymbolRegistry();
        var circuitBreaker = new FakeDataLatencyCircuitBreaker(nowUtc.UtcDateTime);
        var strategySignalService = new FakeStrategySignalService();
        var executionGate = new FakeExecutionGate(nowUtc.UtcDateTime);
        var userExecutionOverrideGuard = new FakeUserExecutionOverrideGuard();
        var executionEngine = new FakeExecutionEngine(options, nowUtc.UtcDateTime);
        var demoSessionService = new FakeDemoSessionService(nowUtc.UtcDateTime);
        exchangeInfoClient ??= new FakeExchangeInfoClient(new Dictionary<string, SymbolMetadataSnapshot>(StringComparer.Ordinal));

        var services = new ServiceCollection();
        services.AddScoped<IDataScopeContextAccessor, TestDataScopeContextAccessor>();
        services.AddScoped(provider => new ApplicationDbContext(options, provider.GetRequiredService<IDataScopeContextAccessor>()));
        services.AddSingleton<IStrategySignalService>(strategySignalService);
        services.AddSingleton<IExecutionGate>(executionGate);
        services.AddSingleton<IUserExecutionOverrideGuard>(userExecutionOverrideGuard);
        services.AddSingleton<IExecutionEngine>(executionEngine);
        services.AddSingleton<IDemoSessionService>(demoSessionService);
        services.AddSingleton<ITradingModeResolver>(new FakeTradingModeResolver(resolvedTradingMode));
        if (registerAiShadowDecisionService)
        {
            services.AddScoped<IAiShadowDecisionService>(provider => new AiShadowDecisionService(
                provider.GetRequiredService<ApplicationDbContext>(),
                new FixedTimeProvider(nowUtc)));
        }
        var serviceProvider = services.BuildServiceProvider();
        var service = new MarketScannerHandoffService(
            dbContext,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            marketDataService,
            indicatorDataService,
            sharedSymbolRegistry,
            circuitBreaker,
            Options.Create(new MarketScannerOptions { HandoffEnabled = true, AllowedQuoteAssets = ["USDT"] }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m" }),
            Options.Create(pilotOptions ?? new BotExecutionPilotOptions { SignalEvaluationMode = ExecutionEnvironment.Live, PrimeHistoricalCandleCount = 34 }),
            new FixedTimeProvider(nowUtc),
            logger ?? NullLogger<MarketScannerHandoffService>.Instance,
            exchangeInfoClient,
            executionRuntimeOptions: Options.Create(new ExecutionRuntimeOptions
            {
                AllowInternalDemoExecution = allowInternalDemoExecution
            }),
            aiSignalOptions: Options.Create(aiSignalOptions ?? new AiSignalOptions()),
            aiShadowDecisionService: aiShadowDecisionService ?? (registerAiShadowDecisionService
                ? new AiShadowDecisionService(dbContext, new FixedTimeProvider(nowUtc))
                : null));

        return new TestHarness(dbContext, service, serviceProvider, marketDataService, indicatorDataService, strategySignalService, executionGate, userExecutionOverrideGuard, executionEngine, demoSessionService, nowUtc.UtcDateTime);
    }

    private static async Task<BotGraph> SeedBotGraphAsync(ApplicationDbContext dbContext, string ownerUserId, string symbol, string strategyKey, string definitionJson = "{}")
    {
        var tradingStrategyId = Guid.NewGuid();
        var tradingStrategyVersionId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();

        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = tradingStrategyId,
            OwnerUserId = ownerUserId,
            StrategyKey = strategyKey,
            DisplayName = strategyKey,
            PromotionState = StrategyPromotionState.LivePublished,
            PublishedMode = ExecutionEnvironment.Live,
            PublishedAtUtc = new DateTime(2026, 4, 3, 11, 59, 0, DateTimeKind.Utc)
        });
        dbContext.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = ownerUserId,
            ExchangeName = "Binance",
            DisplayName = $"{strategyKey}-exchange",
            IsReadOnly = false,
            CredentialStatus = ExchangeCredentialStatus.Active
        });
        dbContext.TradingStrategyVersions.Add(new TradingStrategyVersion
        {
            Id = tradingStrategyVersionId,
            OwnerUserId = ownerUserId,
            TradingStrategyId = tradingStrategyId,
            SchemaVersion = 1,
            VersionNumber = 1,
            Status = StrategyVersionStatus.Published,
            DefinitionJson = definitionJson,
            PublishedAtUtc = new DateTime(2026, 4, 3, 11, 59, 0, DateTimeKind.Utc)
        });
        dbContext.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = ownerUserId,
            Name = strategyKey,
            StrategyKey = strategyKey,
            Symbol = symbol,
            ExchangeAccountId = exchangeAccountId,
            IsEnabled = true
        });
        await dbContext.SaveChangesAsync();

        return new BotGraph(botId, ownerUserId, exchangeAccountId, tradingStrategyId, tradingStrategyVersionId);
    }

    private static void SeedScanCycle(ApplicationDbContext dbContext, Guid scanCycleId, int eligibleCandidateCount = 1, string? bestCandidateSymbol = "BTCUSDT", decimal? bestCandidateScore = 10_000m)
    {
        dbContext.MarketScannerCycles.Add(new MarketScannerCycle
        {
            Id = scanCycleId,
            StartedAtUtc = new DateTime(2026, 4, 3, 11, 59, 58, DateTimeKind.Utc),
            CompletedAtUtc = new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc),
            UniverseSource = "unit-test",
            ScannedSymbolCount = 2,
            EligibleCandidateCount = eligibleCandidateCount,
            TopCandidateCount = 2,
            BestCandidateSymbol = bestCandidateSymbol,
            BestCandidateScore = bestCandidateScore,
            Summary = "unit-test"
        });
    }

    private static void SeedCandidate(ApplicationDbContext dbContext, Guid scanCycleId, string symbol, int? rank, decimal score, bool isEligible = true, string? rejectionReason = null, string? scoringSummary = null)
    {
        dbContext.MarketScannerCandidates.Add(new MarketScannerCandidate
        {
            Id = Guid.NewGuid(),
            ScanCycleId = scanCycleId,
            Symbol = symbol,
            UniverseSource = "unit-test",
            ObservedAtUtc = new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc),
            LastCandleAtUtc = new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc),
            LastPrice = 100m,
            QuoteVolume24h = 100_000m,
            IsEligible = isEligible,
            RejectionReason = rejectionReason,
            ScoringSummary = scoringSummary,
            Score = score,
            Rank = rank,
            IsTopCandidate = isEligible && rank is > 0
        });
    }

    private static StrategyIndicatorSnapshot CreateIndicatorSnapshot(string symbol, string timeframe, DateTime closeTimeUtc)
    {
        var openTimeUtc = closeTimeUtc.AddMinutes(-1);
        return new StrategyIndicatorSnapshot(
            symbol,
            timeframe,
            openTimeUtc,
            closeTimeUtc,
            closeTimeUtc,
            100,
            34,
            IndicatorDataState.Ready,
            DegradedModeReasonCode.None,
            new RelativeStrengthIndexSnapshot(14, true, 55m),
            new MovingAverageConvergenceDivergenceSnapshot(12, 26, 9, true, 1m, 0.8m, 0.2m),
            new BollingerBandsSnapshot(20, 2m, true, 100m, 110m, 90m, 3m),
            "unit-test");
    }

    private static StrategySignalSnapshot CreateEntrySignal(
        Guid tradingStrategyId,
        Guid tradingStrategyVersionId,
        string symbol,
        string timeframe,
        DateTime generatedAtUtc,
        ExecutionEnvironment environment = ExecutionEnvironment.Live,
        StrategyTradeDirection direction = StrategyTradeDirection.Long,
        AiSignalEvaluationResult? aiEvaluation = null)
    {
        var indicatorSnapshot = CreateIndicatorSnapshot(symbol, timeframe, generatedAtUtc);
        return new StrategySignalSnapshot(
            Guid.NewGuid(),
            tradingStrategyId,
            tradingStrategyVersionId,
            1,
            1,
            StrategySignalType.Entry,
            environment,
            symbol,
            timeframe,
            indicatorSnapshot.OpenTimeUtc,
            indicatorSnapshot.CloseTimeUtc,
            indicatorSnapshot.ReceivedAtUtc,
            generatedAtUtc,
            new StrategySignalExplainabilityPayload(
                1,
                tradingStrategyId,
                tradingStrategyVersionId,
                1,
                1,
                environment,
                indicatorSnapshot,
                new StrategyEvaluationResult(
                    true,
                    true,
                    false,
                    false,
                    true,
                    true,
                    null,
                    null,
                    null,
                    direction,
                    direction,
                    StrategyTradeDirection.Neutral),
                new StrategySignalConfidenceSnapshot(91, StrategySignalConfidenceBand.High, 3, 3, true, true, false, RiskVetoReasonCode.None, false, "Entry accepted.", AiEvaluation: aiEvaluation),
                new StrategySignalLogExplainabilitySnapshot("Entry", "Entry accepted", ["driver"], ["scanner"]),
                new StrategySignalDuplicateSuppressionSnapshot(true, false, $"fp-{symbol}")));
    }

    private static AiSignalOptions CreateShadowAiOptions()
    {
        return new AiSignalOptions
        {
            Enabled = true,
            ShadowModeEnabled = true,
            SelectedProvider = ShadowLinearAiSignalProviderAdapter.ProviderNameValue
        };
    }

    private static AiSignalEvaluationResult CreateShadowAiEvaluation()
    {
        return new AiSignalEvaluationResult(
            AiSignalDirection.Short,
            0.82m,
            "Compression breakout setup detected.",
            FeatureSnapshotId: null,
            ShadowLinearAiSignalProviderAdapter.ProviderNameValue,
            "shadow-linear-v1",
            7,
            IsFallback: false,
            FallbackReason: null,
            RawResponseCaptured: false,
            EvaluatedAtUtc: new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc),
            AdvisoryScore: 0.25m,
            Contributions:
            [
                new AiSignalContributionSnapshot("CompressionBreakoutSetupDetected", 0.25m, "Compression breakout setup detected.")
            ]);
    }

    private static void SeedPersistedSignal(ApplicationDbContext dbContext, string ownerUserId, StrategySignalSnapshot signal)
    {
        dbContext.TradingStrategySignals.Add(new TradingStrategySignal
        {
            Id = signal.StrategySignalId,
            OwnerUserId = ownerUserId,
            TradingStrategyId = signal.TradingStrategyId,
            TradingStrategyVersionId = signal.TradingStrategyVersionId,
            StrategyVersionNumber = signal.StrategyVersionNumber,
            StrategySchemaVersion = signal.StrategySchemaVersion,
            SignalType = signal.SignalType,
            ExecutionEnvironment = signal.Mode,
            Symbol = signal.Symbol,
            Timeframe = signal.Timeframe,
            IndicatorOpenTimeUtc = signal.IndicatorOpenTimeUtc,
            IndicatorCloseTimeUtc = signal.IndicatorCloseTimeUtc,
            IndicatorReceivedAtUtc = signal.IndicatorReceivedAtUtc,
            GeneratedAtUtc = signal.GeneratedAtUtc,
            ExplainabilitySchemaVersion = signal.ExplainabilityPayload.ExplainabilitySchemaVersion,
            IndicatorSnapshotJson = JsonSerializer.Serialize(signal.ExplainabilityPayload.IndicatorSnapshot, StrategySignalSerializerOptions),
            RuleResultSnapshotJson = JsonSerializer.Serialize(signal.ExplainabilityPayload.RuleResultSnapshot, StrategySignalSerializerOptions),
            RiskEvaluationJson = JsonSerializer.Serialize(signal.ExplainabilityPayload.ConfidenceSnapshot, StrategySignalSerializerOptions)
        });
    }

    private static async Task SeedExchangePositionAsync(
        ApplicationDbContext dbContext,
        BotGraph bot,
        string symbol,
        decimal quantity,
        decimal entryPrice,
        string positionSide,
        DateTime observedAtUtc)
    {
        dbContext.ExchangePositions.Add(new ExchangePosition
        {
            ExchangeAccountId = bot.ExchangeAccountId,
            OwnerUserId = bot.OwnerUserId,
            Plane = ExchangeDataPlane.Futures,
            Symbol = symbol,
            PositionSide = positionSide,
            Quantity = quantity,
            EntryPrice = entryPrice,
            BreakEvenPrice = entryPrice,
            UnrealizedProfit = 0m,
            MarginType = "isolated",
            IsolatedWallet = 10m,
            ExchangeUpdatedAtUtc = observedAtUtc,
            SyncedAtUtc = observedAtUtc
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedFilledReduceOnlyExitOrderAsync(
        ApplicationDbContext dbContext,
        BotGraph bot,
        string symbol,
        decimal price,
        DateTime createdAtUtc,
        ExecutionOrderSide side = ExecutionOrderSide.Sell)
    {
        dbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = bot.OwnerUserId,
            BotId = bot.BotId,
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Exit,
            StrategyKey = "scanner-handoff-test",
            Symbol = symbol,
            Timeframe = "1m",
            BaseAsset = symbol[..^4],
            QuoteAsset = "USDT",
            Side = side,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.002m,
            Price = price,
            ExchangeAccountId = bot.ExchangeAccountId,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            Plane = ExchangeDataPlane.Futures,
            ReduceOnly = true,
            SubmittedToBroker = true,
            State = ExecutionOrderState.Filled,
            FilledQuantity = 0.002m,
            AverageFillPrice = price,
            IdempotencyKey = $"seed-filled-exit-{Guid.NewGuid():N}",
            RootCorrelationId = "seed-filled-exit-order",
            ExternalOrderId = $"binance:{Guid.NewGuid():N}",
            SubmittedAtUtc = createdAtUtc,
            LastStateChangedAtUtc = createdAtUtc,
            CreatedDate = createdAtUtc,
            UpdatedDate = createdAtUtc
        });

        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedExchangeAccountSyncStateAsync(
        ApplicationDbContext dbContext,
        string ownerUserId,
        Guid exchangeAccountId,
        DateTime syncedAtUtc)
    {
        dbContext.ExchangeAccountSyncStates.Add(new ExchangeAccountSyncState
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            ExchangeAccountId = exchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            PrivateStreamConnectionState = ExchangePrivateStreamConnectionState.Connected,
            LastPositionSyncedAtUtc = syncedAtUtc,
            LastStateReconciledAtUtc = syncedAtUtc,
            DriftStatus = ExchangeStateDriftStatus.InSync,
            CreatedDate = syncedAtUtc,
            UpdatedDate = syncedAtUtc
        });

        await dbContext.SaveChangesAsync();
    }

    private static readonly JsonSerializerOptions StrategySignalSerializerOptions = CreateStrategySignalSerializerOptions();

    private static JsonSerializerOptions CreateStrategySignalSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        return options;
    }

    private static StrategySignalVetoSnapshot CreateVeto(Guid tradingStrategyId, Guid tradingStrategyVersionId, string symbol, string timeframe, DateTime evaluatedAtUtc, RiskVetoReasonCode reasonCode, string summary)
    {
        var indicatorSnapshot = CreateIndicatorSnapshot(symbol, timeframe, evaluatedAtUtc);
        return new StrategySignalVetoSnapshot(
            Guid.NewGuid(),
            tradingStrategyId,
            tradingStrategyVersionId,
            1,
            1,
            StrategySignalType.Entry,
            ExecutionEnvironment.Live,
            symbol,
            timeframe,
            indicatorSnapshot.OpenTimeUtc,
            indicatorSnapshot.CloseTimeUtc,
            indicatorSnapshot.ReceivedAtUtc,
            evaluatedAtUtc,
            new StrategySignalConfidenceSnapshot(12, StrategySignalConfidenceBand.Low, 0, 3, true, false, true, reasonCode, false, summary),
            new StrategySignalLogExplainabilitySnapshot("Veto", summary, ["risk"], ["scanner"]));
    }

    private sealed class FakeStrategySignalService : IStrategySignalService
    {
        private readonly Dictionary<string, StrategySignalSnapshot> signals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StrategySignalVetoSnapshot> vetoes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> duplicateCounts = new(StringComparer.Ordinal);

        public GenerateStrategySignalsRequest? LastRequest { get; private set; }

        public void SetSignal(StrategySignalSnapshot signal) => signals[$"{signal.Symbol}|{signal.Timeframe}"] = signal;

        public void SetVeto(StrategySignalVetoSnapshot veto) => vetoes[$"{veto.Symbol}|{veto.Timeframe}"] = veto;

        public void SetDuplicateSuppressed(string symbol, string timeframe, int duplicateCount) => duplicateCounts[$"{symbol}|{timeframe}"] = duplicateCount;

        public Task<StrategySignalGenerationResult> GenerateAsync(GenerateStrategySignalsRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            var key = $"{request.EvaluationContext.IndicatorSnapshot.Symbol}|{request.EvaluationContext.IndicatorSnapshot.Timeframe}";
            signals.TryGetValue(key, out var signal);
            vetoes.TryGetValue(key, out var veto);
            duplicateCounts.TryGetValue(key, out var duplicateCount);

            return Task.FromResult(new StrategySignalGenerationResult(
                new StrategyEvaluationResult(true, signal is not null, false, false, true, veto is null, null, null, null),
                signal is null ? [] : [signal],
                veto is null ? [] : [veto],
                duplicateCount));
        }

        public Task<StrategySignalSnapshot?> GetAsync(Guid strategySignalId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(signals.Values.SingleOrDefault(signal => signal.StrategySignalId == strategySignalId));
        }

        public Task<StrategySignalVetoSnapshot?> GetVetoAsync(Guid strategySignalVetoId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(vetoes.Values.SingleOrDefault(veto => veto.StrategySignalVetoId == strategySignalVetoId));
        }
    }

    private sealed class FakeExecutionGate(DateTime nowUtc) : IExecutionGate
    {
        private readonly Dictionary<string, (ExecutionGateBlockedReason Reason, string Message)> blockedSymbols = new(StringComparer.Ordinal);

        public ExecutionGateRequest? LastRequest { get; private set; }

        public void BlockSymbol(string symbol, ExecutionGateBlockedReason reason, string message) => blockedSymbols[symbol] = (reason, message);

        public Task<GlobalExecutionSwitchSnapshot> EnsureExecutionAllowedAsync(ExecutionGateRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            if (request.Symbol is not null && blockedSymbols.TryGetValue(request.Symbol, out var blocked))
            {
                throw new ExecutionGateRejectedException(blocked.Reason, request.Environment, blocked.Message);
            }

            return Task.FromResult(new GlobalExecutionSwitchSnapshot(TradeMasterSwitchState.Armed, false, true, nowUtc));
        }
    }

    private sealed class FakeUserExecutionOverrideGuard : IUserExecutionOverrideGuard
    {
        private readonly Dictionary<string, (string Code, string Message)> blockedSymbols = new(StringComparer.Ordinal);
        private readonly List<string> requestedSymbols = [];

        public UserExecutionOverrideEvaluationRequest? LastRequest { get; private set; }

        public IReadOnlyCollection<string> RequestedSymbols => requestedSymbols;

        public void BlockSymbol(string symbol, string code, string message) => blockedSymbols[symbol] = (code, message);

        public Task<UserExecutionOverrideEvaluationResult> EvaluateAsync(UserExecutionOverrideEvaluationRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            requestedSymbols.Add(request.Symbol);
            if (blockedSymbols.TryGetValue(request.Symbol, out var blocked))
            {
                return Task.FromResult(new UserExecutionOverrideEvaluationResult(true, blocked.Code, blocked.Message));
            }

            return Task.FromResult(new UserExecutionOverrideEvaluationResult(false, null, null));
        }
    }

    private sealed class FakeExecutionEngine(DbContextOptions<ApplicationDbContext> options, DateTime nowUtc) : IExecutionEngine
    {
        public ExecutionCommand? LastCommand { get; private set; }

        public async Task<ExecutionDispatchResult> DispatchAsync(ExecutionCommand command, CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            await using var dbContext = new ApplicationDbContext(options, new TestDataScopeContextAccessor());
            var order = new ExecutionOrder
            {
                Id = Guid.NewGuid(),
                OwnerUserId = command.OwnerUserId,
                TradingStrategyId = command.TradingStrategyId,
                TradingStrategyVersionId = command.TradingStrategyVersionId,
                StrategySignalId = command.StrategySignalId,
                SignalType = command.SignalType,
                BotId = command.BotId,
                ExchangeAccountId = command.ExchangeAccountId,
                Plane = command.Plane,
                StrategyKey = command.StrategyKey,
                Symbol = command.Symbol,
                Timeframe = command.Timeframe,
                BaseAsset = command.BaseAsset,
                QuoteAsset = command.QuoteAsset,
                Side = command.Side,
                OrderType = command.OrderType,
                Quantity = command.Quantity,
                Price = command.Price,
                ReduceOnly = command.ReduceOnly,
                ReplacesExecutionOrderId = command.ReplacesExecutionOrderId,
                ExecutionEnvironment = command.RequestedEnvironment
                    ?? (command.IsDemo == true ? ExecutionEnvironment.Demo : ExecutionEnvironment.Live),
                ExecutorKind = command.RequestedEnvironment switch
                {
                    ExecutionEnvironment.BinanceTestnet => ExecutionOrderExecutorKind.BinanceTestnet,
                    ExecutionEnvironment.Demo => ExecutionOrderExecutorKind.Virtual,
                    _ => command.IsDemo == true ? ExecutionOrderExecutorKind.Virtual : ExecutionOrderExecutorKind.Binance
                },
                State = ExecutionOrderState.Received,
                IdempotencyKey = command.IdempotencyKey ?? command.StrategySignalId.ToString("N"),
                RootCorrelationId = command.CorrelationId ?? Guid.NewGuid().ToString("N"),
                ParentCorrelationId = command.ParentCorrelationId,
                LastStateChangedAtUtc = nowUtc,
                CreatedDate = nowUtc,
                UpdatedDate = nowUtc
            };
            dbContext.ExecutionOrders.Add(order);
            await dbContext.SaveChangesAsync(cancellationToken);

            return new ExecutionDispatchResult(
                new ExecutionOrderSnapshot(
                    order.Id,
                    order.TradingStrategyId,
                    order.TradingStrategyVersionId,
                    order.StrategySignalId,
                    order.SignalType,
                    order.BotId,
                    order.ExchangeAccountId,
                    order.StrategyKey,
                    order.Symbol,
                    order.Timeframe,
                    order.BaseAsset,
                    order.QuoteAsset,
                    order.Side,
                    order.OrderType,
                    order.Quantity,
                    order.Price,
                    order.FilledQuantity,
                    order.AverageFillPrice,
                    order.LastFilledAtUtc,
                    order.StopLossPrice,
                    order.TakeProfitPrice,
                    order.ReduceOnly,
                    order.ReplacesExecutionOrderId,
                    order.ExecutionEnvironment,
                    order.ExecutorKind,
                    order.State,
                    order.IdempotencyKey,
                    order.RootCorrelationId,
                    order.ParentCorrelationId,
                    order.ExternalOrderId,
                    order.FailureCode,
                    order.FailureDetail,
                    order.RejectionStage,
                    order.SubmittedToBroker,
                    order.RetryEligible,
                    order.CooldownApplied,
                    order.DuplicateSuppressed,
                    false,
                    false,
                    null,
                    order.SubmittedAtUtc,
                    order.LastReconciledAtUtc,
                    order.ReconciliationStatus,
                    order.ReconciliationSummary,
                    order.LastDriftDetectedAtUtc,
                    order.LastStateChangedAtUtc,
                    Transitions: []),
                IsDuplicate: false);
        }
    }

    private sealed class FakeDemoSessionService(DateTime nowUtc) : IDemoSessionService
    {
        private readonly List<string> checkedOwnerUserIds = [];

        public IReadOnlyCollection<string> CheckedOwnerUserIds => checkedOwnerUserIds;

        public Task<DemoSessionSnapshot?> GetActiveSessionAsync(string ownerUserId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<DemoSessionSnapshot?>(CreateSnapshot());
        }

        public Task<DemoSessionSnapshot> EnsureActiveSessionAsync(string ownerUserId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CreateSnapshot());
        }

        public Task<DemoSessionSnapshot?> RunConsistencyCheckAsync(string ownerUserId, CancellationToken cancellationToken = default)
        {
            checkedOwnerUserIds.Add(ownerUserId);
            return Task.FromResult<DemoSessionSnapshot?>(CreateSnapshot());
        }

        public Task<DemoSessionSnapshot> ResetAsync(DemoSessionResetRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CreateSnapshot());
        }

        private DemoSessionSnapshot CreateSnapshot()
        {
            return new DemoSessionSnapshot(
                Guid.NewGuid(),
                1,
                "USDT",
                10000m,
                DemoSessionState.Active,
                DemoConsistencyStatus.InSync,
                nowUtc,
                ClosedAtUtc: null,
                LastConsistencyCheckedAtUtc: nowUtc,
                LastDriftDetectedAtUtc: null,
                LastDriftSummary: null);
        }
    }

    private sealed class FakeTradingModeResolver(ExecutionEnvironment effectiveMode) : ITradingModeResolver
    {
        public Task<TradingModeResolution> ResolveAsync(
            TradingModeResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TradingModeResolution(
                effectiveMode,
                UserOverrideMode: null,
                BotOverrideMode: null,
                StrategyPublishedMode: null,
                effectiveMode,
                TradingModeResolutionSource.GlobalDefault,
                $"Resolved by unit test as {effectiveMode}.",
                effectiveMode == ExecutionEnvironment.Live));
        }
    }

    private sealed class FakeIndicatorDataService(DateTime nowUtc) : IIndicatorDataService
    {
        private readonly Dictionary<string, StrategyIndicatorSnapshot> snapshots = new(StringComparer.Ordinal);

        public void SetReadySnapshot(StrategyIndicatorSnapshot snapshot) => snapshots[$"{snapshot.Symbol}|{snapshot.Timeframe}"] = snapshot;

        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<StrategyIndicatorSnapshot?> GetLatestAsync(string symbol, string timeframe, CancellationToken cancellationToken = default)
        {
            snapshots.TryGetValue($"{symbol}|{timeframe}", out var snapshot);
            return ValueTask.FromResult<StrategyIndicatorSnapshot?>(snapshot);
        }

        public ValueTask<StrategyIndicatorSnapshot?> PrimeAsync(string symbol, string timeframe, IReadOnlyCollection<MarketCandleSnapshot> historicalCandles, CancellationToken cancellationToken = default)
        {
            var snapshot = CreateIndicatorSnapshot(symbol, timeframe, nowUtc);
            SetReadySnapshot(snapshot);
            return ValueTask.FromResult<StrategyIndicatorSnapshot?>(snapshot);
        }

        public async IAsyncEnumerable<StrategyIndicatorSnapshot> WatchAsync(IEnumerable<IndicatorSubscription> subscriptions, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeMarketDataService(DateTime nowUtc) : IMarketDataService
    {
        private readonly Dictionary<string, MarketPriceSnapshot> prices = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SymbolMetadataSnapshot> metadata = new(StringComparer.Ordinal);

        public void SetPrice(string symbol, decimal price)
        {
            prices[symbol] = new MarketPriceSnapshot(symbol, price, nowUtc, nowUtc, "unit-test");
        }

        public void SetMetadata(string symbol, string baseAsset, string quoteAsset)
        {
            metadata[symbol] = new SymbolMetadataSnapshot(symbol, "Binance", baseAsset, quoteAsset, 0.1m, 0.001m, "TRADING", true, nowUtc)
            {
                MinQuantity = 0.001m,
                MinNotional = 100m,
                QuantityPrecision = 3
            };
            SetPrice(symbol, 100m);
        }

        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
        {
            prices.TryGetValue(symbol, out var snapshot);
            return ValueTask.FromResult<MarketPriceSnapshot?>(snapshot);
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            metadata.TryGetValue(symbol, out var snapshot);
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(snapshot);
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(IEnumerable<string> symbols, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeExchangeInfoClient(IReadOnlyDictionary<string, SymbolMetadataSnapshot> metadata) : IBinanceExchangeInfoClient
    {
        public Task<IReadOnlyCollection<SymbolMetadataSnapshot>> GetSymbolMetadataAsync(
            IReadOnlyCollection<string> symbols,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyCollection<SymbolMetadataSnapshot> snapshots = symbols
                .Where(symbol => metadata.ContainsKey(symbol))
                .Select(symbol => metadata[symbol])
                .ToArray();
            return Task.FromResult(snapshots);
        }

        public Task<DateTime?> GetServerTimeUtcAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<DateTime?>(null);
        }
    }

    private sealed class FakeSharedSymbolRegistry : ISharedSymbolRegistry
    {
        public ValueTask<SymbolMetadataSnapshot?> GetSymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(null);
        }

        public ValueTask<IReadOnlyCollection<SymbolMetadataSnapshot>> ListSymbolsAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IReadOnlyCollection<SymbolMetadataSnapshot>>([]);
        }
    }

    private sealed class FakeDataLatencyCircuitBreaker(DateTime nowUtc) : IDataLatencyCircuitBreaker
    {
        public Task<DegradedModeSnapshot> GetSnapshotAsync(string? correlationId = null, string? symbol = null, string? timeframe = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DegradedModeSnapshot(
                DegradedModeStateCode.Normal,
                DegradedModeReasonCode.None,
                false,
                false,
                nowUtc,
                nowUtc,
                0,
                0,
                nowUtc,
                true,
                "unit-test",
                symbol,
                timeframe,
                nowUtc.AddMinutes(1),
                0));
        }

        public Task<DegradedModeSnapshot> RecordHeartbeatAsync(DataLatencyHeartbeat heartbeat, string? correlationId = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DegradedModeSnapshot(
                heartbeat.GuardStateCode,
                heartbeat.GuardReasonCode,
                heartbeat.GuardStateCode != DegradedModeStateCode.Normal,
                heartbeat.GuardStateCode != DegradedModeStateCode.Normal,
                heartbeat.DataTimestampUtc,
                nowUtc,
                0,
                0,
                nowUtc,
                true,
                heartbeat.Source,
                heartbeat.Symbol,
                heartbeat.Timeframe,
                heartbeat.ExpectedOpenTimeUtc,
                heartbeat.ContinuityGapCount));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }

    private sealed class TestDataScopeContextAccessor : IDataScopeContextAccessor
    {
        public string? UserId { get; private set; }

        public bool HasIsolationBypass { get; private set; } = true;

        public IDisposable BeginScope(string? userId = null, bool hasIsolationBypass = false)
        {
            var previousUserId = UserId;
            var previousIsolationBypass = HasIsolationBypass;
            UserId = userId;
            HasIsolationBypass = hasIsolationBypass;
            return new ScopeReset(this, previousUserId, previousIsolationBypass);
        }

        private sealed class ScopeReset(TestDataScopeContextAccessor accessor, string? previousUserId, bool previousIsolationBypass) : IDisposable
        {
            public void Dispose()
            {
                accessor.UserId = previousUserId;
                accessor.HasIsolationBypass = previousIsolationBypass;
            }
        }
    }

    private sealed class ThrowingAiShadowDecisionService : IAiShadowDecisionService
    {
        public Task<AiShadowDecisionSnapshot> CaptureAsync(AiShadowDecisionWriteRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Root injected AI shadow decision service should not be used.");
        }

        public Task<AiShadowDecisionSnapshot?> GetLatestAsync(string userId, Guid botId, string symbol, string timeframe, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyCollection<AiShadowDecisionSnapshot>> ListRecentAsync(string userId, Guid botId, string symbol, string timeframe, int take = 20, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<AiShadowDecisionSummarySnapshot> GetSummaryAsync(string userId, Guid botId, string symbol, string timeframe, int take = 200, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<AiShadowDecisionOutcomeSnapshot> ScoreOutcomeAsync(string userId, Guid decisionId, AiShadowOutcomeHorizonKind horizonKind = AiShadowOutcomeDefaults.OfficialHorizonKind, int horizonValue = AiShadowOutcomeDefaults.OfficialHorizonValue, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> EnsureOutcomeCoverageAsync(string userId, AiShadowOutcomeHorizonKind horizonKind = AiShadowOutcomeDefaults.OfficialHorizonKind, int horizonValue = AiShadowOutcomeDefaults.OfficialHorizonValue, int take = 200, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<AiShadowDecisionOutcomeSummarySnapshot> GetOutcomeSummaryAsync(string userId, AiShadowOutcomeHorizonKind horizonKind = AiShadowOutcomeDefaults.OfficialHorizonKind, int horizonValue = AiShadowOutcomeDefaults.OfficialHorizonValue, int take = 200, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<LogRecord> records = [];

        public IReadOnlyCollection<LogRecord> Records => records;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            records.Add(new LogRecord(logLevel, formatter(state, exception), exception));
        }

        public sealed record LogRecord(LogLevel Level, string Message, Exception? Exception);

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed record BotGraph(Guid BotId, string OwnerUserId, Guid ExchangeAccountId, Guid TradingStrategyId, Guid TradingStrategyVersionId);

    private sealed class AiEnabledTestHarness(
        ApplicationDbContext dbContext,
        MarketScannerHandoffService service,
        ServiceProvider serviceProvider,
        FakeMarketDataService marketDataService,
        FakeIndicatorDataService indicatorDataService,
        FakeExecutionGate executionGate,
        FakeUserExecutionOverrideGuard userExecutionOverrideGuard,
        DateTime nowUtc) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public MarketScannerHandoffService Service { get; } = service;

        public FakeMarketDataService MarketDataService { get; } = marketDataService;

        public FakeIndicatorDataService IndicatorDataService { get; } = indicatorDataService;

        public FakeExecutionGate ExecutionGate { get; } = executionGate;

        public FakeUserExecutionOverrideGuard UserExecutionOverrideGuard { get; } = userExecutionOverrideGuard;

        public DateTime NowUtc { get; } = nowUtc;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await serviceProvider.DisposeAsync();
        }
    }

    private sealed class TestHarness(
        ApplicationDbContext dbContext,
        MarketScannerHandoffService service,
        ServiceProvider serviceProvider,
        FakeMarketDataService marketDataService,
        FakeIndicatorDataService indicatorDataService,
        FakeStrategySignalService strategySignalService,
        FakeExecutionGate executionGate,
        FakeUserExecutionOverrideGuard userExecutionOverrideGuard,
        FakeExecutionEngine executionEngine,
        FakeDemoSessionService demoSessionService,
        DateTime nowUtc) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public MarketScannerHandoffService Service { get; } = service;

        public FakeMarketDataService MarketDataService { get; } = marketDataService;

        public FakeIndicatorDataService IndicatorDataService { get; } = indicatorDataService;

        public FakeStrategySignalService StrategySignalService { get; } = strategySignalService;

        public FakeExecutionGate ExecutionGate { get; } = executionGate;

        public FakeUserExecutionOverrideGuard UserExecutionOverrideGuard { get; } = userExecutionOverrideGuard;

        public FakeExecutionEngine ExecutionEngine { get; } = executionEngine;

        public FakeDemoSessionService DemoSessionService { get; } = demoSessionService;

        public DateTime NowUtc { get; } = nowUtc;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await serviceProvider.DisposeAsync();
        }
    }
}
