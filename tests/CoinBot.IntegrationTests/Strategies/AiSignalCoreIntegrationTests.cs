using CoinBot.Application.Abstractions.Ai;
using CoinBot.Application.Abstractions.Alerts;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Features;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Ai;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Features;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Risk;
using CoinBot.Infrastructure.Strategies;
using CoinBot.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.IntegrationTests.Strategies;

public sealed class AiSignalCoreIntegrationTests
{
    [Fact]
    public async Task FeatureSnapshot_ToAiOverlay_PersistsSignalAndTraceMetadata_OnSqlServer()
    {
        var databaseName = $"CoinBotAiSignalCore_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var nowUtc = new DateTimeOffset(2026, 4, 6, 12, 0, 0, TimeSpan.Zero);
        await using var dbContext = CreateDbContext(connectionString);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        try
        {
            SeedStrategyGraph(dbContext, nowUtc.UtcDateTime);
            await dbContext.SaveChangesAsync();

            var featureSnapshot = PromoteForLongOverlay(await CaptureFeatureSnapshotAsync(dbContext, nowUtc, stale: false));
            var service = CreateSignalService(dbContext, nowUtc, CreateEnabledAiOptions());
            var versionId = await dbContext.TradingStrategyVersions.Select(entity => entity.Id).SingleAsync();

            var result = await service.GenerateAsync(
                new GenerateStrategySignalsRequest(
                    versionId,
                    CreateContext(nowUtc.UtcDateTime),
                    featureSnapshot));
            var decisionTrace = await dbContext.DecisionTraces.SingleAsync();

            Assert.Single(result.Signals);
            Assert.Empty(result.Vetoes);
            var aiEvaluation = Assert.Single(result.AiEvaluations);
            Assert.Equal(AiSignalDirection.Long, aiEvaluation.SignalDirection);
            Assert.Contains("\"aiEvaluation\"", decisionTrace.SnapshotJson, StringComparison.Ordinal);
            Assert.Contains(featureSnapshot.Id.ToString(), decisionTrace.SnapshotJson, StringComparison.Ordinal);
            Assert.Contains("DeterministicStub", decisionTrace.SnapshotJson, StringComparison.Ordinal);
        }
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task StaleFeatureSnapshot_ToAiOverlay_SuppressesSignal_OnSqlServer()
    {
        var databaseName = $"CoinBotAiSignalCoreStale_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var nowUtc = new DateTimeOffset(2026, 4, 6, 12, 0, 0, TimeSpan.Zero);
        await using var dbContext = CreateDbContext(connectionString);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        try
        {
            SeedStrategyGraph(dbContext, nowUtc.UtcDateTime);
            await dbContext.SaveChangesAsync();

            var featureSnapshot = await CaptureFeatureSnapshotAsync(dbContext, nowUtc, stale: true);
            var service = CreateSignalService(dbContext, nowUtc, CreateEnabledAiOptions());
            var versionId = await dbContext.TradingStrategyVersions.Select(entity => entity.Id).SingleAsync();

            var result = await service.GenerateAsync(
                new GenerateStrategySignalsRequest(
                    versionId,
                    CreateContext(nowUtc.UtcDateTime),
                    featureSnapshot));
            var decisionTrace = await dbContext.DecisionTraces.SingleAsync();

            Assert.Empty(result.Signals);
            Assert.Empty(result.Vetoes);
            var aiEvaluation = Assert.Single(result.AiEvaluations);
            Assert.True(aiEvaluation.IsFallback);
            Assert.Equal(AiSignalFallbackReason.FeatureSnapshotNotReady, aiEvaluation.FallbackReason);
            Assert.Equal("SuppressedByAi", decisionTrace.DecisionOutcome);
            Assert.Equal("AiFeatureSnapshotNotReady", decisionTrace.DecisionReasonCode);
        }
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }

    private static AiSignalOptions CreateEnabledAiOptions()
    {
        return new AiSignalOptions
        {
            Enabled = true,
            SelectedProvider = DeterministicStubAiSignalProviderAdapter.ProviderNameValue,
            MinimumConfidence = 0.70m
        };
    }

    private static async Task<TradingFeatureSnapshotModel> CaptureFeatureSnapshotAsync(ApplicationDbContext dbContext, DateTimeOffset nowUtc, bool stale)
    {
        var timeProvider = new FixedTimeProvider(nowUtc);
        var correlationContextAccessor = new CorrelationContextAccessor();
        var auditLogService = new AuditLogService(dbContext, correlationContextAccessor);
        var tradingModeService = new TradingModeService(dbContext, auditLogService);
        var circuitBreaker = new DataLatencyCircuitBreaker(
            dbContext,
            new FakeAlertService(),
            Options.Create(new DataLatencyGuardOptions()),
            timeProvider,
            NullLogger<DataLatencyCircuitBreaker>.Instance);
        var featureService = new TradingFeatureSnapshotService(
            dbContext,
            circuitBreaker,
            tradingModeService,
            new EmptyHistoricalKlineClient(),
            Options.Create(new BotExecutionPilotOptions
            {
                DefaultSymbol = "BTCUSDT",
                Timeframe = "1m",
                PrimeHistoricalCandleCount = 200,
                PerBotCooldownSeconds = 300,
                PerSymbolCooldownSeconds = 300
            }),
            timeProvider,
            NullLogger<TradingFeatureSnapshotService>.Instance);
        var candles = CreateCandles(nowUtc.UtcDateTime.AddMinutes(-240));
        var heartbeatTimestamp = stale ? nowUtc.UtcDateTime.AddMinutes(-10) : candles[^1].CloseTimeUtc;

        await circuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "integration:ai-signal",
                heartbeatTimestamp,
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                ExpectedOpenTimeUtc: heartbeatTimestamp.AddMinutes(1),
                ContinuityGapCount: 0),
            cancellationToken: CancellationToken.None);

