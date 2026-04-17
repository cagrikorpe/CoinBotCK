using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using CoinBot.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.IntegrationTests.Administration;

public sealed class MarketScannerIntegrationTests
{
    [Fact]
    public async Task MarketScannerService_PersistsLatestScanCycleAndAdminReadModel_OnSqlServer()
    {
        var databaseName = $"CoinBotMarketScannerInt_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        await using var dbContext = CreateDbContext(connectionString);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        try
        {
            SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 50_000m);
            SeedCandles(dbContext, "SOLUSDT", nowUtc.UtcDateTime, closePrice: 20m, volume: 10m);
            await dbContext.SaveChangesAsync();

            var service = new MarketScannerService(
                dbContext,
                new FakeMarketDataService(),
                new FakeSharedSymbolRegistry([
                    new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime),
                    new SymbolMetadataSnapshot("SOLUSDT", "Binance", "SOL", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
                ]),
                Options.Create(new MarketScannerOptions
                {
                    TopCandidateCount = 1,
                    MaxUniverseSymbols = 20,
                    Min24hQuoteVolume = 5_000m,
                    MaxDataAgeSeconds = 120,
                    AllowedQuoteAssets = ["USDT"],
                    HandoffEnabled = false
                }),
                Options.Create(new BinanceMarketDataOptions
                {
                    KlineInterval = "1m",
                    SeedSymbols = ["BTCUSDT", "SOLUSDT"]
                }),
                new FixedTimeProvider(nowUtc),
                NullLogger<MarketScannerService>.Instance);

            var cycle = await service.RunOnceAsync();

            var persistedCycle = await dbContext.MarketScannerCycles.AsNoTracking().SingleAsync(entity => entity.Id == cycle.Id);
            var persistedCandidates = await dbContext.MarketScannerCandidates
                .AsNoTracking()
                .Where(entity => entity.ScanCycleId == cycle.Id)
                .OrderBy(entity => entity.Symbol)
                .ToListAsync();
            var readModelService = new AdminMonitoringReadModelService(
                dbContext,
                new MemoryCache(new MemoryCacheOptions()),
                new FixedTimeProvider(nowUtc),
                Options.Create(new DataLatencyGuardOptions()));
            var dashboardSnapshot = await readModelService.GetSnapshotAsync();

            Assert.Equal(2, persistedCycle.ScannedSymbolCount);
            Assert.Equal(1, persistedCycle.EligibleCandidateCount);
            Assert.Equal("BTCUSDT", persistedCycle.BestCandidateSymbol);
            Assert.Equal(2, persistedCandidates.Count);
            Assert.Contains(persistedCandidates, candidate => candidate.Symbol == "BTCUSDT" && candidate.IsEligible && candidate.Rank == 1 && candidate.IsTopCandidate);
            Assert.Contains(persistedCandidates, candidate => candidate.Symbol == "SOLUSDT" && !candidate.IsEligible && candidate.RejectionReason == "LowQuoteVolume");
            Assert.Equal("BTCUSDT", dashboardSnapshot.MarketScanner.BestCandidateSymbol);
            Assert.Equal(2, dashboardSnapshot.MarketScanner.ScannedSymbolCount);
            Assert.Equal(1, dashboardSnapshot.MarketScanner.EligibleCandidateCount);
            Assert.Single(dashboardSnapshot.MarketScanner.TopCandidates);
            Assert.Single(dashboardSnapshot.MarketScanner.RejectedSamples);
        }
        finally
        {
            await dbContext.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task MarketScannerService_PersistsStrategyAwareCompositeScoreAndAdminProjection_OnSqlServer()
    {
        var databaseName = $"CoinBotMarketScannerStrategyScoreInt_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        await using var dbContext = CreateDbContext(connectionString);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        try
        {
            await SeedStrategyGraphAsync(dbContext, "user-btc", "BTCUSDT", "scanner-btc");
            await SeedStrategyGraphAsync(dbContext, "user-eth", "ETHUSDT", "scanner-eth");
            SeedCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 1_000m);
            SeedCandles(dbContext, "ETHUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 2_000m);
            await dbContext.SaveChangesAsync();

            var indicatorDataService = new FakeIndicatorDataService();
            indicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("BTCUSDT", "1m", nowUtc.UtcDateTime));
            indicatorDataService.SetReadySnapshot(CreateIndicatorSnapshot("ETHUSDT", "1m", nowUtc.UtcDateTime));

            var strategyEvaluatorService = new FakeStrategyEvaluatorService();
            strategyEvaluatorService.SetReport("BTCUSDT", CreateEvaluationReport("scanner-btc", "BTCUSDT", "1m", nowUtc.UtcDateTime, 95));
            strategyEvaluatorService.SetReport("ETHUSDT", CreateEvaluationReport("scanner-eth", "ETHUSDT", "1m", nowUtc.UtcDateTime, 5));

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
                Options.Create(new BinanceMarketDataOptions
                {
                    KlineInterval = "1m",
                    SeedSymbols = ["BTCUSDT", "ETHUSDT"]
                }),
                new FixedTimeProvider(nowUtc),
                NullLogger<MarketScannerService>.Instance,
                handoffService: null,
                indicatorDataService,
                strategyEvaluatorService,
                Options.Create(new BotExecutionPilotOptions { SignalEvaluationMode = ExecutionEnvironment.Live }));

            var cycle = await service.RunOnceAsync();

            var persistedCandidates = await dbContext.MarketScannerCandidates
                .AsNoTracking()
                .Where(entity => entity.ScanCycleId == cycle.Id)
                .OrderBy(entity => entity.Rank)
                .ToListAsync();
            var readModelService = new AdminMonitoringReadModelService(
                dbContext,
                new MemoryCache(new MemoryCacheOptions()),
                new FixedTimeProvider(nowUtc),
                Options.Create(new DataLatencyGuardOptions()));
            var dashboardSnapshot = await readModelService.GetSnapshotAsync();

            Assert.Equal("BTCUSDT", cycle.BestCandidateSymbol);
            Assert.Equal(96.6667m, cycle.BestCandidateScore);
            Assert.Equal(["BTCUSDT", "ETHUSDT"], persistedCandidates.Select(candidate => candidate.Symbol).ToArray());

            var btc = persistedCandidates[0];
            Assert.Equal(100m, btc.MarketScore);
            Assert.Equal(95, btc.StrategyScore);
            Assert.Equal(96.6667m, btc.Score);
            Assert.Contains("StrategyKey=scanner-btc", btc.ScoringSummary, StringComparison.Ordinal);

            Assert.Equal("BTCUSDT", dashboardSnapshot.MarketScanner.BestCandidateSymbol);
            Assert.Equal(96.6667m, dashboardSnapshot.MarketScanner.BestCandidateScore);
            var topCandidate = Assert.Single(dashboardSnapshot.MarketScanner.TopCandidates, candidate => candidate.Symbol == "BTCUSDT");
            Assert.Equal(100m, topCandidate.MarketScore);
            Assert.Equal(95, topCandidate.StrategyScore);
            Assert.Equal(96.6667m, topCandidate.Score);
            Assert.Contains("Outcome=EntryMatched", topCandidate.ScoringSummary, StringComparison.Ordinal);
        }
        finally
        {
            await dbContext.Database.EnsureDeletedAsync();
        }
    }

    private static ApplicationDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private static void SeedCandles(ApplicationDbContext dbContext, string symbol, DateTime latestCloseTimeUtc, decimal closePrice, decimal volume)
    {
        var firstOpenTimeUtc = latestCloseTimeUtc.AddMinutes(-10);

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
                Source = "integration-test"
            });
        }
    }

    private static async Task SeedStrategyGraphAsync(ApplicationDbContext dbContext, string ownerUserId, string symbol, string strategyKey)
    {
        var strategyId = Guid.NewGuid();
        dbContext.Users.Add(new ApplicationUser
        {
            Id = ownerUserId,
            UserName = ownerUserId,
            NormalizedUserName = ownerUserId.ToUpperInvariant(),
            Email = $"{ownerUserId}@coinbot.test",
            NormalizedEmail = $"{ownerUserId.ToUpperInvariant()}@COINBOT.TEST",
            FullName = ownerUserId,
            EmailConfirmed = true
        });
        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = strategyId,
            OwnerUserId = ownerUserId,
            StrategyKey = strategyKey,
            DisplayName = strategyKey,
            PromotionState = StrategyPromotionState.LivePublished,
            PublishedMode = ExecutionEnvironment.Live,
            PublishedAtUtc = new DateTime(2026, 4, 3, 11, 59, 0, DateTimeKind.Utc)
        });
        dbContext.TradingStrategyVersions.Add(new TradingStrategyVersion
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            TradingStrategyId = strategyId,
            SchemaVersion = 1,
            VersionNumber = 1,
            Status = StrategyVersionStatus.Published,
            DefinitionJson = "{}",
            PublishedAtUtc = new DateTime(2026, 4, 3, 11, 59, 0, DateTimeKind.Utc)
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
            "integration-test");
    }

    private static StrategyEvaluationReportSnapshot CreateEvaluationReport(string strategyKey, string symbol, string timeframe, DateTime evaluatedAtUtc, int aggregateScore)
    {
        return new StrategyEvaluationReportSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
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
            $"Strategy={strategyKey}; Symbol={symbol}; Timeframe={timeframe}; Outcome=EntryMatched; Score={aggregateScore}");
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<MarketPriceSnapshot?>(null);
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
            return reports[request.EvaluationContext.IndicatorSnapshot.Symbol];
        }
    }
}
