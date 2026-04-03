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
        Assert.Contains("BestCandidate=ADAUSDT", heartbeat.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(4, marketDataService.SharedPriceReadCount);
        Assert.Equal(0, marketDataService.LegacyPriceReadCount);
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
                StrategyScoreWeight = 20_000m,
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

        Assert.Equal("BTCUSDT", cycle.BestCandidateSymbol);
        Assert.Equal(["BTCUSDT", "ETHUSDT"], rankedCandidates.Select(candidate => candidate.Symbol).ToArray());

        var btc = Assert.Single(rankedCandidates, candidate => candidate.Symbol == "BTCUSDT");
        var eth = Assert.Single(rankedCandidates, candidate => candidate.Symbol == "ETHUSDT");

        Assert.Equal(1_000_000m, btc.MarketScore);
        Assert.Equal(95, btc.StrategyScore);
        Assert.Equal(2_900_000m, btc.Score);
        Assert.Contains("StrategyKey=scanner-btc", btc.ScoringSummary, StringComparison.Ordinal);
        Assert.Contains("StrategyScore=95", btc.ScoringSummary, StringComparison.Ordinal);

        Assert.Equal(2_000_000m, eth.MarketScore);
        Assert.Equal(5, eth.StrategyScore);
        Assert.Equal(2_100_000m, eth.Score);
        Assert.True(btc.Score > eth.Score);
        Assert.Equal("BTCUSDT", strategyEvaluatorService.RequestedSymbols[0]);
        Assert.Equal("ETHUSDT", strategyEvaluatorService.RequestedSymbols[1]);
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

    private static StrategyIndicatorSnapshot CreateIndicatorSnapshot(string symbol, string timeframe, DateTime closeTimeUtc)
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
            new RelativeStrengthIndexSnapshot(14, true, 30m),
            new MovingAverageConvergenceDivergenceSnapshot(12, 26, 9, true, 1m, 0.8m, 0.2m),
            new BollingerBandsSnapshot(20, 2m, true, 100m, 110m, 90m, 3m),
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
        string explanation)
    {
        var indicatorSnapshot = CreateIndicatorSnapshot(symbol, timeframe, evaluatedAtUtc);
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
            "EntryMatched",
            aggregateScore,
            2,
            0,
            new StrategyEvaluationResult(true, true, false, false, true, true, null, null, null),
            ["entry-mode [context/1m] PASS w=20 :: matched"],
            [],
            $"Strategy={strategyKey}; Symbol={indicatorSnapshot.Symbol}; Timeframe={indicatorSnapshot.Timeframe}; Outcome=EntryMatched; Score={aggregateScore}; {explanation}");
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

        public int SharedPriceReadCount { get; private set; }

        public int LegacyPriceReadCount { get; private set; }

        public void SetLatestPrice(string symbol, decimal price, DateTime observedAtUtc)
        {
            latestPrices[symbol] = new MarketPriceSnapshot(symbol, price, observedAtUtc, observedAtUtc, "unit-test");
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
            return reports[request.EvaluationContext.IndicatorSnapshot.Symbol];
        }
    }
}