        return await featureService.CaptureAsync(
            new TradingFeatureCaptureRequest(
                "ai-int-user",
                Guid.Parse("99999999-9999-9999-9999-999999999999"),
                "ai-int-core",
                "BTCUSDT",
                "1m",
                nowUtc.UtcDateTime,
                HistoricalCandles: candles),
            CancellationToken.None);
    }

    private static TradingFeatureSnapshotModel PromoteForLongOverlay(TradingFeatureSnapshotModel snapshot)
    {
        return snapshot with
        {
            Trend = snapshot.Trend with { Ema20 = 63800m, Ema50 = 63500m, Ema200 = 62000m },
            Momentum = snapshot.Momentum with { Rsi = 28m, MacdHistogram = 0.5m, MacdLine = 1.4m, MacdSignal = 0.9m },
            Volume = snapshot.Volume with { RelativeVolume = 1.35m },
            FeatureSummary = "AI-ready bullish feature snapshot.",
            TopSignalHints = "RSI oversold; MACD improving; relative volume elevated.",
            PrimaryRegime = "TrendUp",
            MomentumBias = "Bullish",
            VolatilityState = "Contained"
        };
    }

    private static StrategySignalService CreateSignalService(ApplicationDbContext dbContext, DateTimeOffset nowUtc, AiSignalOptions aiOptions)
    {
        var timeProvider = new FixedTimeProvider(nowUtc);
        var correlationContextAccessor = new CorrelationContextAccessor();

        return new StrategySignalService(
            dbContext,
            new StrategyEvaluatorService(new StrategyRuleParser()),
            new RiskPolicyEvaluator(
                dbContext,
                timeProvider,
                NullLogger<RiskPolicyEvaluator>.Instance),
            new TraceService(
                dbContext,
                correlationContextAccessor,
                timeProvider),
            correlationContextAccessor,
            new AiSignalEvaluator(
                [new DeterministicStubAiSignalProviderAdapter(), new OfflineAiSignalProviderAdapter(), new OpenAiSignalProviderAdapter(), new GeminiAiSignalProviderAdapter()],
                Options.Create(aiOptions),
                timeProvider,
                NullLogger<AiSignalEvaluator>.Instance),
            Options.Create(aiOptions),
            timeProvider,
            NullLogger<StrategySignalService>.Instance);
    }

    private static void SeedStrategyGraph(ApplicationDbContext dbContext, DateTime nowUtc)
    {
        var ownerUserId = "ai-int-user";
        var strategy = new TradingStrategy
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            StrategyKey = "ai-int-core",
            DisplayName = "AI Integration Core"
        };

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
        dbContext.TradingBots.Add(new TradingBot
        {
            Id = Guid.Parse("99999999-9999-9999-9999-999999999999"),
            OwnerUserId = ownerUserId,
            Name = "AI Integration Bot",
            StrategyKey = strategy.StrategyKey,
            Symbol = "BTCUSDT",
            IsEnabled = true
        });
        dbContext.TradingStrategies.Add(strategy);
        dbContext.TradingStrategyVersions.Add(new TradingStrategyVersion
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            TradingStrategyId = strategy.Id,
            SchemaVersion = 2,
            VersionNumber = 1,
            Status = StrategyVersionStatus.Published,
            DefinitionJson = CreateDefinitionJson(),
            PublishedAtUtc = nowUtc.AddMinutes(-5)
        });
        dbContext.RiskProfiles.Add(new RiskProfile
        {
            OwnerUserId = ownerUserId,
            ProfileName = "Balanced",
            MaxDailyLossPercentage = 5m,
            MaxPositionSizePercentage = 80m,
            MaxLeverage = 2m
        });
        dbContext.DemoWallets.Add(new DemoWallet
        {
            OwnerUserId = ownerUserId,
            Asset = "USDT",
            AvailableBalance = 10000m,
            ReservedBalance = 0m,
            LastActivityAtUtc = nowUtc
        });
    }

    private static StrategyEvaluationContext CreateContext(DateTime nowUtc)
    {
        return new StrategyEvaluationContext(
            ExecutionEnvironment.Demo,
            new StrategyIndicatorSnapshot(
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                OpenTimeUtc: nowUtc.AddMinutes(-1),
                CloseTimeUtc: nowUtc,
                ReceivedAtUtc: nowUtc.AddSeconds(1),
                SampleCount: 240,
                RequiredSampleCount: 120,
                State: IndicatorDataState.Ready,
                DataQualityReasonCode: DegradedModeReasonCode.None,
                Rsi: new RelativeStrengthIndexSnapshot(14, IsReady: true, Value: 28m),
                Macd: new MovingAverageConvergenceDivergenceSnapshot(12, 26, 9, true, 1.4m, 1.1m, 0.3m),
                Bollinger: new BollingerBandsSnapshot(20, 2m, true, 62000m, 62500m, 61500m, 250m),
                Source: "integration-test"));
    }

    private static IReadOnlyList<MarketCandleSnapshot> CreateCandles(DateTime startOpenTimeUtc)
    {
        return Enumerable.Range(0, 240)
            .Select(index =>
            {
                var openTimeUtc = startOpenTimeUtc.AddMinutes(index);
                var closePrice = 64000m + (index * 12m);
                return new MarketCandleSnapshot(
                    "BTCUSDT",
                    "1m",
                    openTimeUtc,
                    openTimeUtc.AddMinutes(1).AddMilliseconds(-1),
                    closePrice - 3m,
                    closePrice + 5m,
                    closePrice - 8m,
                    closePrice,
                    120m + index,
                    IsClosed: true,
                    ReceivedAtUtc: openTimeUtc.AddMinutes(1),
                    Source: "integration-candles");
            })
            .ToArray();
    }

    private static string CreateDefinitionJson()
    {
        return
            """
            {
              "schemaVersion": 2,
              "metadata": {
                "templateKey": "ai-int-template",
                "templateName": "AI Integration Template"
              },
              "entry": {
                "operator": "all",
                "rules": [
                  {
                    "path": "context.mode",
                    "comparison": "equals",
                    "value": "Demo"
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
            """;
    }

    private static ApplicationDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }

    private sealed class EmptyHistoricalKlineClient : IBinanceHistoricalKlineClient
    {
        public Task<IReadOnlyCollection<MarketCandleSnapshot>> GetClosedCandlesAsync(string symbol, string interval, DateTime startOpenTimeUtc, DateTime endOpenTimeUtc, int limit, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyCollection<MarketCandleSnapshot>>(Array.Empty<MarketCandleSnapshot>());
        }
    }

    private sealed class FakeAlertService : IAlertService
    {
        public Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }
}

