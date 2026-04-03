using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using CoinBot.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.IntegrationTests.Administration;

public sealed class MarketScannerHandoffIntegrationTests
{
    [Fact]
    public async Task MarketScannerHandoffService_PersistsPreparedAttemptAndAdminReadModel_OnSqlServer()
    {
        var databaseName = $"CoinBotMarketScannerHandoffInt_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(connectionString).Options;
        await using var dbContext = new ApplicationDbContext(options, new TestDataScopeContextAccessor());
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        try
        {
            var scanCycleId = Guid.NewGuid();
            var strategyId = Guid.NewGuid();
            var strategyVersionId = Guid.NewGuid();
            var botId = Guid.NewGuid();
            dbContext.Users.Add(new ApplicationUser
            {
                Id = "user-btc",
                UserName = "user-btc",
                NormalizedUserName = "USER-BTC",
                Email = "user-btc@coinbot.test",
                NormalizedEmail = "USER-BTC@COINBOT.TEST",
                FullName = "Scanner Handoff BTC User"
            });
            dbContext.MarketScannerCycles.Add(new MarketScannerCycle
            {
                Id = scanCycleId,
                StartedAtUtc = nowUtc.UtcDateTime.AddSeconds(-2),
                CompletedAtUtc = nowUtc.UtcDateTime,
                UniverseSource = "integration-test",
                ScannedSymbolCount = 1,
                EligibleCandidateCount = 1,
                TopCandidateCount = 1,
                BestCandidateSymbol = "BTCUSDT",
                BestCandidateScore = 250_000m,
                Summary = "integration-test"
            });
            dbContext.MarketScannerCandidates.Add(new MarketScannerCandidate
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                ScanCycleId = scanCycleId,
                Symbol = "BTCUSDT",
                UniverseSource = "integration-test",
                ObservedAtUtc = nowUtc.UtcDateTime,
                LastCandleAtUtc = nowUtc.UtcDateTime,
                LastPrice = 100m,
                QuoteVolume24h = 250_000m,
                IsEligible = true,
                Score = 250_000m,
                Rank = 1,
                IsTopCandidate = true
            });
            dbContext.TradingStrategies.Add(new TradingStrategy
            {
                Id = strategyId,
                OwnerUserId = "user-btc",
                StrategyKey = "scanner-handoff-btc",
                DisplayName = "Scanner Handoff BTC",
                PromotionState = StrategyPromotionState.LivePublished,
                PublishedMode = ExecutionEnvironment.Live,
                PublishedAtUtc = nowUtc.UtcDateTime.AddMinutes(-1)
            });
            dbContext.TradingStrategyVersions.Add(new TradingStrategyVersion
            {
                Id = strategyVersionId,
                OwnerUserId = "user-btc",
                TradingStrategyId = strategyId,
                SchemaVersion = 1,
                VersionNumber = 1,
                Status = StrategyVersionStatus.Published,
                DefinitionJson = "{}",
                PublishedAtUtc = nowUtc.UtcDateTime.AddMinutes(-1)
            });
            dbContext.TradingBots.Add(new TradingBot
            {
                Id = botId,
                OwnerUserId = "user-btc",
                Name = "Scanner Handoff BTC Bot",
                StrategyKey = "scanner-handoff-btc",
                Symbol = "BTCUSDT",
                IsEnabled = true
            });
            await dbContext.SaveChangesAsync();

            var strategySignalService = new FakeStrategySignalService(CreateSignal(strategyId, strategyVersionId, "BTCUSDT", "1m", nowUtc.UtcDateTime));
            var services = new ServiceCollection();
            services.AddScoped<IDataScopeContextAccessor, TestDataScopeContextAccessor>();
            services.AddScoped(provider => new ApplicationDbContext(options, provider.GetRequiredService<IDataScopeContextAccessor>()));
            services.AddSingleton<IStrategySignalService>(strategySignalService);
            services.AddSingleton<IExecutionGate>(new FakeExecutionGate(nowUtc.UtcDateTime));
            services.AddSingleton<IUserExecutionOverrideGuard>(new FakeUserExecutionOverrideGuard());
            await using var serviceProvider = services.BuildServiceProvider();
            var handoffService = new MarketScannerHandoffService(
                dbContext,
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                new FakeMarketDataService(nowUtc.UtcDateTime),
                new FakeIndicatorDataService(CreateIndicatorSnapshot("BTCUSDT", "1m", nowUtc.UtcDateTime)),
                new FakeSharedSymbolRegistry(),
                new FakeDataLatencyCircuitBreaker(nowUtc.UtcDateTime),
                Options.Create(new MarketScannerOptions { HandoffEnabled = true, AllowedQuoteAssets = ["USDT"] }),
                Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m" }),
                Options.Create(new BotExecutionPilotOptions { SignalEvaluationMode = ExecutionEnvironment.Live, PrimeHistoricalCandleCount = 34 }),
                new FixedTimeProvider(nowUtc),
                NullLogger<MarketScannerHandoffService>.Instance);

            var attempt = await handoffService.RunOnceAsync(scanCycleId);

            var persistedAttempt = await dbContext.MarketScannerHandoffAttempts.AsNoTracking().SingleAsync(entity => entity.Id == attempt.Id);
            var readModelService = new AdminMonitoringReadModelService(dbContext, new MemoryCache(new MemoryCacheOptions()), new FixedTimeProvider(nowUtc));
            var dashboardSnapshot = await readModelService.GetSnapshotAsync();

            Assert.Equal("BTCUSDT", persistedAttempt.SelectedSymbol);
            Assert.Equal("Prepared", persistedAttempt.ExecutionRequestStatus);
            Assert.Equal("Persisted", persistedAttempt.StrategyDecisionOutcome);
            Assert.Equal(strategyId, persistedAttempt.TradingStrategyId);
            Assert.Equal(strategyVersionId, persistedAttempt.TradingStrategyVersionId);
            Assert.Equal(ExecutionOrderSide.Buy, persistedAttempt.ExecutionSide);
            Assert.Equal(ExecutionEnvironment.Live, persistedAttempt.ExecutionEnvironment);
            Assert.Null(persistedAttempt.BlockerCode);
            Assert.Equal("BTCUSDT", strategySignalService.LastRequest?.EvaluationContext.IndicatorSnapshot.Symbol);
            Assert.Equal("BTCUSDT", dashboardSnapshot.MarketScanner.LatestHandoff.SelectedSymbol);
            Assert.Equal("Prepared", dashboardSnapshot.MarketScanner.LatestHandoff.ExecutionRequestStatus);
            Assert.Equal("Persisted", dashboardSnapshot.MarketScanner.LatestHandoff.StrategyDecisionOutcome);
            Assert.Equal("BTCUSDT", dashboardSnapshot.MarketScanner.LastSuccessfulHandoff.SelectedSymbol);
        }
        finally
        {
            await dbContext.Database.EnsureDeletedAsync();
        }
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
            new RelativeStrengthIndexSnapshot(14, true, 55m),
            new MovingAverageConvergenceDivergenceSnapshot(12, 26, 9, true, 1m, 0.8m, 0.2m),
            new BollingerBandsSnapshot(20, 2m, true, 100m, 110m, 90m, 3m),
            "integration-test");
    }

    private static StrategySignalSnapshot CreateSignal(Guid strategyId, Guid versionId, string symbol, string timeframe, DateTime generatedAtUtc)
    {
        var indicatorSnapshot = CreateIndicatorSnapshot(symbol, timeframe, generatedAtUtc);
        return new StrategySignalSnapshot(
            Guid.NewGuid(),
            strategyId,
            versionId,
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
                strategyId,
                versionId,
                1,
                1,
                ExecutionEnvironment.Live,
                indicatorSnapshot,
                new StrategyEvaluationResult(true, true, false, false, true, true, null, null, null),
                new StrategySignalConfidenceSnapshot(95, StrategySignalConfidenceBand.High, 3, 3, true, true, false, RiskVetoReasonCode.None, false, "Integration entry."),
                new StrategySignalLogExplainabilitySnapshot("Entry", "Integration entry", ["driver"], ["scanner"]),
                new StrategySignalDuplicateSuppressionSnapshot(true, false, "fp-BTCUSDT")));
    }

    private sealed class FakeStrategySignalService(StrategySignalSnapshot signal) : IStrategySignalService
    {
        public GenerateStrategySignalsRequest? LastRequest { get; private set; }

        public Task<StrategySignalGenerationResult> GenerateAsync(GenerateStrategySignalsRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new StrategySignalGenerationResult(
                new StrategyEvaluationResult(true, true, false, false, true, true, null, null, null),
                [signal],
                [],
                0));
        }

        public Task<StrategySignalSnapshot?> GetAsync(Guid strategySignalId, CancellationToken cancellationToken = default) => Task.FromResult<StrategySignalSnapshot?>(signal);

        public Task<StrategySignalVetoSnapshot?> GetVetoAsync(Guid strategySignalVetoId, CancellationToken cancellationToken = default) => Task.FromResult<StrategySignalVetoSnapshot?>(null);
    }

    private sealed class FakeExecutionGate(DateTime nowUtc) : IExecutionGate
    {
        public Task<GlobalExecutionSwitchSnapshot> EnsureExecutionAllowedAsync(ExecutionGateRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GlobalExecutionSwitchSnapshot(TradeMasterSwitchState.Armed, false, true, nowUtc));
        }
    }

    private sealed class FakeUserExecutionOverrideGuard : IUserExecutionOverrideGuard
    {
        public Task<UserExecutionOverrideEvaluationResult> EvaluateAsync(UserExecutionOverrideEvaluationRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UserExecutionOverrideEvaluationResult(false, null, null));
        }
    }

    private sealed class FakeIndicatorDataService(StrategyIndicatorSnapshot snapshot) : IIndicatorDataService
    {
        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<StrategyIndicatorSnapshot?> GetLatestAsync(string symbol, string timeframe, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<StrategyIndicatorSnapshot?>(snapshot);
        }

        public ValueTask<StrategyIndicatorSnapshot?> PrimeAsync(string symbol, string timeframe, IReadOnlyCollection<MarketCandleSnapshot> historicalCandles, CancellationToken cancellationToken = default)
        {
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
        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<MarketPriceSnapshot?>(new MarketPriceSnapshot(symbol, 100m, nowUtc, nowUtc, "integration-test"));
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(new SymbolMetadataSnapshot(symbol, "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc) { MinQuantity = 0.001m, MinNotional = 100m, QuantityPrecision = 3 });
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(IEnumerable<string> symbols, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeSharedSymbolRegistry : ISharedSymbolRegistry
    {
        public ValueTask<SymbolMetadataSnapshot?> GetSymbolAsync(string symbol, CancellationToken cancellationToken = default) => ValueTask.FromResult<SymbolMetadataSnapshot?>(null);

        public ValueTask<IReadOnlyCollection<SymbolMetadataSnapshot>> ListSymbolsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyCollection<SymbolMetadataSnapshot>>([]);
    }

    private sealed class FakeDataLatencyCircuitBreaker(DateTime nowUtc) : IDataLatencyCircuitBreaker
    {
        public Task<DegradedModeSnapshot> GetSnapshotAsync(string? correlationId = null, string? symbol = null, string? timeframe = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DegradedModeSnapshot(DegradedModeStateCode.Normal, DegradedModeReasonCode.None, false, false, nowUtc, nowUtc, 0, 0, nowUtc, true, "integration-test", symbol, timeframe, nowUtc.AddMinutes(1), 0));
        }

        public Task<DegradedModeSnapshot> RecordHeartbeatAsync(DataLatencyHeartbeat heartbeat, string? correlationId = null, CancellationToken cancellationToken = default)
        {
            return GetSnapshotAsync(correlationId, heartbeat.Symbol, heartbeat.Timeframe, cancellationToken);
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
            UserId = userId;
            HasIsolationBypass = hasIsolationBypass;
            return new ScopeReset(this);
        }

        private sealed class ScopeReset(TestDataScopeContextAccessor accessor) : IDisposable
        {
            public void Dispose()
            {
                accessor.UserId = null;
                accessor.HasIsolationBypass = true;
            }
        }
    }
}




