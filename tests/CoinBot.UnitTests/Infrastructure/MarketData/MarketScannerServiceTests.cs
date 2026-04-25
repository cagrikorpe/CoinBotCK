using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.MarketData;

public sealed class MarketScannerServiceTests
{
    [Fact]
    public async Task RunOnceAsync_ResolvesUniverseFromRegistryConfigBotsAndHistoricalCandles_AndRanksEligibleCandidatesDeterministically()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        dbContext.TradingBots.Add(new TradingBot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-1",
            Name = "ETH Bot",
            StrategyKey = "breakout",
            Symbol = "ETHUSDT",
            IsEnabled = true
        });
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        SeedCandles(dbContext, "ETHUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        SeedCandles(dbContext, "SOLUSDT", nowUtc.UtcDateTime, closePrice: 25m, volume: 10m);
        SeedCandles(dbContext, "ADAUSDT", nowUtc.UtcDateTime, closePrice: 1m, volume: 3_000_000m);
        await dbContext.SaveChangesAsync();

        var marketDataService = new FakeMarketDataService();
        marketDataService.SetLatestPrice("BTCUSDT", 100m, nowUtc.UtcDateTime);
        marketDataService.SetLatestPrice("ETHUSDT", 100m, nowUtc.UtcDateTime);
        marketDataService.SetLatestPrice("SOLUSDT", 25m, nowUtc.UtcDateTime);
        marketDataService.SetLatestPrice("ADAUSDT", 1m, nowUtc.UtcDateTime);
        var service = new MarketScannerService(
            dbContext,
            marketDataService,
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime),
                new SymbolMetadataSnapshot("ETHUSDT", "Binance", "ETH", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime),
                new SymbolMetadataSnapshot("SOLUSDT", "Binance", "SOL", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 2,
                MaxUniverseSymbols = 50,
                Min24hQuoteVolume = 50_000m,
                MaxDataAgeSeconds = 120,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = false
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT", "   ", "SOLUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance);

        var cycle = await service.RunOnceAsync();

        var persistedCycle = await dbContext.MarketScannerCycles.SingleAsync(entity => entity.Id == cycle.Id);
        var candidates = await dbContext.MarketScannerCandidates
            .Where(entity => entity.ScanCycleId == cycle.Id)
            .OrderBy(entity => entity.Symbol)
            .ToListAsync();
        var heartbeat = await dbContext.WorkerHeartbeats.SingleAsync(entity => entity.WorkerKey == MarketScannerService.WorkerKey);

        Assert.Equal(4, persistedCycle.ScannedSymbolCount);
        Assert.Equal(3, persistedCycle.EligibleCandidateCount);
        Assert.Equal("ADAUSDT", persistedCycle.BestCandidateSymbol);
        Assert.Equal("config+enabled-bot+historical-candles+registry", persistedCycle.UniverseSource);

        var ada = Assert.Single(candidates, candidate => candidate.Symbol == "ADAUSDT");
        var btc = Assert.Single(candidates, candidate => candidate.Symbol == "BTCUSDT");
        var eth = Assert.Single(candidates, candidate => candidate.Symbol == "ETHUSDT");
        var sol = Assert.Single(candidates, candidate => candidate.Symbol == "SOLUSDT");

        Assert.True(ada.IsEligible);
        Assert.Equal(1, ada.Rank);
        Assert.True(ada.IsTopCandidate);
        Assert.Equal("historical-candles", ada.UniverseSource);
        Assert.True(btc.IsEligible);
        Assert.Equal(100m, btc.LastPrice);
        Assert.Equal(2, btc.Rank);
        Assert.True(btc.IsTopCandidate);
        Assert.Equal("config+historical-candles+registry", btc.UniverseSource);
        Assert.True(eth.IsEligible);
        Assert.Equal(3, eth.Rank);
        Assert.False(eth.IsTopCandidate);
        Assert.Equal("enabled-bot+historical-candles+registry", eth.UniverseSource);
        Assert.False(sol.IsEligible);
        Assert.Equal("LowQuoteVolume", sol.RejectionReason);
        Assert.Equal(MonitoringHealthState.Healthy, heartbeat.HealthState);
        Assert.Contains("Timeframe=1m", heartbeat.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("HandoffEnabled=False", heartbeat.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("BestCandidate=ADAUSDT", heartbeat.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(4, marketDataService.SharedPriceReadCount);
        Assert.Equal(0, marketDataService.LegacyPriceReadCount);
    }

    [Fact]
    public async Task RunOnceAsync_ArchivesLegacyDirtyMarketScoreCandidates_AndRebuildsAffectedCycle()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        var archivedCycleId = Guid.NewGuid();
        var dirtyCandidateId = Guid.NewGuid();

        dbContext.MarketScannerCycles.Add(new MarketScannerCycle
        {
            Id = archivedCycleId,
            StartedAtUtc = nowUtc.UtcDateTime.AddMinutes(-2),
            CompletedAtUtc = nowUtc.UtcDateTime.AddMinutes(-1),
            UniverseSource = "legacy",
            ScannedSymbolCount = 2,
            EligibleCandidateCount = 2,
            TopCandidateCount = 2,
            BestCandidateSymbol = "BTCUSDT",
            BestCandidateScore = 95m,
            Summary = "legacy"
        });
        dbContext.MarketScannerCandidates.AddRange(
            new MarketScannerCandidate
            {
                Id = dirtyCandidateId,
                ScanCycleId = archivedCycleId,
                Symbol = "BTCUSDT",
                UniverseSource = "legacy",
                ObservedAtUtc = nowUtc.UtcDateTime.AddMinutes(-1),
                LastCandleAtUtc = nowUtc.UtcDateTime.AddMinutes(-1),
                LastPrice = 100m,
                QuoteVolume24h = 123_456m,
                MarketScore = 123_456m,
                StrategyScore = 90,
                ScoringSummary = "legacy",
                IsEligible = true,
                Score = 95m,
                Rank = 1,
                IsTopCandidate = true
            },
            new MarketScannerCandidate
            {
                Id = Guid.NewGuid(),
                ScanCycleId = archivedCycleId,
                Symbol = "ETHUSDT",
                UniverseSource = "legacy",
                ObservedAtUtc = nowUtc.UtcDateTime.AddMinutes(-1),
                LastCandleAtUtc = nowUtc.UtcDateTime.AddMinutes(-1),
                LastPrice = 90m,
                QuoteVolume24h = 100_000m,
                MarketScore = 100m,
                StrategyScore = 70,
                ScoringSummary = "clean",
                IsEligible = true,
                Score = 80m,
                Rank = 2,
                IsTopCandidate = true
            });
        await dbContext.SaveChangesAsync();

        var service = new MarketScannerService(
            dbContext,
            new FakeMarketDataService(),
            new FakeSharedSymbolRegistry([]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 2,
                MaxUniverseSymbols = 50,
                Min24hQuoteVolume = 1_000m,
                MaxDataAgeSeconds = 120,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = false
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = [] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance);

        _ = await service.RunOnceAsync();

        var archivedCandidate = await dbContext.MarketScannerCandidates
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.Id == dirtyCandidateId);
        var rebuiltCycle = await dbContext.MarketScannerCycles.SingleAsync(entity => entity.Id == archivedCycleId);

        Assert.True(archivedCandidate.IsDeleted);
        Assert.False(archivedCandidate.IsTopCandidate);
        Assert.Null(archivedCandidate.Rank);
        Assert.Equal(MarketScannerCandidateIntegrityGuard.LegacyArchivedDirtyMarketScoreReason, archivedCandidate.RejectionReason);
        Assert.Equal(1, rebuiltCycle.ScannedSymbolCount);
        Assert.Equal(1, rebuiltCycle.EligibleCandidateCount);
        Assert.Equal(1, rebuiltCycle.TopCandidateCount);
        Assert.Equal("ETHUSDT", rebuiltCycle.BestCandidateSymbol);
        Assert.Equal(80m, rebuiltCycle.BestCandidateScore);
    }

    [Fact]
    public async Task RunOnceAsync_AppliesStrategyAwareCompositeScore_AndPersistsScoringSummaryDeterministically()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        var btcStrategy = await SeedStrategyGraphAsync(dbContext, "user-btc", "BTCUSDT", "scanner-btc", "{}");
        var ethStrategy = await SeedStrategyGraphAsync(dbContext, "user-eth", "ETHUSDT", "scanner-eth", "{}");
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        SeedCandles(dbContext, "ETHUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 2_000m);
        await dbContext.SaveChangesAsync();

        var indicatorDataService = new FakeIndicatorDataService();
        indicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", nowUtc.UtcDateTime));
        indicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("ETHUSDT", "1m", nowUtc.UtcDateTime));

        var strategyEvaluatorService = new FakeStrategyEvaluatorService();
        strategyEvaluatorService.SetReport("BTCUSDT", CreateEvaluationReport(btcStrategy.TradingStrategyId, btcStrategy.TradingStrategyVersionId, "scanner-btc", "BTCUSDT", "1m", nowUtc.UtcDateTime, 95, "BTC strategy accepted."));
        strategyEvaluatorService.SetReport("ETHUSDT", CreateEvaluationReport(ethStrategy.TradingStrategyId, ethStrategy.TradingStrategyVersionId, "scanner-eth", "ETHUSDT", "1m", nowUtc.UtcDateTime, 5, "ETH strategy weak but accepted."));

        var service = new MarketScannerService(
            dbContext,
            new FakeMarketDataService(),
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime),
                new SymbolMetadataSnapshot("ETHUSDT", "Binance", "ETH", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 2,
                MaxUniverseSymbols = 50,
                Min24hQuoteVolume = 1_000m,
                MaxDataAgeSeconds = 120,
                StrategyScoreWeight = 2m,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = true
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT", "ETHUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance,
            handoffService: null,
            indicatorDataService,
            strategyEvaluatorService,
            Options.Create(new BotExecutionPilotOptions { SignalEvaluationMode = ExecutionEnvironment.Live }));

        var cycle = await service.RunOnceAsync();

        var rankedCandidates = await dbContext.MarketScannerCandidates
            .Where(entity => entity.ScanCycleId == cycle.Id && entity.IsEligible)
            .OrderBy(entity => entity.Rank)
            .ToListAsync();
        var heartbeat = await dbContext.WorkerHeartbeats.SingleAsync(entity => entity.WorkerKey == MarketScannerService.WorkerKey);

        Assert.Equal("BTCUSDT", cycle.BestCandidateSymbol);
        Assert.Equal(["BTCUSDT", "ETHUSDT"], rankedCandidates.Select(candidate => candidate.Symbol).ToArray());

        var btc = Assert.Single(rankedCandidates, candidate => candidate.Symbol == "BTCUSDT");
        var eth = Assert.Single(rankedCandidates, candidate => candidate.Symbol == "ETHUSDT");

        Assert.Equal(100m, btc.MarketScore);
        Assert.Equal(95, btc.StrategyScore);
        Assert.Equal(96.6667m, btc.Score);
        Assert.Contains("StrategyKey=scanner-btc", btc.ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("StrategyScore=95", btc.ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("ScannerLabels=HasTrendBreakoutUp", btc.ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("ScannerReasonCodes=TrendBreakoutConfirmed", btc.ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("ScannerReasonSummary=Bullish trend breakout confirmed above the Bollinger mid-band with positive MACD alignment.", btc.ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("ScannerShadowScore=55", btc.ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("ScannerShadowContributions=TrendBreakoutConfirmed:+55", btc.ScoringSummary, StringComparison.Ordinal);

        Assert.Equal(100m, eth.MarketScore);
        Assert.Equal(5, eth.StrategyScore);
        Assert.Equal(36.6667m, eth.Score);
        Assert.InRange(btc.MarketScore, 0m, 100m);
        Assert.InRange(eth.MarketScore, 0m, 100m);
        Assert.InRange(btc.Score, 0m, 100m);
        Assert.InRange(eth.Score, 0m, 100m);
        Assert.True(btc.Score > eth.Score);
        Assert.Contains("HandoffEnabled=True", heartbeat.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("BTCUSDT", strategyEvaluatorService.RequestedSymbols[0]);
        Assert.Equal("ETHUSDT", strategyEvaluatorService.RequestedSymbols[1]);
    }

    [Fact]
    public async Task RunOnceAsync_AppendsCompressionSetupScannerLabel_WhenBollingerBandwidthIsTight()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        var strategy = await SeedStrategyGraphAsync(dbContext, "user-compression", "BTCUSDT", "scanner-compression", "{}");
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        await dbContext.SaveChangesAsync();

        var indicatorDataService = new FakeIndicatorDataService();
        indicatorDataService.SetReadySnapshot(new StrategyIndicatorSnapshot(
            "BTCUSDT",
            "1m",
            nowUtc.UtcDateTime.AddMinutes(-1),
            nowUtc.UtcDateTime,
            nowUtc.UtcDateTime,
            100,
            34,
            IndicatorDataState.Ready,
            DegradedModeReasonCode.None,
            new RelativeStrengthIndexSnapshot(14, true, 52m),
            new MovingAverageConvergenceDivergenceSnapshot(12, 26, 9, true, 0.3m, 0.1m, 0.2m),
            new BollingerBandsSnapshot(20, 2m, true, 100m, 102.5m, 97.5m, 1m),
            "unit-test"));

        var strategyEvaluatorService = new FakeStrategyEvaluatorService();
        strategyEvaluatorService.SetReport("BTCUSDT", CreateEvaluationReport(
            strategy.TradingStrategyId,
            strategy.TradingStrategyVersionId,
            "scanner-compression",
            "BTCUSDT",
            "1m",
            nowUtc.UtcDateTime,
            85,
            "Compression setup accepted."));

        var service = new MarketScannerService(
            dbContext,
            new FakeMarketDataService(),
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 1,
                MaxUniverseSymbols = 10,
                Min24hQuoteVolume = 1_000m,
                MaxDataAgeSeconds = 120,
                StrategyScoreWeight = 2m,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = true
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance,
            handoffService: null,
            indicatorDataService,
            strategyEvaluatorService,
            Options.Create(new BotExecutionPilotOptions { SignalEvaluationMode = ExecutionEnvironment.Live }));

        var cycle = await service.RunOnceAsync();

        var candidate = await dbContext.MarketScannerCandidates.SingleAsync(entity => entity.ScanCycleId == cycle.Id);
        Assert.Contains("ScannerLabels=HasCompressionBreakoutSetup", candidate.ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("ScannerReasonCodes=CompressionBreakoutSetupDetected", candidate.ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("ScannerShadowScore=80", candidate.ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("ScannerShadowContributions=CompressionBreakoutSetupDetected:+25,TrendBreakoutConfirmed:+55", candidate.ScoringSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnceAsync_PreservesScannerReasonTokens_WhenScoringSummaryAndHistoricalRecoveryAreLong()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        var strategy = await SeedStrategyGraphAsync(dbContext, "user-long-summary", "BTCUSDT", "scanner-long-summary", "{}");
        await dbContext.SaveChangesAsync();

        var marketDataService = new FakeMarketDataService();
        marketDataService.SetLatestPrice("BTCUSDT", 100m, nowUtc.UtcDateTime);
        marketDataService.SetLatestKline(
            "BTCUSDT",
            "1m",
            new MarketCandleSnapshot(
                "BTCUSDT",
                "1m",
                nowUtc.UtcDateTime.AddMinutes(-1),
                nowUtc.UtcDateTime,
                100m,
                100m,
                100m,
                100m,
                1_500m,
                true,
                nowUtc.UtcDateTime,
                "unit-test-shared"),
            SharedMarketDataCacheReadStatus.HitFresh);

        var historicalClient = new FakeHistoricalKlineClient();
        historicalClient.SetCandles(
            "BTCUSDT",
            CreateClosedCandles("BTCUSDT", nowUtc.UtcDateTime.AddMinutes(-20), 20, 100m, 2_000m, "unit-test-rest"));

        var indicatorDataService = new FakeIndicatorDataService();
        indicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", nowUtc.UtcDateTime));

        var strategyEvaluatorService = new FakeStrategyEvaluatorService();
        strategyEvaluatorService.SetReport("BTCUSDT", CreateEvaluationReport(
            strategy.TradingStrategyId,
            strategy.TradingStrategyVersionId,
            "scanner-long-summary",
            "BTCUSDT",
            "1m",
            nowUtc.UtcDateTime,
            95,
            new string('X', 1300)));

        var service = new MarketScannerService(
            dbContext,
            marketDataService,
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 1,
                MaxUniverseSymbols = 10,
                Min24hQuoteVolume = 1_000m,
                MaxDataAgeSeconds = 120,
                StrategyScoreWeight = 2m,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = false
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance,
            handoffService: null,
            indicatorDataService,
            strategyEvaluatorService,
            Options.Create(new BotExecutionPilotOptions { SignalEvaluationMode = ExecutionEnvironment.Live }),
            historicalKlineClient: historicalClient);

        var cycle = await service.RunOnceAsync();

        var candidate = await dbContext.MarketScannerCandidates.SingleAsync(entity => entity.ScanCycleId == cycle.Id);
        Assert.NotNull(candidate.ScoringSummary);
        Assert.True(candidate.ScoringSummary!.Length <= 2048);
        Assert.Contains("ScannerReasonCodes=TrendBreakoutConfirmed", candidate.ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("ScannerReasonSummary=Bullish trend breakout confirmed above the Bollinger mid-band with positive MACD alignment.", candidate.ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("HistoricalRecoveryApplied=True", candidate.ScoringSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnceAsync_ReportsNoEnabledBotForSymbol_DistinctFromNoPublishedStrategy()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        await dbContext.SaveChangesAsync();

        var marketDataService = new FakeMarketDataService();
        marketDataService.SetLatestPrice("BTCUSDT", 100m, nowUtc.UtcDateTime);

        var service = new MarketScannerService(
            dbContext,
            marketDataService,
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 1,
                MaxUniverseSymbols = 10,
                Min24hQuoteVolume = 100m,
                MaxDataAgeSeconds = 120,
                StrategyScoreWeight = 2m,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = true
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance,
            handoffService: null,
            indicatorDataService: new FakeIndicatorDataService(),
            strategyEvaluatorService: new FakeStrategyEvaluatorService(),
            Options.Create(new BotExecutionPilotOptions { SignalEvaluationMode = ExecutionEnvironment.Live }));

        var cycle = await service.RunOnceAsync();

        var candidate = await dbContext.MarketScannerCandidates.SingleAsync(entity => entity.ScanCycleId == cycle.Id);
        Assert.False(candidate.IsEligible);
        Assert.Equal("NoEnabledBotForSymbol", candidate.RejectionReason);
        Assert.Contains("StrategyOutcome=NoEnabledBotForSymbol", candidate.ScoringSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnceAsync_AcceptsActivePublishedVersion_WhenStrategyHeaderStateIsDraft()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        var strategyId = Guid.NewGuid();
        var activeVersionId = Guid.NewGuid();
        var marketDataService = new FakeMarketDataService();
        marketDataService.SetLatestPrice("BTCUSDT", 100m, nowUtc.UtcDateTime);

        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = strategyId,
            OwnerUserId = "user-header-drift",
            StrategyKey = "scanner-header-drift",
            DisplayName = "scanner-header-drift",
            PromotionState = StrategyPromotionState.Draft,
            PublishedMode = null,
            PublishedAtUtc = null,
            UsesExplicitVersionLifecycle = true,
            ActiveTradingStrategyVersionId = activeVersionId,
            ActiveVersionActivatedAtUtc = nowUtc.UtcDateTime.AddMinutes(-5)
        });
        dbContext.TradingStrategyVersions.Add(new TradingStrategyVersion
        {
            Id = activeVersionId,
            OwnerUserId = "user-header-drift",
            TradingStrategyId = strategyId,
            SchemaVersion = 2,
            VersionNumber = 1,
            Status = StrategyVersionStatus.Published,
            DefinitionJson = "{\"schemaVersion\":2,\"metadata\":{\"templateKey\":\"active-v1\",\"templateName\":\"Active V1\"},\"entry\":{\"path\":\"context.mode\",\"comparison\":\"equals\",\"value\":\"Live\"}}",
            PublishedAtUtc = nowUtc.UtcDateTime.AddMinutes(-5)
        });
        dbContext.TradingBots.Add(new TradingBot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-header-drift",
            Name = "scanner-header-drift bot",
            StrategyKey = "scanner-header-drift",
            Symbol = "BTCUSDT",
            IsEnabled = true
        });
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        await dbContext.SaveChangesAsync();

        var indicatorDataService = new FakeIndicatorDataService();
        indicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", nowUtc.UtcDateTime));

        var strategyEvaluatorService = new FakeStrategyEvaluatorService();
        strategyEvaluatorService.SetReport("BTCUSDT", CreateEvaluationReport(strategyId, activeVersionId, "scanner-header-drift", "BTCUSDT", "1m", nowUtc.UtcDateTime, 95, "Active strategy accepted."));

        var service = new MarketScannerService(
            dbContext,
            marketDataService,
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 1,
                MaxUniverseSymbols = 10,
                Min24hQuoteVolume = 1_000m,
                MaxDataAgeSeconds = 120,
                StrategyScoreWeight = 2m,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = true
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance,
            handoffService: null,
            indicatorDataService,
            strategyEvaluatorService,
            Options.Create(new BotExecutionPilotOptions { SignalEvaluationMode = ExecutionEnvironment.Live }));

        var cycle = await service.RunOnceAsync();

        var candidate = await dbContext.MarketScannerCandidates.SingleAsync(entity => entity.ScanCycleId == cycle.Id);
        Assert.True(candidate.IsEligible);
        Assert.Null(candidate.RejectionReason);
        Assert.Equal(activeVersionId, Assert.Single(strategyEvaluatorService.RequestedVersionIds));
        Assert.Contains("StrategyKey=scanner-header-drift", candidate.ScoringSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnceAsync_ReportsStrategyRiskVetoWithRuleScopedReason()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        var strategy = await SeedStrategyGraphAsync(dbContext, "user-risk", "SOLUSDT", "scanner-risk", "{}");
        SeedCandles(dbContext, "SOLUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        await dbContext.SaveChangesAsync();

        var marketDataService = new FakeMarketDataService();
        marketDataService.SetLatestPrice("SOLUSDT", 100m, nowUtc.UtcDateTime);
        var indicatorDataService = new FakeIndicatorDataService();
        indicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", nowUtc.UtcDateTime));

        var failedRiskRule = new StrategyRuleResultSnapshot(
            false,
            null,
            "indicator.rsi.value",
            StrategyRuleComparisonOperator.LessThan,
            "30",
            StrategyRuleOperandKind.Number,
            "47.5",
            "30",
            Array.Empty<StrategyRuleResultSnapshot>(),
            RuleId: "risk-rsi-floor",
            RuleType: "risk",
            Timeframe: "1m",
            Group: "risk",
            Reason: "RSI did not enter the risk-approved band.");
        var riskResult = new StrategyRuleResultSnapshot(
            false,
            StrategyRuleGroupOperator.All,
            null,
            null,
            null,
            null,
            null,
            null,
            [failedRiskRule],
            RuleId: "risk-root",
            RuleType: "risk",
            Timeframe: "1m",
            Group: "risk",
            Reason: "Risk root failed.");
        var evaluationResult = new StrategyEvaluationResult(
            HasEntryRules: true,
            EntryMatched: false,
            HasExitRules: false,
            ExitMatched: false,
            HasRiskRules: true,
            RiskPassed: false,
            EntryRuleResult: null,
            ExitRuleResult: null,
            RiskRuleResult: riskResult);

        var strategyEvaluatorService = new FakeStrategyEvaluatorService();
        strategyEvaluatorService.SetReport(
            "SOLUSDT",
            CreateEvaluationReport(
                strategy.TradingStrategyId,
                strategy.TradingStrategyVersionId,
                "scanner-risk",
                "SOLUSDT",
                "1m",
                nowUtc.UtcDateTime,
                67,
                "Risk rule failed.",
                "RiskVetoed",
                evaluationResult,
                ["entry-ready [data-quality/1m] PASS w=10 :: ready"],
                ["risk-rsi-floor [risk/1m] FAIL w=30 :: RSI did not enter the risk-approved band."]));

        var service = new MarketScannerService(
            dbContext,
            marketDataService,
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("SOLUSDT", "Binance", "SOL", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 1,
                MaxUniverseSymbols = 10,
                Min24hQuoteVolume = 1_000m,
                MaxDataAgeSeconds = 120,
                StrategyScoreWeight = 2m,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = true
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["SOLUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance,
            handoffService: null,
            indicatorDataService,
            strategyEvaluatorService,
            Options.Create(new BotExecutionPilotOptions { SignalEvaluationMode = ExecutionEnvironment.Live }));

        var cycle = await service.RunOnceAsync();

        var candidate = await dbContext.MarketScannerCandidates.SingleAsync(entity => entity.ScanCycleId == cycle.Id);
        Assert.False(candidate.IsEligible);
        Assert.Equal("StrategyRiskVetoedRiskRsiFloor", candidate.RejectionReason);
        Assert.Contains("StrategyBlocker=StrategyRiskVetoedRiskRsiFloor", candidate.ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("StrategyRiskVetoedRiskRsiFloor:1 [SOLUSDT]", cycle.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnceAsync_ReportsExitMatchedWithDirectionScopedReason()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        var strategy = await SeedStrategyGraphAsync(dbContext, "user-exit", "SOLUSDT", "scanner-exit", "{}");
        SeedCandles(dbContext, "SOLUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        await dbContext.SaveChangesAsync();

        var marketDataService = new FakeMarketDataService();
        marketDataService.SetLatestPrice("SOLUSDT", 100m, nowUtc.UtcDateTime);
        var indicatorDataService = new FakeIndicatorDataService();
        indicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("SOLUSDT", "1m", nowUtc.UtcDateTime));

        var exitRule = new StrategyRuleResultSnapshot(
            true,
            null,
            "indicator.rsi.value",
            StrategyRuleComparisonOperator.GreaterThanOrEqual,
            "45",
            StrategyRuleOperandKind.Number,
            "47.5",
            "45",
            Array.Empty<StrategyRuleResultSnapshot>(),
            RuleId: "long-exit-rsi-recover",
            RuleType: "exit",
            Timeframe: "1m",
            Group: "long-exit",
            Reason: "Long exit RSI recovery matched.");
        var evaluationResult = new StrategyEvaluationResult(
            HasEntryRules: true,
            EntryMatched: false,
            HasExitRules: true,
            ExitMatched: true,
            HasRiskRules: true,
            RiskPassed: true,
            EntryRuleResult: null,
            ExitRuleResult: exitRule,
            RiskRuleResult: null,
            Direction: StrategyTradeDirection.Long,
            EntryDirection: StrategyTradeDirection.Neutral,
            ExitDirection: StrategyTradeDirection.Long);

        var strategyEvaluatorService = new FakeStrategyEvaluatorService();
        strategyEvaluatorService.SetReport(
            "SOLUSDT",
            CreateEvaluationReport(
                strategy.TradingStrategyId,
                strategy.TradingStrategyVersionId,
                "scanner-exit",
                "SOLUSDT",
                "1m",
                nowUtc.UtcDateTime,
                77,
                "Long exit matched.",
                "ExitMatched",
                evaluationResult,
                ["long-exit-rsi-recover [exit/1m] PASS w=60 :: Long exit RSI recovery matched."],
                ["entry-rsi-soft [rsi/1m] FAIL w=30 :: Entry not matched."]));

        var service = new MarketScannerService(
            dbContext,
            marketDataService,
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("SOLUSDT", "Binance", "SOL", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 1,
                MaxUniverseSymbols = 10,
                Min24hQuoteVolume = 1_000m,
                MaxDataAgeSeconds = 120,
                StrategyScoreWeight = 2m,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = true
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["SOLUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance,
            handoffService: null,
            indicatorDataService,
            strategyEvaluatorService,
            Options.Create(new BotExecutionPilotOptions { SignalEvaluationMode = ExecutionEnvironment.Live }));

        var cycle = await service.RunOnceAsync();

        var candidate = await dbContext.MarketScannerCandidates.SingleAsync(entity => entity.ScanCycleId == cycle.Id);
        Assert.False(candidate.IsEligible);
        Assert.Equal("StrategyLongExitMatched", candidate.RejectionReason);
        Assert.Contains("StrategyBlocker=StrategyLongExitMatched", candidate.ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("StrategyLongExitMatched:1 [SOLUSDT]", cycle.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnceAsync_UsesActiveHistoricalQuerySet_ForUniverseAndQuoteVolume()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        var deletedCandle = new HistoricalMarketCandle
        {
            Id = Guid.NewGuid(),
            Symbol = "ADAUSDT",
            Interval = "1m",
            OpenTimeUtc = nowUtc.UtcDateTime.AddMinutes(-3),
            CloseTimeUtc = nowUtc.UtcDateTime.AddMinutes(-2),
            OpenPrice = 1m,
            HighPrice = 1m,
            LowPrice = 1m,
            ClosePrice = 1m,
            Volume = 99m,
            ReceivedAtUtc = nowUtc.UtcDateTime.AddMinutes(-2),
            Source = "unit-test"
        };
        dbContext.HistoricalMarketCandles.AddRange(
            new HistoricalMarketCandle
            {
                Id = Guid.NewGuid(),
                Symbol = "ADAUSDT",
                Interval = " 1m ",
                OpenTimeUtc = nowUtc.UtcDateTime.AddHours(-1).AddMinutes(-1),
                CloseTimeUtc = nowUtc.UtcDateTime.AddHours(-1),
                OpenPrice = 1m,
                HighPrice = 1m,
                LowPrice = 1m,
                ClosePrice = 1m,
                Volume = 10m,
                ReceivedAtUtc = nowUtc.UtcDateTime.AddHours(-1),
                Source = " Binance.Rest.Kline "
            },
            new HistoricalMarketCandle
            {
                Id = Guid.NewGuid(),
                Symbol = "ADAUSDT",
                Interval = "1m",
                OpenTimeUtc = nowUtc.UtcDateTime.AddMinutes(-1),
                CloseTimeUtc = nowUtc.UtcDateTime,
                OpenPrice = 1m,
                HighPrice = 1m,
                LowPrice = 1m,
                ClosePrice = 1m,
                Volume = 20m,
                ReceivedAtUtc = nowUtc.UtcDateTime,
                Source = "unit-test"
            },
            new HistoricalMarketCandle
            {
                Id = Guid.NewGuid(),
                Symbol = "ADAUSDT",
                Interval = "1m",
                OpenTimeUtc = nowUtc.UtcDateTime.AddMinutes(-2),
                CloseTimeUtc = nowUtc.UtcDateTime.AddMinutes(-1),
                OpenPrice = 1m,
                HighPrice = 1m,
                LowPrice = 1m,
                ClosePrice = 1m,
                Volume = 99m,
                ReceivedAtUtc = nowUtc.UtcDateTime.AddMinutes(-1),
                Source = "   "
            },
            deletedCandle,
            new HistoricalMarketCandle
            {
                Id = Guid.NewGuid(),
                Symbol = "ADAUSDT",
                Interval = "1m",
                OpenTimeUtc = nowUtc.UtcDateTime.AddHours(-2).AddMinutes(-1),
                CloseTimeUtc = nowUtc.UtcDateTime.AddHours(-2),
                OpenPrice = 1m,
                HighPrice = 1m,
                LowPrice = 1m,
                ClosePrice = 1m,
                Volume = 99m,
                ReceivedAtUtc = nowUtc.UtcDateTime.AddHours(-2),
                Source = "unit-test"
            });
        await dbContext.SaveChangesAsync();
        deletedCandle.IsDeleted = true;
        await dbContext.SaveChangesAsync();

        var marketDataService = new FakeMarketDataService();
        marketDataService.SetLatestPrice("ADAUSDT", 1m, nowUtc.UtcDateTime);
        marketDataService.SetLatestKline(
            "ADAUSDT",
            "1m",
            new MarketCandleSnapshot(
                "ADAUSDT",
                "1m",
                nowUtc.UtcDateTime.AddMinutes(-1),
                nowUtc.UtcDateTime,
                1m,
                1m,
                1m,
                1m,
                20m,
                true,
                nowUtc.UtcDateTime,
                "unit-test-shared"),
            SharedMarketDataCacheReadStatus.HitFresh);

        var service = new MarketScannerService(
            dbContext,
            marketDataService,
            new FakeSharedSymbolRegistry([]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 1,
                MaxUniverseSymbols = 10,
                VolumeLookbackHours = 1,
                Min24hQuoteVolume = 10m,
                MaxDataAgeSeconds = 120,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = false
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = [] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance);

        var cycle = await service.RunOnceAsync();

        var candidate = await dbContext.MarketScannerCandidates.SingleAsync(entity => entity.ScanCycleId == cycle.Id);
        Assert.Equal(1, cycle.ScannedSymbolCount);
        Assert.Equal("historical-candles", cycle.UniverseSource);
        Assert.True(candidate.IsEligible);
        Assert.Equal("ADAUSDT", candidate.Symbol);
        Assert.Equal(30m, candidate.QuoteVolume24h);
    }

    [Fact]
    public async Task RunOnceAsync_UsesActiveHistoricalWindow_ForQuoteVolumeAndEligibility_WhenSharedMarketDataMissing()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        await dbContext.SaveChangesAsync();

        var service = new MarketScannerService(
            dbContext,
            new FakeMarketDataService(),
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 1,
                MaxUniverseSymbols = 10,
                Min24hQuoteVolume = 100m,
                MaxDataAgeSeconds = 120,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = false
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance);

        var cycle = await service.RunOnceAsync();

        var candidate = await dbContext.MarketScannerCandidates.SingleAsync(entity => entity.ScanCycleId == cycle.Id);

        Assert.Equal(1, cycle.ScannedSymbolCount);
        Assert.Equal(1, cycle.EligibleCandidateCount);
        Assert.Equal("BTCUSDT", cycle.BestCandidateSymbol);
        Assert.True(candidate.IsEligible);
        Assert.Null(candidate.RejectionReason);
        Assert.Equal(100m, candidate.LastPrice);
        Assert.Equal(1_000_000m, candidate.QuoteVolume24h);
    }

    [Fact]
    public async Task RunOnceAsync_ReactivatesRecoverableDeletedHistoricalWindow_WhenRestRecoveryIsUnavailable()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        var deletedCandle = new HistoricalMarketCandle
        {
            Id = Guid.NewGuid(),
            Symbol = "BTCUSDT",
            Interval = "1m",
            OpenTimeUtc = nowUtc.UtcDateTime.AddMinutes(-1),
            CloseTimeUtc = nowUtc.UtcDateTime,
            OpenPrice = 100m,
            HighPrice = 100m,
            LowPrice = 100m,
            ClosePrice = 100m,
            Volume = 1_000m,
            ReceivedAtUtc = nowUtc.UtcDateTime,
            Source = "unit-test"
        };
        dbContext.HistoricalMarketCandles.Add(deletedCandle);
        await dbContext.SaveChangesAsync();
        deletedCandle.IsDeleted = true;
        await dbContext.SaveChangesAsync();

        var marketDataService = new FakeMarketDataService();
        marketDataService.SetLatestPrice("BTCUSDT", 100m, nowUtc.UtcDateTime);
        marketDataService.SetLatestKline(
            "BTCUSDT",
            "1m",
            new MarketCandleSnapshot(
                "BTCUSDT",
                "1m",
                nowUtc.UtcDateTime.AddMinutes(-1),
                nowUtc.UtcDateTime,
                100m,
                100m,
                100m,
                100m,
                1_000m,
                true,
                nowUtc.UtcDateTime,
                "unit-test-shared"),
            SharedMarketDataCacheReadStatus.HitFresh);

        var service = new MarketScannerService(
            dbContext,
            marketDataService,
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 1,
                MaxUniverseSymbols = 10,
                Min24hQuoteVolume = 100m,
                MaxDataAgeSeconds = 120,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = false
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance);

        var cycle = await service.RunOnceAsync();

        var candidate = await dbContext.MarketScannerCandidates.SingleAsync(entity => entity.ScanCycleId == cycle.Id);
        var restoredCandle = await dbContext.HistoricalMarketCandles
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.Id == deletedCandle.Id);

        Assert.True(candidate.IsEligible);
        Assert.Equal(100_000m, candidate.QuoteVolume24h);
        Assert.False(restoredCandle.IsDeleted);
        Assert.Contains("HistoricalCandlesDb.Reactivated", candidate.ScoringSummary ?? string.Empty, StringComparison.Ordinal);
    }
    [Fact]
    public async Task RunOnceAsync_RejectsDeletedHistoricalWindow_WhenSharedCandleExistsButHistoricalWindowMissing()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();

        var marketDataService = new FakeMarketDataService();
        marketDataService.SetLatestPrice("BTCUSDT", 100m, nowUtc.UtcDateTime);
        marketDataService.SetLatestKline(
            "BTCUSDT",
            "1m",
            new MarketCandleSnapshot(
                "BTCUSDT",
                "1m",
                nowUtc.UtcDateTime.AddMinutes(-1),
                nowUtc.UtcDateTime,
                100m,
                100m,
                100m,
                100m,
                10_000m,
                true,
                nowUtc.UtcDateTime,
                "unit-test-shared"),
            SharedMarketDataCacheReadStatus.HitFresh);

        var service = new MarketScannerService(
            dbContext,
            marketDataService,
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 2,
                MaxUniverseSymbols = 10,
                Min24hQuoteVolume = 1_000m,
                MaxDataAgeSeconds = 120,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = false
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance);

        var cycle = await service.RunOnceAsync();

        var candidate = await dbContext.MarketScannerCandidates.SingleAsync(entity => entity.ScanCycleId == cycle.Id);
        Assert.False(candidate.IsEligible);
        Assert.Equal("DeletedHistoricalWindow", candidate.RejectionReason);
        Assert.Null(candidate.QuoteVolume24h);
    }

    [Fact]
    public async Task RunOnceAsync_RecoversHistoricalParityLag_FromRestBackfill_AndPersistsRecoveredCandles()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime.AddMinutes(-30), closePrice: 100m, volume: 1_000m);
        await dbContext.SaveChangesAsync();
        var historicalCountBefore = await dbContext.HistoricalMarketCandles.CountAsync(entity => entity.Symbol == "BTCUSDT");

        var marketDataService = new FakeMarketDataService();
        marketDataService.SetLatestPrice("BTCUSDT", 100m, nowUtc.UtcDateTime);
        marketDataService.SetLatestKline(
            "BTCUSDT",
            "1m",
            new MarketCandleSnapshot(
                "BTCUSDT",
                "1m",
                nowUtc.UtcDateTime.AddMinutes(-1),
                nowUtc.UtcDateTime,
                100m,
                100m,
                100m,
                100m,
                1_500m,
                true,
                nowUtc.UtcDateTime,
                "unit-test-shared"),
            SharedMarketDataCacheReadStatus.HitFresh);

        var historicalClient = new FakeHistoricalKlineClient();
        historicalClient.SetCandles(
            "BTCUSDT",
            CreateClosedCandles("BTCUSDT", nowUtc.UtcDateTime.AddMinutes(-9), 10, 100m, 2_000m, "unit-test-rest"));

        var service = new MarketScannerService(
            dbContext,
            marketDataService,
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 2,
                MaxUniverseSymbols = 10,
                Min24hQuoteVolume = 1_000m,
                MaxDataAgeSeconds = 120,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = false
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance,
            historicalKlineClient: historicalClient);

        var cycle = await service.RunOnceAsync();

        var candidate = await dbContext.MarketScannerCandidates.SingleAsync(entity => entity.ScanCycleId == cycle.Id);
        var historicalCountAfter = await dbContext.HistoricalMarketCandles.CountAsync(entity => entity.Symbol == "BTCUSDT");

        Assert.True(candidate.IsEligible);
        Assert.NotNull(candidate.QuoteVolume24h);
        Assert.Equal(nowUtc.UtcDateTime, candidate.LastCandleAtUtc);
        Assert.True(historicalCountAfter > historicalCountBefore);
        Assert.Contains("Binance.Rest.KlineRecovery", candidate.ScoringSummary ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnceAsync_ReactivatesSoftDeletedRecoveredHistoricalCandle_InsteadOfInsertingDuplicate()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        var softDeletedOpenTimeUtc = nowUtc.UtcDateTime.AddMinutes(-1);
        var softDeletedCandle = new HistoricalMarketCandle
        {
            Id = Guid.NewGuid(),
            Symbol = "BTCUSDT",
            Interval = "1m",
            OpenTimeUtc = softDeletedOpenTimeUtc,
            CloseTimeUtc = nowUtc.UtcDateTime,
            OpenPrice = 1m,
            HighPrice = 1m,
            LowPrice = 1m,
            ClosePrice = 1m,
            Volume = 1m,
            ReceivedAtUtc = nowUtc.UtcDateTime,
            Source = "deleted-seed"
        };
        dbContext.HistoricalMarketCandles.Add(softDeletedCandle);
        await dbContext.SaveChangesAsync();
        softDeletedCandle.IsDeleted = true;
        await dbContext.SaveChangesAsync();

        var marketDataService = new FakeMarketDataService();
        marketDataService.SetLatestPrice("BTCUSDT", 100m, nowUtc.UtcDateTime);
        marketDataService.SetLatestKline(
            "BTCUSDT",
            "1m",
            new MarketCandleSnapshot(
                "BTCUSDT",
                "1m",
                softDeletedOpenTimeUtc,
                nowUtc.UtcDateTime,
                100m,
                100m,
                100m,
                100m,
                1_500m,
                true,
                nowUtc.UtcDateTime,
                "unit-test-shared"),
            SharedMarketDataCacheReadStatus.HitFresh);

        var historicalClient = new FakeHistoricalKlineClient();
        historicalClient.SetCandles(
            "BTCUSDT",
            [
                new MarketCandleSnapshot(
                    "BTCUSDT",
                    "1m",
                    nowUtc.UtcDateTime.AddMinutes(-2),
                    softDeletedOpenTimeUtc,
                    100m,
                    100m,
                    100m,
                    100m,
                    2_000m,
                    true,
                    softDeletedOpenTimeUtc,
                    "unit-test-rest")
            ]);

        var service = new MarketScannerService(
            dbContext,
            marketDataService,
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 1,
                MaxUniverseSymbols = 10,
                VolumeLookbackHours = 1,
                Min24hQuoteVolume = 100m,
                MaxDataAgeSeconds = 120,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = false
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance,
            historicalKlineClient: historicalClient);

        var cycle = await service.RunOnceAsync();

        var candidate = await dbContext.MarketScannerCandidates.SingleAsync(entity => entity.ScanCycleId == cycle.Id);
        var recoveredRows = await dbContext.HistoricalMarketCandles
            .IgnoreQueryFilters()
            .Where(entity => entity.Symbol == "BTCUSDT" && entity.Interval == "1m" && entity.OpenTimeUtc == softDeletedOpenTimeUtc)
            .ToListAsync();
        var recoveredCandle = Assert.Single(recoveredRows);

        Assert.True(candidate.IsEligible);
        Assert.NotNull(candidate.QuoteVolume24h);
        Assert.Equal(softDeletedCandle.Id, recoveredCandle.Id);
        Assert.False(recoveredCandle.IsDeleted);
        Assert.Equal("unit-test-shared", recoveredCandle.Source);
        Assert.Equal(100m, recoveredCandle.ClosePrice);
    }

    [Fact]
    public async Task RunOnceAsync_ClampsMarketScoreAndCompositeScore_WhenQuoteVolumeGreatlyExceedsThreshold()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 1_000_000m, volume: 1_000_000m);
        await dbContext.SaveChangesAsync();

        var marketDataService = new FakeMarketDataService();
        marketDataService.SetLatestPrice("BTCUSDT", 1_000_000m, nowUtc.UtcDateTime);

        var service = new MarketScannerService(
            dbContext,
            marketDataService,
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 1,
                MaxUniverseSymbols = 10,
                Min24hQuoteVolume = 1_000m,
                MaxDataAgeSeconds = 120,
                StrategyScoreWeight = 2m,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = false
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance);

        var cycle = await service.RunOnceAsync();
        var candidate = await dbContext.MarketScannerCandidates.SingleAsync(entity => entity.ScanCycleId == cycle.Id);

        Assert.Equal(79_228_162_514.264337593543950335m, candidate.QuoteVolume24h);
        Assert.Equal(100m, candidate.MarketScore);
        Assert.Equal(33.3333m, candidate.Score);
    }

    [Fact]
    public async Task RunOnceAsync_AllowsRawQuoteVolumeDiagnosticPersistence_WhenOnlyDiagnosticScaleExceedsGuardEnvelope()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 0.333333333333333333m, volume: 3_000_000m);
        await dbContext.SaveChangesAsync();

        var marketDataService = new FakeMarketDataService();
        marketDataService.SetLatestPrice("BTCUSDT", 0.333333333333333333m, nowUtc.UtcDateTime);

        var service = new MarketScannerService(
            dbContext,
            marketDataService,
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 1,
                MaxUniverseSymbols = 10,
                Min24hQuoteVolume = 100m,
                MaxDataAgeSeconds = 120,
                StrategyScoreWeight = 2m,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = false
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance);

        var cycle = await service.RunOnceAsync();
        var candidate = await dbContext.MarketScannerCandidates.SingleAsync(entity => entity.ScanCycleId == cycle.Id);

        Assert.True(candidate.IsEligible);
        Assert.True(candidate.QuoteVolume24h.HasValue);
        Assert.InRange(candidate.MarketScore, 0m, 100m);
        Assert.InRange(candidate.Score, 0m, 100m);
    }

    [Fact]
    public async Task RunOnceAsync_RejectsDisabledUnsupportedStaleAndMissingMarketData_FailClosed()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        SeedCandles(dbContext, "STALEUSDT", nowUtc.UtcDateTime.AddMinutes(-10), closePrice: 10m, volume: 100_000m);
        SeedCandles(dbContext, "XRPBTC", nowUtc.UtcDateTime, closePrice: 0.00002m, volume: 100_000m);
        SeedCandles(dbContext, "HALTUSDT", nowUtc.UtcDateTime, closePrice: 1m, volume: 100_000m);
        await dbContext.SaveChangesAsync();

        var service = new MarketScannerService(
            dbContext,
            new FakeMarketDataService(),
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("HALTUSDT", "Binance", "HALT", "USDT", 0.1m, 1m, "BREAK", false, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 5,
                MaxUniverseSymbols = 50,
                Min24hQuoteVolume = 100m,
                MaxDataAgeSeconds = 120,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = false
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["STALEUSDT", "XRPBTC", "HALTUSDT", "MISSINGUSDT", " "] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance);

        var cycle = await service.RunOnceAsync();
        var candidates = await dbContext.MarketScannerCandidates
            .Where(entity => entity.ScanCycleId == cycle.Id)
            .ToDictionaryAsync(entity => entity.Symbol);

        Assert.Equal("StaleMarketData", candidates["STALEUSDT"].RejectionReason);
        Assert.Equal("QuoteAssetNotAllowed", candidates["XRPBTC"].RejectionReason);
        Assert.Equal("SymbolTradingDisabled", candidates["HALTUSDT"].RejectionReason);
        Assert.Equal("MissingLastPrice", candidates["MISSINGUSDT"].RejectionReason);
        Assert.All(candidates.Values, candidate => Assert.False(candidate.IsTopCandidate));
    }

    [Fact]
    public async Task RunOnceAsync_UsesSharedKlineFreshnessBeforeHistoricalFallback_WhenHistoricalCandlesLag()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime.AddMinutes(-5), closePrice: 100m, volume: 1_000m);
        await dbContext.SaveChangesAsync();

        var marketDataService = new FakeMarketDataService();
        marketDataService.SetLatestPrice("BTCUSDT", 101m, nowUtc.UtcDateTime);
        marketDataService.SetLatestKline(
            "BTCUSDT",
            "1m",
            new MarketCandleSnapshot(
                "BTCUSDT",
                "1m",
                nowUtc.UtcDateTime.AddMinutes(-1),
                nowUtc.UtcDateTime.AddSeconds(-1),
                100m,
                101m,
                99m,
                101m,
                50m,
                true,
                nowUtc.UtcDateTime,
                "shared-kline-cache"),
            SharedMarketDataCacheReadStatus.HitFresh);

        var service = new MarketScannerService(
            dbContext,
            marketDataService,
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 1,
                MaxUniverseSymbols = 10,
                Min24hQuoteVolume = 100m,
                MaxDataAgeSeconds = 120,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = false
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance);

        var cycle = await service.RunOnceAsync();

        var candidate = await dbContext.MarketScannerCandidates.SingleAsync(entity => entity.ScanCycleId == cycle.Id);

        Assert.True(candidate.IsEligible);
        Assert.Equal(nowUtc.UtcDateTime.AddSeconds(-1), candidate.LastCandleAtUtc);
        Assert.Equal(101m, candidate.LastPrice);
        Assert.Equal(1, marketDataService.SharedKlineReadCount);
        Assert.Equal(1, marketDataService.SharedPriceReadCount);
    }

    [Fact]
    public async Task RunOnceAsync_PausesScannerWhenAllSymbolsAreStale_InsteadOfPersistingRejectStorm()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime.AddMinutes(-5), closePrice: 100m, volume: 1_000m);
        SeedCandles(dbContext, "ETHUSDT", nowUtc.UtcDateTime.AddMinutes(-5), closePrice: 100m, volume: 1_000m);
        SeedCandles(dbContext, "SOLUSDT", nowUtc.UtcDateTime.AddMinutes(-5), closePrice: 20m, volume: 10_000m);
        await dbContext.SaveChangesAsync();

        var marketDataService = new FakeMarketDataService();
        marketDataService.SetLatestPrice("BTCUSDT", 100m, nowUtc.UtcDateTime);
        marketDataService.SetLatestPrice("ETHUSDT", 100m, nowUtc.UtcDateTime);
        marketDataService.SetLatestPrice("SOLUSDT", 20m, nowUtc.UtcDateTime);

        var service = new MarketScannerService(
            dbContext,
            marketDataService,
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime),
                new SymbolMetadataSnapshot("ETHUSDT", "Binance", "ETH", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime),
                new SymbolMetadataSnapshot("SOLUSDT", "Binance", "SOL", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 3,
                MaxUniverseSymbols = 10,
                Min24hQuoteVolume = 100m,
                MaxDataAgeSeconds = 120,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = false
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT", "ETHUSDT", "SOLUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance);

        var cycle = await service.RunOnceAsync();
        var heartbeat = await dbContext.WorkerHeartbeats.SingleAsync(entity => entity.WorkerKey == MarketScannerService.WorkerKey);

        Assert.Equal(3, cycle.ScannedSymbolCount);
        Assert.Equal(0, cycle.EligibleCandidateCount);
        Assert.Contains("Market scanner paused because fresh candle data is unavailable", cycle.Summary, StringComparison.Ordinal);
        Assert.Empty(await dbContext.MarketScannerCandidates.Where(entity => entity.ScanCycleId == cycle.Id).ToListAsync());
        Assert.Equal("ScannerFreshnessPaused", heartbeat.LastErrorCode);
        Assert.Equal(MonitoringFreshnessTier.Stale, heartbeat.FreshnessTier);
        Assert.Equal(CircuitBreakerStateCode.Cooldown, heartbeat.CircuitBreakerState);
    }

    [Fact]
    public async Task RunOnceAsync_EnrichesNoEligibleHeartbeatAndCycleSummary_WithRejectionBreakdown()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        await using var dbContext = CreateDbContext();
        SeedCandles(dbContext, "ETHUSDT", nowUtc.UtcDateTime, closePrice: 1m, volume: 1m);
        await dbContext.SaveChangesAsync();

        var marketDataService = new FakeMarketDataService();
        marketDataService.SetLatestPrice("BTCUSDT", 100m, nowUtc.UtcDateTime);
        marketDataService.SetLatestPrice("ETHUSDT", 100m, nowUtc.UtcDateTime);

        var service = new MarketScannerService(
            dbContext,
            marketDataService,
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime),
                new SymbolMetadataSnapshot("ETHUSDT", "Binance", "ETH", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 2,
                MaxUniverseSymbols = 10,
                Min24hQuoteVolume = 100m,
                MaxDataAgeSeconds = 120,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = false
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT", "ETHUSDT"] }),
            new AdjustableTimeProvider(nowUtc),
            NullLogger<MarketScannerService>.Instance);

        var cycle = await service.RunOnceAsync();
        var heartbeat = await dbContext.WorkerHeartbeats.SingleAsync(entity => entity.WorkerKey == MarketScannerService.WorkerKey);

        Assert.Equal(2, cycle.ScannedSymbolCount);
        Assert.Equal(0, cycle.EligibleCandidateCount);
        Assert.Contains("QuoteVolume24hMissing:1 [BTCUSDT]", cycle.Summary ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("LowQuoteVolume:1 [ETHUSDT]", cycle.Summary ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("LowQuoteVolume", heartbeat.LastErrorCode);
        Assert.Contains("QuoteVolume24hMissing:1 [BTCUSDT]", heartbeat.LastErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("LowQuoteVolume:1 [ETHUSDT]", heartbeat.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnceAsync_UsesStableSymbolTieBreak_WhenScoresAreEqual()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        await using var dbContext = CreateDbContext();
        SeedCandles(dbContext, "ETHUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        await dbContext.SaveChangesAsync();

        var service = new MarketScannerService(
            dbContext,
            new FakeMarketDataService(),
            new FakeSharedSymbolRegistry([]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 2,
                MaxUniverseSymbols = 50,
                Min24hQuoteVolume = 10m,
                MaxDataAgeSeconds = 120,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = false
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["ETHUSDT", "BTCUSDT"] }),
            new AdjustableTimeProvider(nowUtc),
            NullLogger<MarketScannerService>.Instance);

        var cycle = await service.RunOnceAsync();
        var rankedSymbols = await dbContext.MarketScannerCandidates
            .Where(entity => entity.ScanCycleId == cycle.Id && entity.IsEligible)
            .OrderBy(entity => entity.Rank)
            .Select(entity => entity.Symbol)
            .ToListAsync();

        Assert.Equal(["BTCUSDT", "ETHUSDT"], rankedSymbols);
    }

    [Fact]
    public async Task RunOnceAsync_UsesClassicalRankingWhenAiRankingIsDisabled()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        var btcStrategy = await SeedStrategyGraphAsync(dbContext, "user-ai-btc", "BTCUSDT", "scanner-ai-btc", "{}");
        var ethStrategy = await SeedStrategyGraphAsync(dbContext, "user-ai-eth", "ETHUSDT", "scanner-ai-eth", "{}");
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 99m, volume: 1_000m);
        SeedCandles(dbContext, "ETHUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        await dbContext.SaveChangesAsync();

        var indicatorDataService = new FakeIndicatorDataService();
        indicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", nowUtc.UtcDateTime, macdLine: 1m, macdSignalLine: 0.8m, macdHistogram: 0.2m, middleBand: 100m, upperBand: 102m, lowerBand: 98m));
        indicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("ETHUSDT", "1m", nowUtc.UtcDateTime, middleBand: 99m, upperBand: 101m, lowerBand: 97m));

        var strategyEvaluatorService = new FakeStrategyEvaluatorService();
        strategyEvaluatorService.SetReport("BTCUSDT", CreateEvaluationReport(btcStrategy.TradingStrategyId, btcStrategy.TradingStrategyVersionId, "scanner-ai-btc", "BTCUSDT", "1m", nowUtc.UtcDateTime, 95, "BTC classical winner."));
        strategyEvaluatorService.SetReport("ETHUSDT", CreateEvaluationReport(ethStrategy.TradingStrategyId, ethStrategy.TradingStrategyVersionId, "scanner-ai-eth", "ETHUSDT", "1m", nowUtc.UtcDateTime, 20, "ETH advisory winner."));

        var service = new MarketScannerService(
            dbContext,
            new FakeMarketDataService(),
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime),
                new SymbolMetadataSnapshot("ETHUSDT", "Binance", "ETH", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 2,
                MaxUniverseSymbols = 20,
                Min24hQuoteVolume = 1_000m,
                MaxDataAgeSeconds = 120,
                StrategyScoreWeight = 2m,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = true
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT", "ETHUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance,
            handoffService: null,
            indicatorDataService,
            strategyEvaluatorService,
            Options.Create(new BotExecutionPilotOptions { SignalEvaluationMode = ExecutionEnvironment.Live }));

        var cycle = await service.RunOnceAsync();
        var rankedCandidates = await dbContext.MarketScannerCandidates
            .Where(entity => entity.ScanCycleId == cycle.Id && entity.IsEligible)
            .OrderBy(entity => entity.Rank)
            .ToListAsync();

        Assert.Equal(["BTCUSDT", "ETHUSDT"], rankedCandidates.Select(candidate => candidate.Symbol).ToArray());
        Assert.Equal(96.6667m, rankedCandidates[0].Score);
        Assert.Equal(46.6667m, rankedCandidates[1].Score);
        Assert.Contains("ScannerRankingMode=Disabled", rankedCandidates[0].ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("ScannerClassicalScore=96.6667", rankedCandidates[0].ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("ScannerCombinedScore=96.6667", rankedCandidates[0].ScoringSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnceAsync_FallsBackToClassicalWhenAiScoreIsMissing()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        var strategy = await SeedStrategyGraphAsync(dbContext, "user-ai-fallback", "BTCUSDT", "scanner-ai-fallback", "{}");
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        await dbContext.SaveChangesAsync();

        var indicatorDataService = new FakeIndicatorDataService();
        indicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", nowUtc.UtcDateTime, macdLine: 0.5m, macdSignalLine: 0.5m, macdHistogram: 0m, middleBand: 100m, upperBand: 110m, lowerBand: 90m));

        var strategyEvaluatorService = new FakeStrategyEvaluatorService();
        strategyEvaluatorService.SetReport("BTCUSDT", CreateEvaluationReport(strategy.TradingStrategyId, strategy.TradingStrategyVersionId, "scanner-ai-fallback", "BTCUSDT", "1m", nowUtc.UtcDateTime, 60, "Fallback to classical."));

        var service = new MarketScannerService(
            dbContext,
            new FakeMarketDataService(),
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 1,
                MaxUniverseSymbols = 20,
                Min24hQuoteVolume = 1_000m,
                MaxDataAgeSeconds = 120,
                StrategyScoreWeight = 2m,
                AiAssistedRankingEnabled = true,
                AiAssistedRankingWeight = 0.5m,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = true
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance,
            handoffService: null,
            indicatorDataService,
            strategyEvaluatorService,
            Options.Create(new BotExecutionPilotOptions { SignalEvaluationMode = ExecutionEnvironment.Live }));

        var cycle = await service.RunOnceAsync();
        var candidate = await dbContext.MarketScannerCandidates.SingleAsync(entity => entity.ScanCycleId == cycle.Id);

        Assert.True(candidate.IsEligible);
        Assert.Equal(73.3333m, candidate.Score);
        Assert.DoesNotContain("ScannerShadowScore=", candidate.ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("ScannerRankingMode=ClassicalFallback", candidate.ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("ScannerClassicalScore=73.3333", candidate.ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("ScannerCombinedScore=73.3333", candidate.ScoringSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnceAsync_CombinesClassicalAndAiScore_WhenAiRankingEnabled()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        var btcStrategy = await SeedStrategyGraphAsync(dbContext, "user-ai-rank-btc", "BTCUSDT", "scanner-ai-rank-btc", "{}");
        var ethStrategy = await SeedStrategyGraphAsync(dbContext, "user-ai-rank-eth", "ETHUSDT", "scanner-ai-rank-eth", "{}");
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 99m, volume: 1_000m);
        SeedCandles(dbContext, "ETHUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        await dbContext.SaveChangesAsync();

        var indicatorDataService = new FakeIndicatorDataService();
        indicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", nowUtc.UtcDateTime, macdLine: 1m, macdSignalLine: 0.8m, macdHistogram: 0.2m, middleBand: 100m, upperBand: 102m, lowerBand: 98m));
        indicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("ETHUSDT", "1m", nowUtc.UtcDateTime, middleBand: 99m, upperBand: 101m, lowerBand: 97m));

        var strategyEvaluatorService = new FakeStrategyEvaluatorService();
        strategyEvaluatorService.SetReport("BTCUSDT", CreateEvaluationReport(btcStrategy.TradingStrategyId, btcStrategy.TradingStrategyVersionId, "scanner-ai-rank-btc", "BTCUSDT", "1m", nowUtc.UtcDateTime, 95, "BTC classical winner."));
        strategyEvaluatorService.SetReport("ETHUSDT", CreateEvaluationReport(ethStrategy.TradingStrategyId, ethStrategy.TradingStrategyVersionId, "scanner-ai-rank-eth", "ETHUSDT", "1m", nowUtc.UtcDateTime, 20, "ETH advisory winner."));

        var service = new MarketScannerService(
            dbContext,
            new FakeMarketDataService(),
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime),
                new SymbolMetadataSnapshot("ETHUSDT", "Binance", "ETH", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 2,
                MaxUniverseSymbols = 20,
                Min24hQuoteVolume = 1_000m,
                MaxDataAgeSeconds = 120,
                StrategyScoreWeight = 2m,
                AiAssistedRankingEnabled = true,
                AiAssistedRankingWeight = 0.5m,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = true
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT", "ETHUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance,
            handoffService: null,
            indicatorDataService,
            strategyEvaluatorService,
            Options.Create(new BotExecutionPilotOptions { SignalEvaluationMode = ExecutionEnvironment.Live }));

        var cycle = await service.RunOnceAsync();
        var rankedCandidates = await dbContext.MarketScannerCandidates
            .Where(entity => entity.ScanCycleId == cycle.Id && entity.IsEligible)
            .OrderBy(entity => entity.Rank)
            .ToListAsync();

        Assert.Equal(["ETHUSDT", "BTCUSDT"], rankedCandidates.Select(candidate => candidate.Symbol).ToArray());
        Assert.Equal(63.3334m, rankedCandidates[0].Score);
        Assert.Equal(60.8334m, rankedCandidates[1].Score);
        Assert.Contains("ScannerRankingMode=AdvisoryCombined", rankedCandidates[0].ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("ScannerClassicalScore=46.6667", rankedCandidates[0].ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("ScannerShadowScore=80", rankedCandidates[0].ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("ScannerCombinedScore=63.3334", rankedCandidates[0].ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("ScannerShadowContributions=CompressionBreakoutSetupDetected:+25,TrendBreakoutConfirmed:+55", rankedCandidates[0].ScoringSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnceAsync_DoesNotOverrideIneligibleCandidate_WhenAdvisoryScoreIsPresent()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        var btcStrategy = await SeedStrategyGraphAsync(dbContext, "user-ai-ineligible-btc", "BTCUSDT", "scanner-ai-ineligible-btc", "{}");
        var ethStrategy = await SeedStrategyGraphAsync(dbContext, "user-ai-ineligible-eth", "ETHUSDT", "scanner-ai-ineligible-eth", "{}");
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        SeedCandles(dbContext, "ETHUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        await dbContext.SaveChangesAsync();

        var indicatorDataService = new FakeIndicatorDataService();
        indicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", nowUtc.UtcDateTime, middleBand: 99m, upperBand: 101m, lowerBand: 97m));
        indicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("ETHUSDT", "1m", nowUtc.UtcDateTime, macdLine: 0.5m, macdSignalLine: 0.5m, macdHistogram: 0m, middleBand: 100m, upperBand: 110m, lowerBand: 90m));

        var strategyEvaluatorService = new FakeStrategyEvaluatorService();
        strategyEvaluatorService.SetReport("BTCUSDT", CreateEvaluationReport(
            btcStrategy.TradingStrategyId,
            btcStrategy.TradingStrategyVersionId,
            "scanner-ai-ineligible-btc",
            "BTCUSDT",
            "1m",
            nowUtc.UtcDateTime,
            95,
            "Entry not matched.",
            outcome: "NoSignalCandidate",
            ruleEvaluation: new StrategyEvaluationResult(
                HasEntryRules: true,
                EntryMatched: false,
                HasExitRules: false,
                ExitMatched: false,
                HasRiskRules: true,
                RiskPassed: true,
                EntryRuleResult: null,
                ExitRuleResult: null,
                RiskRuleResult: null)));
        strategyEvaluatorService.SetReport("ETHUSDT", CreateEvaluationReport(ethStrategy.TradingStrategyId, ethStrategy.TradingStrategyVersionId, "scanner-ai-ineligible-eth", "ETHUSDT", "1m", nowUtc.UtcDateTime, 20, "ETH eligible."));

        var service = new MarketScannerService(
            dbContext,
            new FakeMarketDataService(),
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime),
                new SymbolMetadataSnapshot("ETHUSDT", "Binance", "ETH", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 2,
                MaxUniverseSymbols = 20,
                Min24hQuoteVolume = 1_000m,
                MaxDataAgeSeconds = 120,
                StrategyScoreWeight = 2m,
                AiAssistedRankingEnabled = true,
                AiAssistedRankingWeight = 0.5m,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = true
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT", "ETHUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance,
            handoffService: null,
            indicatorDataService,
            strategyEvaluatorService,
            Options.Create(new BotExecutionPilotOptions { SignalEvaluationMode = ExecutionEnvironment.Live }));

        var cycle = await service.RunOnceAsync();
        var candidates = await dbContext.MarketScannerCandidates
            .Where(entity => entity.ScanCycleId == cycle.Id)
            .OrderBy(entity => entity.Symbol)
            .ToListAsync();

        var btc = Assert.Single(candidates, candidate => candidate.Symbol == "BTCUSDT");
        var eth = Assert.Single(candidates, candidate => candidate.Symbol == "ETHUSDT");

        Assert.False(btc.IsEligible);
        Assert.Equal(0m, btc.Score);
        Assert.StartsWith("StrategyNoSignal", btc.RejectionReason, StringComparison.Ordinal);
        Assert.Contains("ScannerShadowScore=80", btc.ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("ScannerRankingMode=NotRanked", btc.ScoringSummary, StringComparison.Ordinal);
        Assert.True(eth.IsEligible);
        Assert.Equal(1, eth.Rank);
    }

    [Fact]
    public async Task RunOnceAsync_DoesNotCreateExecutionDecision_WhenAiRankingChangesOrder()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        var btcStrategy = await SeedStrategyGraphAsync(dbContext, "user-ai-noexec-btc", "BTCUSDT", "scanner-ai-noexec-btc", "{}");
        var ethStrategy = await SeedStrategyGraphAsync(dbContext, "user-ai-noexec-eth", "ETHUSDT", "scanner-ai-noexec-eth", "{}");
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 99m, volume: 1_000m);
        SeedCandles(dbContext, "ETHUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        await dbContext.SaveChangesAsync();

        var indicatorDataService = new FakeIndicatorDataService();
        indicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", nowUtc.UtcDateTime, macdLine: 1m, macdSignalLine: 0.8m, macdHistogram: 0.2m, middleBand: 100m, upperBand: 102m, lowerBand: 98m));
        indicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("ETHUSDT", "1m", nowUtc.UtcDateTime, middleBand: 99m, upperBand: 101m, lowerBand: 97m));

        var strategyEvaluatorService = new FakeStrategyEvaluatorService();
        strategyEvaluatorService.SetReport("BTCUSDT", CreateEvaluationReport(btcStrategy.TradingStrategyId, btcStrategy.TradingStrategyVersionId, "scanner-ai-noexec-btc", "BTCUSDT", "1m", nowUtc.UtcDateTime, 95, "BTC classical winner."));
        strategyEvaluatorService.SetReport("ETHUSDT", CreateEvaluationReport(ethStrategy.TradingStrategyId, ethStrategy.TradingStrategyVersionId, "scanner-ai-noexec-eth", "ETHUSDT", "1m", nowUtc.UtcDateTime, 20, "ETH advisory winner."));

        var service = new MarketScannerService(
            dbContext,
            new FakeMarketDataService(),
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime),
                new SymbolMetadataSnapshot("ETHUSDT", "Binance", "ETH", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 2,
                MaxUniverseSymbols = 20,
                Min24hQuoteVolume = 1_000m,
                MaxDataAgeSeconds = 120,
                StrategyScoreWeight = 2m,
                AiAssistedRankingEnabled = true,
                AiAssistedRankingWeight = 0.5m,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = true
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT", "ETHUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance,
            handoffService: null,
            indicatorDataService,
            strategyEvaluatorService,
            Options.Create(new BotExecutionPilotOptions { SignalEvaluationMode = ExecutionEnvironment.Live }));

        _ = await service.RunOnceAsync();

        Assert.Empty(dbContext.MarketScannerHandoffAttempts);
        Assert.Empty(dbContext.ExecutionOrders);
    }

    [Fact]
    public async Task RunOnceAsync_SuppressesLowQualitySetup_WhenAdaptiveFilteringIsEnabled()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        var strategy = await SeedStrategyGraphAsync(dbContext, "user-ai-suppress", "BTCUSDT", "scanner-ai-suppress", "{}");
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 99m, volume: 1_000m);
        await dbContext.SaveChangesAsync();

        var indicatorDataService = new FakeIndicatorDataService();
        indicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", nowUtc.UtcDateTime, macdLine: 1m, macdSignalLine: 0.8m, macdHistogram: 0.2m, middleBand: 100m, upperBand: 102m, lowerBand: 98m));

        var strategyEvaluatorService = new FakeStrategyEvaluatorService();
        strategyEvaluatorService.SetReport("BTCUSDT", CreateEvaluationReport(strategy.TradingStrategyId, strategy.TradingStrategyVersionId, "scanner-ai-suppress", "BTCUSDT", "1m", nowUtc.UtcDateTime, 20, "Suppression candidate."));

        var service = new MarketScannerService(
            dbContext,
            new FakeMarketDataService(),
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 1,
                MaxUniverseSymbols = 20,
                Min24hQuoteVolume = 1_000m,
                MaxDataAgeSeconds = 120,
                StrategyScoreWeight = 2m,
                AdaptiveFilteringEnabled = true,
                AdaptiveFilteringMaxAdvisoryScore = 30,
                AdaptiveFilteringMaxClassicalScore = 60m,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = true
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance,
            handoffService: null,
            indicatorDataService,
            strategyEvaluatorService,
            Options.Create(new BotExecutionPilotOptions { SignalEvaluationMode = ExecutionEnvironment.Live }));

        var cycle = await service.RunOnceAsync();
        var candidate = await dbContext.MarketScannerCandidates.SingleAsync(entity => entity.ScanCycleId == cycle.Id);

        Assert.False(candidate.IsEligible);
        Assert.Equal("AdaptiveFilterLowQualitySetup", candidate.RejectionReason);
        Assert.Equal(0m, candidate.Score);
        Assert.Contains("ScannerAdaptiveFilterState=Suppressed", candidate.ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("ScannerAdaptiveFilterReason=LowAdvisoryScoreAndWeakClassicalScore", candidate.ScoringSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnceAsync_DoesNotSuppress_WhenConfidenceIsInsufficient()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        var strategy = await SeedStrategyGraphAsync(dbContext, "user-ai-nosuppress", "BTCUSDT", "scanner-ai-nosuppress", "{}");
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        await dbContext.SaveChangesAsync();

        var indicatorDataService = new FakeIndicatorDataService();
        indicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", nowUtc.UtcDateTime, macdLine: 0.5m, macdSignalLine: 0.5m, macdHistogram: 0m, middleBand: 100m, upperBand: 110m, lowerBand: 90m));

        var strategyEvaluatorService = new FakeStrategyEvaluatorService();
        strategyEvaluatorService.SetReport("BTCUSDT", CreateEvaluationReport(strategy.TradingStrategyId, strategy.TradingStrategyVersionId, "scanner-ai-nosuppress", "BTCUSDT", "1m", nowUtc.UtcDateTime, 20, "No suppression without advisory confidence."));

        var service = new MarketScannerService(
            dbContext,
            new FakeMarketDataService(),
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 1,
                MaxUniverseSymbols = 20,
                Min24hQuoteVolume = 1_000m,
                MaxDataAgeSeconds = 120,
                StrategyScoreWeight = 2m,
                AdaptiveFilteringEnabled = true,
                AdaptiveFilteringMaxAdvisoryScore = 30,
                AdaptiveFilteringMaxClassicalScore = 60m,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = true
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance,
            handoffService: null,
            indicatorDataService,
            strategyEvaluatorService,
            Options.Create(new BotExecutionPilotOptions { SignalEvaluationMode = ExecutionEnvironment.Live }));

        var cycle = await service.RunOnceAsync();
        var candidate = await dbContext.MarketScannerCandidates.SingleAsync(entity => entity.ScanCycleId == cycle.Id);

        Assert.True(candidate.IsEligible);
        Assert.Null(candidate.RejectionReason);
        Assert.Contains("ScannerAdaptiveFilterState=Passed", candidate.ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("ScannerAdaptiveFilterReason=ConfidenceInsufficient", candidate.ScoringSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnceAsync_ThrowsScannerPoisonedCandleAuditException_AndSoftDeletesInvalidCandles()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 0m, volume: 10m);
        await dbContext.SaveChangesAsync();

        var marketDataService = new FakeMarketDataService();
        marketDataService.SetLatestPrice("BTCUSDT", 100m, nowUtc.UtcDateTime);

        var service = new MarketScannerService(
            dbContext,
            marketDataService,
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 1,
                MaxUniverseSymbols = 10,
                Min24hQuoteVolume = 100m,
                MaxDataAgeSeconds = 120,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = false
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance);

        var exception = await Assert.ThrowsAsync<MarketScannerPoisonedCandleAuditException>(() => service.RunOnceAsync());

        Assert.Equal("ScannerPoisonedCandleAudit", exception.ErrorCode);
        Assert.Equal(10, exception.PurgedCount);
        Assert.Contains("PurgedCount=10", exception.Detail, StringComparison.Ordinal);
        Assert.Contains("Symbol=BTCUSDT", exception.Detail, StringComparison.Ordinal);
        Assert.Contains("Field=ClosePrice", exception.Detail, StringComparison.Ordinal);
        Assert.Contains("BadValue=0", exception.Detail, StringComparison.Ordinal);

        var softDeletedCandles = await dbContext.HistoricalMarketCandles
            .IgnoreQueryFilters()
            .CountAsync(entity => entity.Symbol == "BTCUSDT" && entity.IsDeleted);
        var candidateCount = await dbContext.MarketScannerCandidates.CountAsync();
        var cycleCount = await dbContext.MarketScannerCycles.CountAsync();

        Assert.Equal(10, softDeletedCandles);
        Assert.Equal(0, candidateCount);
        Assert.Equal(0, cycleCount);
    }

    [Fact]
    public async Task RunOnceAsync_KeepsValidHighNotionalCandlesActive_AndCapsPersistedQuoteVolume()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 10_000_000_000m, volume: 10_000_000_000m);
        await dbContext.SaveChangesAsync();

        var marketDataService = new FakeMarketDataService();
        marketDataService.SetLatestPrice("BTCUSDT", 100m, nowUtc.UtcDateTime);

        var service = new MarketScannerService(
            dbContext,
            marketDataService,
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 1,
                MaxUniverseSymbols = 10,
                Min24hQuoteVolume = 100m,
                MaxDataAgeSeconds = 120,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = false
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance);

        var cycle = await service.RunOnceAsync();

        var candidate = await dbContext.MarketScannerCandidates.SingleAsync(entity => entity.ScanCycleId == cycle.Id);
        var softDeletedCandles = await dbContext.HistoricalMarketCandles
            .IgnoreQueryFilters()
            .CountAsync(entity => entity.Symbol == "BTCUSDT" && entity.IsDeleted);

        Assert.True(candidate.IsEligible);
        Assert.Equal(79_228_162_514.264337593543950335m, candidate.QuoteVolume24h);
        Assert.Equal(0, softDeletedCandles);
    }
    [Fact]
    public async Task RunOnceAsync_UsesExplicitActiveStrategyVersion_WhenLifecycleIsManaged()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(nowUtc);
        await using var dbContext = CreateDbContext();
        var strategyId = Guid.NewGuid();
        var activeVersionId = Guid.NewGuid();
        var inactiveVersionId = Guid.NewGuid();
        var marketDataService = new FakeMarketDataService();
        marketDataService.SetLatestPrice("BTCUSDT", 100m, nowUtc.UtcDateTime);

        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = strategyId,
            OwnerUserId = "user-active",
            StrategyKey = "scanner-active",
            DisplayName = "scanner-active",
            PublishedMode = ExecutionEnvironment.Live,
            PublishedAtUtc = nowUtc.UtcDateTime.AddMinutes(-5),
            UsesExplicitVersionLifecycle = true,
            ActiveTradingStrategyVersionId = activeVersionId,
            ActiveVersionActivatedAtUtc = nowUtc.UtcDateTime.AddMinutes(-5)
        });
        dbContext.TradingStrategyVersions.AddRange(
            new TradingStrategyVersion
            {
                Id = activeVersionId,
                OwnerUserId = "user-active",
                TradingStrategyId = strategyId,
                SchemaVersion = 2,
                VersionNumber = 1,
                Status = StrategyVersionStatus.Published,
                DefinitionJson = "{\"schemaVersion\":2,\"metadata\":{\"templateKey\":\"active-v1\",\"templateName\":\"Active V1\"},\"entry\":{\"path\":\"context.mode\",\"comparison\":\"equals\",\"value\":\"Live\"}}",
                PublishedAtUtc = nowUtc.UtcDateTime.AddMinutes(-5)
            },
            new TradingStrategyVersion
            {
                Id = inactiveVersionId,
                OwnerUserId = "user-active",
                TradingStrategyId = strategyId,
                SchemaVersion = 2,
                VersionNumber = 2,
                Status = StrategyVersionStatus.Published,
                DefinitionJson = "{\"schemaVersion\":2,\"metadata\":{\"templateKey\":\"active-v2\",\"templateName\":\"Active V2\"},\"entry\":{\"path\":\"context.mode\",\"comparison\":\"equals\",\"value\":\"Live\"}}",
                PublishedAtUtc = nowUtc.UtcDateTime.AddMinutes(-1)
            });
        dbContext.TradingBots.Add(new TradingBot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-active",
            Name = "scanner-active bot",
            StrategyKey = "scanner-active",
            Symbol = "BTCUSDT",
            IsEnabled = true
        });
        SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
        await dbContext.SaveChangesAsync();

        var indicatorDataService = new FakeIndicatorDataService();
        indicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", nowUtc.UtcDateTime));

        var strategyEvaluatorService = new FakeStrategyEvaluatorService();
        strategyEvaluatorService.SetReport("BTCUSDT", CreateEvaluationReport(strategyId, activeVersionId, "scanner-active", "BTCUSDT", "1m", nowUtc.UtcDateTime, 95, "Active strategy accepted."));

        var service = new MarketScannerService(
            dbContext,
            marketDataService,
            new FakeSharedSymbolRegistry([
                new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
            ]),
            Options.Create(new MarketScannerOptions
            {
                TopCandidateCount = 1,
                MaxUniverseSymbols = 20,
                Min24hQuoteVolume = 1_000m,
                MaxDataAgeSeconds = 120,
                StrategyScoreWeight = 2m,
                AllowedQuoteAssets = ["USDT"],
                HandoffEnabled = true
            }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m", SeedSymbols = ["BTCUSDT"] }),
            timeProvider,
            NullLogger<MarketScannerService>.Instance,
            handoffService: null,
            indicatorDataService,
            strategyEvaluatorService,
            Options.Create(new BotExecutionPilotOptions { SignalEvaluationMode = ExecutionEnvironment.Live }));

        _ = await service.RunOnceAsync();
        var strategy = await dbContext.TradingStrategies.SingleAsync(entity => entity.Id == strategyId);
        strategy.ActiveTradingStrategyVersionId = inactiveVersionId;
        strategy.ActiveVersionActivatedAtUtc = nowUtc.UtcDateTime;
        await dbContext.SaveChangesAsync();
        _ = await service.RunOnceAsync();

        Assert.Equal([activeVersionId, inactiveVersionId], strategyEvaluatorService.RequestedVersionIds);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private static void SeedCandles(ApplicationDbContext dbContext, string symbol, DateTime latestCloseTimeUtc, decimal closePrice, decimal volume)
    {
        var normalizedCloseTimeUtc = DateTime.SpecifyKind(latestCloseTimeUtc, DateTimeKind.Utc);
        var firstOpenTimeUtc = normalizedCloseTimeUtc.AddMinutes(-10);

        for (var index = 0; index < 10; index++)
        {
            var openTimeUtc = firstOpenTimeUtc.AddMinutes(index);
            dbContext.HistoricalMarketCandles.Add(new HistoricalMarketCandle
            {
                Id = Guid.NewGuid(),
                Symbol = symbol,
                Interval = "1m",
                OpenTimeUtc = openTimeUtc,
                CloseTimeUtc = openTimeUtc.AddMinutes(1),
                OpenPrice = closePrice,
                HighPrice = closePrice,
                LowPrice = closePrice,
                ClosePrice = closePrice,
                Volume = volume,
                ReceivedAtUtc = openTimeUtc.AddMinutes(1),
                Source = "unit-test"
            });
        }
    }

    private static async Task<(Guid TradingStrategyId, Guid TradingStrategyVersionId)> SeedStrategyGraphAsync(
        ApplicationDbContext dbContext,
        string ownerUserId,
        string symbol,
        string strategyKey,
        string definitionJson)
    {
        var tradingStrategyId = Guid.NewGuid();
        var tradingStrategyVersionId = Guid.NewGuid();

        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = tradingStrategyId,
            OwnerUserId = ownerUserId,
            StrategyKey = strategyKey,
            DisplayName = strategyKey,
            PromotionState = StrategyPromotionState.LivePublished,
            PublishedMode = ExecutionEnvironment.Live,
            PublishedAtUtc = new DateTime(2026, 4, 3, 11, 55, 0, DateTimeKind.Utc)
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
            PublishedAtUtc = new DateTime(2026, 4, 3, 11, 56, 0, DateTimeKind.Utc)
        });
        dbContext.TradingBots.Add(new TradingBot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = $"{strategyKey} bot",
            StrategyKey = strategyKey,
            Symbol = symbol,
            IsEnabled = true
        });

        await dbContext.SaveChangesAsync();
        return (tradingStrategyId, tradingStrategyVersionId);
    }

    private static StrategyIndicatorSnapshot CreateIndicatorSnapshot(
        string symbol,
        string timeframe,
        DateTime closeTimeUtc,
        decimal rsiValue = 30m,
        decimal macdLine = 1m,
        decimal macdSignalLine = 0.8m,
        decimal macdHistogram = 0.2m,
        decimal middleBand = 100m,
        decimal upperBand = 110m,
        decimal lowerBand = 90m)
    {
        return new StrategyIndicatorSnapshot(
            symbol,
            timeframe,
            closeTimeUtc.AddMinutes(-1),
            closeTimeUtc,
            closeTimeUtc,
            100,
            34,
            IndicatorDataState.Ready,
            DegradedModeReasonCode.None,
            new RelativeStrengthIndexSnapshot(14, true, rsiValue),
            new MovingAverageConvergenceDivergenceSnapshot(12, 26, 9, true, macdLine, macdSignalLine, macdHistogram),
            new BollingerBandsSnapshot(20, 2m, true, middleBand, upperBand, lowerBand, 3m),
            "unit-test");
    }

    private static StrategyEvaluationReportSnapshot CreateEvaluationReport(
        Guid tradingStrategyId,
        Guid tradingStrategyVersionId,
        string strategyKey,
        string symbol,
        string timeframe,
        DateTime evaluatedAtUtc,
        int aggregateScore,
        string explanation,
        string outcome = "EntryMatched",
        StrategyEvaluationResult? ruleEvaluation = null,
        IReadOnlyCollection<string>? passedRules = null,
        IReadOnlyCollection<string>? failedRules = null)
    {
        var indicatorSnapshot = CreateIndicatorSnapshot(symbol, timeframe, evaluatedAtUtc);
        var resolvedPassedRules = passedRules ?? new[] { "entry-mode [context/1m] PASS w=20 :: matched" };
        var resolvedFailedRules = failedRules ?? Array.Empty<string>();
        return new StrategyEvaluationReportSnapshot(
            tradingStrategyId,
            tradingStrategyVersionId,
            1,
            strategyKey,
            strategyKey,
            "rsi-reversal",
            "RSI Reversal",
            symbol,
            timeframe,
            evaluatedAtUtc,
            outcome,
            aggregateScore,
            resolvedPassedRules.Count,
            resolvedFailedRules.Count,
            ruleEvaluation ?? new StrategyEvaluationResult(true, true, false, false, true, true, null, null, null),
            resolvedPassedRules,
            resolvedFailedRules,
            $"Strategy={strategyKey}; Symbol={indicatorSnapshot.Symbol}; Timeframe={indicatorSnapshot.Timeframe}; Outcome={outcome}; Score={aggregateScore}; {explanation}");
    }

    private static IReadOnlyCollection<MarketCandleSnapshot> CreateClosedCandles(
        string symbol,
        DateTime latestCloseTimeUtc,
        int candleCount,
        decimal closePrice,
        decimal volume,
        string source)
    {
        var candles = new List<MarketCandleSnapshot>(candleCount);
        var firstOpenTimeUtc = latestCloseTimeUtc.AddMinutes(-candleCount + 1);

        for (var index = 0; index < candleCount; index++)
        {
            var openTimeUtc = firstOpenTimeUtc.AddMinutes(index);
            candles.Add(new MarketCandleSnapshot(
                symbol,
                "1m",
                openTimeUtc,
                openTimeUtc.AddMinutes(1),
                closePrice,
                closePrice,
                closePrice,
                closePrice,
                volume,
                true,
                openTimeUtc.AddMinutes(1),
                source));
        }

        return candles;
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeSharedSymbolRegistry(IReadOnlyCollection<SymbolMetadataSnapshot> snapshots) : ISharedSymbolRegistry
    {
        public ValueTask<SymbolMetadataSnapshot?> GetSymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(snapshots.SingleOrDefault(item => item.Symbol == symbol));
        }

        public ValueTask<IReadOnlyCollection<SymbolMetadataSnapshot>> ListSymbolsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(snapshots);
        }
    }

    private sealed class FakeMarketDataService : IMarketDataService
    {
        private readonly Dictionary<string, MarketPriceSnapshot> latestPrices = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (MarketCandleSnapshot Snapshot, SharedMarketDataCacheReadStatus Status)> latestKlines = new(StringComparer.Ordinal);

        public int SharedPriceReadCount { get; private set; }

        public int SharedKlineReadCount { get; private set; }

        public int LegacyPriceReadCount { get; private set; }

        public void SetLatestPrice(string symbol, decimal price, DateTime observedAtUtc)
        {
            latestPrices[symbol] = new MarketPriceSnapshot(symbol, price, observedAtUtc, observedAtUtc, "unit-test");
        }

        public void SetLatestKline(string symbol, string timeframe, MarketCandleSnapshot snapshot, SharedMarketDataCacheReadStatus status)
        {
            latestKlines[$"{symbol}|{timeframe}"] = (snapshot, status);
        }

        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
        {
            LegacyPriceReadCount++;
            latestPrices.TryGetValue(symbol, out var snapshot);
            return ValueTask.FromResult<MarketPriceSnapshot?>(snapshot);
        }

        public ValueTask<SharedMarketDataCacheReadResult<MarketPriceSnapshot>> ReadLatestPriceAsync(
            string symbol,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SharedPriceReadCount++;
            latestPrices.TryGetValue(symbol, out var snapshot);

            return ValueTask.FromResult(snapshot is null
                ? SharedMarketDataCacheReadResult<MarketPriceSnapshot>.Miss("No shared ticker snapshot.")
                : SharedMarketDataCacheReadResult<MarketPriceSnapshot>.HitFresh(
                    new SharedMarketDataCacheEntry<MarketPriceSnapshot>(
                        SharedMarketDataCacheDataType.Ticker,
                        snapshot.Symbol,
                        Timeframe: null,
                        UpdatedAtUtc: snapshot.ObservedAtUtc,
                        CachedAtUtc: snapshot.ReceivedAtUtc,
                        FreshUntilUtc: snapshot.ReceivedAtUtc.AddSeconds(15),
                        ExpiresAtUtc: snapshot.ReceivedAtUtc.AddMinutes(5),
                        Source: snapshot.Source,
                        Payload: snapshot)));
        }

        public ValueTask<SharedMarketDataCacheReadResult<MarketCandleSnapshot>> ReadLatestKlineAsync(
            string symbol,
            string timeframe,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SharedKlineReadCount++;

            if (!latestKlines.TryGetValue($"{symbol}|{timeframe}", out var entry))
            {
                return ValueTask.FromResult(SharedMarketDataCacheReadResult<MarketCandleSnapshot>.Miss("No shared kline snapshot."));
            }

            var cacheEntry = new SharedMarketDataCacheEntry<MarketCandleSnapshot>(
                SharedMarketDataCacheDataType.Kline,
                entry.Snapshot.Symbol,
                entry.Snapshot.Interval,
                UpdatedAtUtc: entry.Snapshot.CloseTimeUtc,
                CachedAtUtc: entry.Snapshot.ReceivedAtUtc,
                FreshUntilUtc: entry.Snapshot.ReceivedAtUtc.AddSeconds(15),
                ExpiresAtUtc: entry.Snapshot.ReceivedAtUtc.AddMinutes(5),
                Source: entry.Snapshot.Source,
                Payload: entry.Snapshot);

            return ValueTask.FromResult(entry.Status switch
            {
                SharedMarketDataCacheReadStatus.HitFresh => SharedMarketDataCacheReadResult<MarketCandleSnapshot>.HitFresh(cacheEntry),
                SharedMarketDataCacheReadStatus.HitStale => SharedMarketDataCacheReadResult<MarketCandleSnapshot>.HitStale(cacheEntry),
                SharedMarketDataCacheReadStatus.ProviderUnavailable => SharedMarketDataCacheReadResult<MarketCandleSnapshot>.ProviderUnavailable("Shared kline provider unavailable."),
                SharedMarketDataCacheReadStatus.DeserializeFailed => SharedMarketDataCacheReadResult<MarketCandleSnapshot>.DeserializeFailed("Shared kline deserialize failed."),
                SharedMarketDataCacheReadStatus.InvalidPayload => SharedMarketDataCacheReadResult<MarketCandleSnapshot>.InvalidPayload("Shared kline invalid payload."),
                _ => SharedMarketDataCacheReadResult<MarketCandleSnapshot>.Miss("Unsupported shared kline status.")
            });
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(null);
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeHistoricalKlineClient : IBinanceHistoricalKlineClient
    {
        private readonly Dictionary<string, IReadOnlyCollection<MarketCandleSnapshot>> candles = new(StringComparer.Ordinal);

        public void SetCandles(string symbol, IReadOnlyCollection<MarketCandleSnapshot> snapshots)
        {
            candles[symbol] = snapshots;
        }

        public Task<IReadOnlyCollection<MarketCandleSnapshot>> GetClosedCandlesAsync(
            string symbol,
            string interval,
            DateTime startOpenTimeUtc,
            DateTime endOpenTimeUtc,
            int limit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candles.TryGetValue(symbol, out var snapshots);
            IReadOnlyCollection<MarketCandleSnapshot> filtered = snapshots is null
                ? Array.Empty<MarketCandleSnapshot>()
                : snapshots
                    .Where(item =>
                        item.Interval == interval &&
                        item.OpenTimeUtc >= startOpenTimeUtc &&
                        item.OpenTimeUtc <= endOpenTimeUtc)
                    .OrderBy(item => item.OpenTimeUtc)
                    .Take(limit)
                    .ToArray();
            return Task.FromResult(filtered);
        }
    }

    private sealed class FakeIndicatorDataService : IIndicatorDataService
    {
        private readonly Dictionary<string, StrategyIndicatorSnapshot> snapshots = new(StringComparer.Ordinal);

        public void SetReadySnapshot(StrategyIndicatorSnapshot snapshot)
        {
            snapshots[$"{snapshot.Symbol}|{snapshot.Timeframe}"] = snapshot;
        }

        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<StrategyIndicatorSnapshot?> GetLatestAsync(string symbol, string timeframe, CancellationToken cancellationToken = default)
        {
            snapshots.TryGetValue($"{symbol}|{timeframe}", out var snapshot);
            return ValueTask.FromResult<StrategyIndicatorSnapshot?>(snapshot);
        }

        public ValueTask<StrategyIndicatorSnapshot?> PrimeAsync(string symbol, string timeframe, IReadOnlyCollection<MarketCandleSnapshot> historicalCandles, CancellationToken cancellationToken = default)
        {
            snapshots.TryGetValue($"{symbol}|{timeframe}", out var snapshot);
            return ValueTask.FromResult<StrategyIndicatorSnapshot?>(snapshot);
        }

        public async IAsyncEnumerable<StrategyIndicatorSnapshot> WatchAsync(
            IEnumerable<IndicatorSubscription> subscriptions,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeStrategyEvaluatorService : IStrategyEvaluatorService
    {
        private readonly Dictionary<string, StrategyEvaluationReportSnapshot> reports = new(StringComparer.Ordinal);

        public List<string> RequestedSymbols { get; } = [];

        public List<Guid> RequestedVersionIds { get; } = [];

        public void SetReport(string symbol, StrategyEvaluationReportSnapshot report)
        {
            reports[symbol] = report;
        }

        public StrategyEvaluationResult Evaluate(string definitionJson, StrategyEvaluationContext context)
        {
            throw new NotSupportedException();
        }

        public StrategyEvaluationResult Evaluate(StrategyRuleDocument document, StrategyEvaluationContext context)
        {
            throw new NotSupportedException();
        }

        public StrategyEvaluationReportSnapshot EvaluateReport(StrategyEvaluationReportRequest request)
        {
            RequestedSymbols.Add(request.EvaluationContext.IndicatorSnapshot.Symbol);
            RequestedVersionIds.Add(request.TradingStrategyVersionId);
            return reports[request.EvaluationContext.IndicatorSnapshot.Symbol];
        }
    }
}
