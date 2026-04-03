using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.MarketData;

public sealed class MarketScannerHandoffServiceTests
{
    [Fact]
    public async Task RunOnceAsync_PreparesDeterministicTopCandidateAndPassesSelectedSymbolToStrategyAndGuards()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        await using var harness = CreateHarness(nowUtc);
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
        Assert.Contains("Top-ranked eligible candidate selected", persistedAttempt.SelectionReason, StringComparison.Ordinal);
        Assert.Equal("BTCUSDT", harness.StrategySignalService.LastRequest?.EvaluationContext.IndicatorSnapshot.Symbol);
        Assert.Equal("BTCUSDT", harness.ExecutionGate.LastRequest?.Symbol);
        Assert.Equal("1m", harness.ExecutionGate.LastRequest?.Timeframe);
        Assert.Equal("BTCUSDT", harness.UserExecutionOverrideGuard.LastRequest?.Symbol);
        Assert.NotEqual(string.Empty, persistedAttempt.CorrelationId);
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
        Assert.Equal("Execution blocked because market data is stale.", attempt.BlockerDetail);
        Assert.Equal("Persisted", attempt.StrategyDecisionOutcome);
        Assert.Contains("ExecutionGate=StaleMarketData; Symbol=BTCUSDT; Timeframe=1m", attempt.GuardSummary, StringComparison.Ordinal);
        Assert.Equal("BTCUSDT", harness.ExecutionGate.LastRequest?.Symbol);
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
        Assert.Equal("CandidateSelection=None", attempt.GuardSummary);
        Assert.Null(attempt.SelectedSymbol);
    }

    private static TestHarness CreateHarness(DateTimeOffset nowUtc)
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

        var services = new ServiceCollection();
        services.AddScoped<IDataScopeContextAccessor, TestDataScopeContextAccessor>();
        services.AddScoped(provider => new ApplicationDbContext(options, provider.GetRequiredService<IDataScopeContextAccessor>()));
        services.AddSingleton<IStrategySignalService>(strategySignalService);
        services.AddSingleton<IExecutionGate>(executionGate);
        services.AddSingleton<IUserExecutionOverrideGuard>(userExecutionOverrideGuard);
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
            new FixedTimeProvider(nowUtc),
            NullLogger<MarketScannerHandoffService>.Instance);

        return new TestHarness(dbContext, service, serviceProvider, marketDataService, indicatorDataService, strategySignalService, executionGate, userExecutionOverrideGuard, nowUtc.UtcDateTime);
    }

    private static async Task<BotGraph> SeedBotGraphAsync(ApplicationDbContext dbContext, string ownerUserId, string symbol, string strategyKey)
    {
        var tradingStrategyId = Guid.NewGuid();
        var tradingStrategyVersionId = Guid.NewGuid();
        var botId = Guid.NewGuid();

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
        dbContext.TradingStrategyVersions.Add(new TradingStrategyVersion
        {
            Id = tradingStrategyVersionId,
            OwnerUserId = ownerUserId,
            TradingStrategyId = tradingStrategyId,
            SchemaVersion = 1,
            VersionNumber = 1,
            Status = StrategyVersionStatus.Published,
            DefinitionJson = "{}",
            PublishedAtUtc = new DateTime(2026, 4, 3, 11, 59, 0, DateTimeKind.Utc)
        });
        dbContext.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = ownerUserId,
            Name = strategyKey,
            StrategyKey = strategyKey,
            Symbol = symbol,
            IsEnabled = true
        });
        await dbContext.SaveChangesAsync();

        return new BotGraph(botId, tradingStrategyId, tradingStrategyVersionId);
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

    private sealed record BotGraph(Guid BotId, Guid TradingStrategyId, Guid TradingStrategyVersionId);

    private sealed class TestHarness(
        ApplicationDbContext dbContext,
        MarketScannerHandoffService service,
        ServiceProvider serviceProvider,
        FakeMarketDataService marketDataService,
        FakeIndicatorDataService indicatorDataService,
        FakeStrategySignalService strategySignalService,
        FakeExecutionGate executionGate,
        FakeUserExecutionOverrideGuard userExecutionOverrideGuard,
        DateTime nowUtc) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public MarketScannerHandoffService Service { get; } = service;

        public FakeMarketDataService MarketDataService { get; } = marketDataService;

        public FakeIndicatorDataService IndicatorDataService { get; } = indicatorDataService;

        public FakeStrategySignalService StrategySignalService { get; } = strategySignalService;

        public FakeExecutionGate ExecutionGate { get; } = executionGate;

        public FakeUserExecutionOverrideGuard UserExecutionOverrideGuard { get; } = userExecutionOverrideGuard;

        public DateTime NowUtc { get; } = nowUtc;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await serviceProvider.DisposeAsync();
        }
    }
}


