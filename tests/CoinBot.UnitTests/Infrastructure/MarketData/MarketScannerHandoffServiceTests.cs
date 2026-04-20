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
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Risk;
using CoinBot.Infrastructure.Strategies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        Assert.Equal("BTCUSDT", harness.ExecutionGate.LastRequest?.Symbol);
        Assert.Equal("1m", harness.ExecutionGate.LastRequest?.Timeframe);
        Assert.Equal(btcBot.ExchangeAccountId, harness.ExecutionGate.LastRequest?.ExchangeAccountId);
        Assert.Equal(ExchangeDataPlane.Futures, harness.ExecutionGate.LastRequest?.Plane);
        Assert.Contains("DevelopmentFuturesTestnetPilot=True", harness.ExecutionGate.LastRequest?.Context, StringComparison.Ordinal);
        Assert.Equal("BTCUSDT", harness.UserExecutionOverrideGuard.LastRequest?.Symbol);
        Assert.NotEqual(string.Empty, persistedAttempt.CorrelationId);
        var order = await harness.DbContext.ExecutionOrders.SingleAsync(entity => entity.StrategySignalId == persistedAttempt.StrategySignalId);
        Assert.Equal(ExecutionEnvironment.Live, order.ExecutionEnvironment);
        Assert.Equal(ExecutionOrderExecutorKind.Binance, order.ExecutorKind);
        Assert.Equal(persistedAttempt.BotId, order.BotId);
        Assert.Equal(btcBot.ExchangeAccountId, order.ExchangeAccountId);
        Assert.Equal(btcBot.ExchangeAccountId, harness.ExecutionEngine.LastCommand?.ExchangeAccountId);
        Assert.Equal("scanner-handoff", order.IdempotencyKey.Split(':')[0]);
    }

    [Fact]
    public async Task RunOnceAsync_UsesResolvedTradingModeForExecutionGuards_WhenSignalEvaluationModeIsLive()
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
        Assert.Equal(ExecutionEnvironment.Demo, harness.ExecutionGate.LastRequest?.Environment);
        Assert.Null(harness.ExecutionGate.LastRequest?.ExchangeAccountId);
        Assert.DoesNotContain("DevelopmentFuturesTestnetPilot=True", harness.ExecutionGate.LastRequest?.Context, StringComparison.Ordinal);
        Assert.Equal(ExecutionEnvironment.Demo, harness.UserExecutionOverrideGuard.LastRequest?.Environment);
        var order = await harness.DbContext.ExecutionOrders.SingleAsync(entity => entity.StrategySignalId == attempt.StrategySignalId);
        Assert.Equal(ExecutionEnvironment.Demo, order.ExecutionEnvironment);
        Assert.Equal(ExecutionOrderExecutorKind.Virtual, order.ExecutorKind);
        Assert.True(harness.ExecutionEngine.LastCommand?.IsDemo);
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
        var existingSignal = CreateEntrySignal(bot.TradingStrategyId, bot.TradingStrategyVersionId, "SOLUSDT", "1m", harness.NowUtc);
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
            ExecutionEnvironment = ExecutionEnvironment.Demo,
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
                    [new DeterministicStubAiSignalProviderAdapter(), new OfflineAiSignalProviderAdapter(), new OpenAiSignalProviderAdapter(), new GeminiAiSignalProviderAdapter()],
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
        ExecutionEnvironment resolvedTradingMode = ExecutionEnvironment.Live)
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

        var services = new ServiceCollection();
        services.AddScoped<IDataScopeContextAccessor, TestDataScopeContextAccessor>();
        services.AddScoped(provider => new ApplicationDbContext(options, provider.GetRequiredService<IDataScopeContextAccessor>()));
        services.AddSingleton<IStrategySignalService>(strategySignalService);
        services.AddSingleton<IExecutionGate>(executionGate);
        services.AddSingleton<IUserExecutionOverrideGuard>(userExecutionOverrideGuard);
        services.AddSingleton<IExecutionEngine>(executionEngine);
        services.AddSingleton<IDemoSessionService>(demoSessionService);
        services.AddSingleton<ITradingModeResolver>(new FakeTradingModeResolver(resolvedTradingMode));
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
            NullLogger<MarketScannerHandoffService>.Instance);

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

    private static void SeedCandidate(ApplicationDbContext dbContext, Guid scanCycleId, string symbol, int? rank, decimal score, bool isEligible = true, string? rejectionReason = null)
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

    private static StrategySignalSnapshot CreateEntrySignal(Guid tradingStrategyId, Guid tradingStrategyVersionId, string symbol, string timeframe, DateTime generatedAtUtc)
    {
        var indicatorSnapshot = CreateIndicatorSnapshot(symbol, timeframe, generatedAtUtc);
        return new StrategySignalSnapshot(
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
            generatedAtUtc,
            new StrategySignalExplainabilityPayload(
                1,
                tradingStrategyId,
                tradingStrategyVersionId,
                1,
                1,
                ExecutionEnvironment.Live,
                indicatorSnapshot,
                new StrategyEvaluationResult(true, true, false, false, true, true, null, null, null),
                new StrategySignalConfidenceSnapshot(91, StrategySignalConfidenceBand.High, 3, 3, true, true, false, RiskVetoReasonCode.None, false, "Entry accepted."),
                new StrategySignalLogExplainabilitySnapshot("Entry", "Entry accepted", ["driver"], ["scanner"]),
                new StrategySignalDuplicateSuppressionSnapshot(true, false, $"fp-{symbol}")));
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
                ExecutionEnvironment = command.IsDemo == true ? ExecutionEnvironment.Demo : ExecutionEnvironment.Live,
                ExecutorKind = command.IsDemo == true ? ExecutionOrderExecutorKind.Virtual : ExecutionOrderExecutorKind.Binance,
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

        public void SetMetadata(string symbol, string baseAsset, string quoteAsset)
        {
            metadata[symbol] = new SymbolMetadataSnapshot(symbol, "Binance", baseAsset, quoteAsset, 0.1m, 0.001m, "TRADING", true, nowUtc)
            {
                MinQuantity = 0.001m,
                MinNotional = 100m,
                QuantityPrecision = 3
            };
            prices[symbol] = new MarketPriceSnapshot(symbol, 100m, nowUtc, nowUtc, "unit-test");
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
