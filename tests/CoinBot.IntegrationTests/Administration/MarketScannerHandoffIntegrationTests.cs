using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Risk;
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
                BestCandidateScore = 95m,
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
            var executionEngine = new FakeExecutionEngine(options, nowUtc.UtcDateTime);
            var services = new ServiceCollection();
            services.AddScoped<IDataScopeContextAccessor, TestDataScopeContextAccessor>();
            services.AddScoped(provider => new ApplicationDbContext(options, provider.GetRequiredService<IDataScopeContextAccessor>()));
            services.AddSingleton<IStrategySignalService>(strategySignalService);
            services.AddSingleton<IExecutionGate>(new FakeExecutionGate(nowUtc.UtcDateTime));
            services.AddSingleton<IUserExecutionOverrideGuard>(new FakeUserExecutionOverrideGuard());
            services.AddSingleton<IExecutionEngine>(executionEngine);
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
            var readModelService = new AdminMonitoringReadModelService(dbContext, new MemoryCache(new MemoryCacheOptions()), new FixedTimeProvider(nowUtc), Options.Create(new DataLatencyGuardOptions()));
            var dashboardSnapshot = await readModelService.GetSnapshotAsync();

            Assert.Equal("BTCUSDT", persistedAttempt.SelectedSymbol);
            Assert.Equal("Prepared", persistedAttempt.ExecutionRequestStatus);
            Assert.Equal("Persisted", persistedAttempt.StrategyDecisionOutcome);
            var executionOrder = await dbContext.ExecutionOrders.AsNoTracking().SingleAsync(entity => entity.StrategySignalId == persistedAttempt.StrategySignalId);
            Assert.Equal(ExecutionEnvironment.Live, executionOrder.ExecutionEnvironment);
            Assert.Equal(ExecutionOrderExecutorKind.Binance, executionOrder.ExecutorKind);
            Assert.Equal(strategyId, persistedAttempt.TradingStrategyId);
            Assert.Equal(strategyVersionId, persistedAttempt.TradingStrategyVersionId);
            Assert.Equal(ExecutionOrderSide.Buy, persistedAttempt.ExecutionSide);
            Assert.Equal(ExecutionEnvironment.Live, persistedAttempt.ExecutionEnvironment);
            Assert.Null(persistedAttempt.BlockerCode);
            Assert.Equal("BTCUSDT", strategySignalService.LastRequest?.EvaluationContext.IndicatorSnapshot.Symbol);
            Assert.Equal("BTCUSDT", dashboardSnapshot.MarketScanner.LatestHandoff.SelectedSymbol);
            Assert.Equal("Prepared", dashboardSnapshot.MarketScanner.LatestHandoff.ExecutionRequestStatus);
            Assert.Equal("Persisted", dashboardSnapshot.MarketScanner.LatestHandoff.StrategyDecisionOutcome);
            Assert.Equal("Allow", dashboardSnapshot.MarketScanner.LatestHandoff.DecisionOutcome);
            Assert.Equal("Allow", dashboardSnapshot.MarketScanner.LatestHandoff.DecisionReasonType);
            Assert.Equal("Allowed", dashboardSnapshot.MarketScanner.LatestHandoff.DecisionReasonCode);
            Assert.Equal("Execution decision allowed the request.", dashboardSnapshot.MarketScanner.LatestHandoff.DecisionSummary);
            Assert.Equal(nowUtc.UtcDateTime, dashboardSnapshot.MarketScanner.LatestHandoff.MarketDataLastCandleAtUtc);
            Assert.Equal(0, dashboardSnapshot.MarketScanner.LatestHandoff.MarketDataAgeMilliseconds);
            Assert.Equal(3000, dashboardSnapshot.MarketScanner.LatestHandoff.MarketDataStaleThresholdMilliseconds);
            Assert.Equal("Continuity OK", dashboardSnapshot.MarketScanner.LatestHandoff.ContinuityState);
            Assert.Equal("BTCUSDT", dashboardSnapshot.MarketScanner.LastSuccessfulHandoff.SelectedSymbol);
        }
        finally
        {
            await dbContext.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task MarketScannerHandoffService_PersistsRiskVetoSnapshotAndAdminReadModel_OnSqlServer()
    {
        var databaseName = $"CoinBotMarketScannerRiskHandoffInt_{Guid.NewGuid():N}";
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
                Id = "user-risk",
                UserName = "user-risk",
                NormalizedUserName = "USER-RISK",
                Email = "user-risk@coinbot.test",
                NormalizedEmail = "USER-RISK@COINBOT.TEST",
                FullName = "Scanner Handoff Risk User"
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
                Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                ScanCycleId = scanCycleId,
                Symbol = "BTCUSDT",
                UniverseSource = "integration-test",
                ObservedAtUtc = nowUtc.UtcDateTime,
                LastCandleAtUtc = nowUtc.UtcDateTime,
                LastPrice = 100m,
                QuoteVolume24h = 250_000m,
                MarketScore = 90m,
                StrategyScore = 90,
                ScoringSummary = "MarketScore=90; StrategyScore=90; CompositeScore=90.",
                IsEligible = true,
                Score = 90m,
                Rank = 1,
                IsTopCandidate = true
            });
            dbContext.TradingStrategies.Add(new TradingStrategy
            {
                Id = strategyId,
                OwnerUserId = "user-risk",
                StrategyKey = "scanner-handoff-risk",
                DisplayName = "Scanner Handoff Risk",
                PromotionState = StrategyPromotionState.LivePublished,
                PublishedMode = ExecutionEnvironment.Live,
                PublishedAtUtc = nowUtc.UtcDateTime.AddMinutes(-1)
            });
            dbContext.TradingStrategyVersions.Add(new TradingStrategyVersion
            {
                Id = strategyVersionId,
                OwnerUserId = "user-risk",
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
                OwnerUserId = "user-risk",
                Name = "Scanner Handoff Risk Bot",
                StrategyKey = "scanner-handoff-risk",
                Symbol = "BTCUSDT",
                IsEnabled = true
            });
            await dbContext.SaveChangesAsync();

            var strategySignalService = new FakeStrategySignalService(CreateSignal(strategyId, strategyVersionId, "BTCUSDT", "1m", nowUtc.UtcDateTime));
            var riskSnapshot = new PreTradeRiskSnapshot(
                IsVirtualCheck: false,
                RiskProfileId: Guid.NewGuid(),
                RiskProfileName: "Scanner Handoff Risk",
                KillSwitchEnabled: false,
                CurrentEquity: 1000m,
                CurrentGrossExposure: 100m,
                CurrentLeverage: 0.1m,
                CurrentExposurePercentage: 10m,
                CurrentDailyLossAmount: 60m,
                CurrentDailyLossPercentage: 6m,
                MaxDailyLossPercentage: 5m,
                MaxExposurePercentage: 100m,
                MaxLeverage: 2m,
                OpenPositionCount: 1,
                EvaluatedAtUtc: nowUtc.UtcDateTime,
                OwnerUserId: "user-risk",
                BotId: botId,
                Symbol: "BTCUSDT",
                BaseAsset: "BTC",
                Timeframe: "1m",
                Side: ExecutionOrderSide.Buy,
                RequestedQuantity: 1m,
                RequestedPrice: 100m,
                RequestedNotional: 100m,
                CurrentWeeklyLossAmount: 60m,
                CurrentWeeklyLossPercentage: 6m,
                MaxWeeklyLossPercentage: 20m,
                ProjectedGrossExposure: 200m,
                ProjectedLeverage: 0.2m,
                ProjectedExposurePercentage: 20m,
                CurrentSymbolExposureAmount: 100m,
                ProjectedSymbolExposureAmount: 200m,
                CurrentSymbolExposurePercentage: 10m,
                ProjectedSymbolExposurePercentage: 20m,
                MaxSymbolExposurePercentage: 15m,
                ProjectedOpenPositionCount: 1,
                MaxConcurrentPositions: 2,
                CurrentCoinExposureAmount: 100m,
                ProjectedCoinExposureAmount: 200m,
                CurrentCoinExposurePercentage: 10m,
                ProjectedCoinExposurePercentage: 20m,
                MaxCoinExposurePercentage: 25m);
            var services = new ServiceCollection();
            services.AddScoped<IDataScopeContextAccessor, TestDataScopeContextAccessor>();
            services.AddScoped(provider => new ApplicationDbContext(options, provider.GetRequiredService<IDataScopeContextAccessor>()));
            services.AddSingleton<IStrategySignalService>(strategySignalService);
            services.AddSingleton<IExecutionGate>(new FakeExecutionGate(nowUtc.UtcDateTime));
            services.AddSingleton<IExecutionEngine>(new FakeExecutionEngine(options, nowUtc.UtcDateTime));
            services.AddSingleton<IUserExecutionOverrideGuard>(new FakeUserExecutionOverrideGuard(
                new UserExecutionOverrideEvaluationResult(
                    true,
                    "UserExecutionRiskSymbolExposureLimitBreached",
                    "Execution blocked because risk policy vetoed the order: SymbolExposureLimitBreached. Reason=SymbolExposureLimitBreached; Scope=User:user-risk/Bot:" + botId + "/Symbol:BTCUSDT/Coin:BTC/Timeframe:1m.",
                    new RiskVetoResult(
                        true,
                        RiskVetoReasonCode.SymbolExposureLimitBreached,
                        riskSnapshot,
                        "Reason=SymbolExposureLimitBreached; Scope=User:user-risk/Bot:" + botId + "/Symbol:BTCUSDT/Coin:BTC/Timeframe:1m; SymbolExposure=10->20/15%; OpenPositions=1->1/2."))));
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
            var readModelService = new AdminMonitoringReadModelService(dbContext, new MemoryCache(new MemoryCacheOptions()), new FixedTimeProvider(nowUtc), Options.Create(new DataLatencyGuardOptions()));
            var dashboardSnapshot = await readModelService.GetSnapshotAsync();

            Assert.Equal("Blocked", persistedAttempt.ExecutionRequestStatus);
            Assert.Equal("UserExecutionRiskSymbolExposureLimitBreached", persistedAttempt.BlockerCode);
            Assert.Equal("Vetoed", persistedAttempt.RiskOutcome);
            Assert.Equal("SymbolExposureLimitBreached", persistedAttempt.RiskVetoReasonCode);
            Assert.Equal(6m, persistedAttempt.RiskCurrentDailyLossPercentage);
            Assert.Equal(5m, persistedAttempt.RiskMaxDailyLossPercentage);
            Assert.Equal(6m, persistedAttempt.RiskCurrentWeeklyLossPercentage);
            Assert.Equal(20m, persistedAttempt.RiskMaxWeeklyLossPercentage);
            Assert.Equal(0.1m, persistedAttempt.RiskCurrentLeverage);
            Assert.Equal(0.2m, persistedAttempt.RiskProjectedLeverage);
            Assert.Equal(2m, persistedAttempt.RiskMaxLeverage);
            Assert.Equal(10m, persistedAttempt.RiskCurrentSymbolExposurePercentage);
            Assert.Equal(20m, persistedAttempt.RiskProjectedSymbolExposurePercentage);
            Assert.Equal(15m, persistedAttempt.RiskMaxSymbolExposurePercentage);
            Assert.Equal(1, persistedAttempt.RiskCurrentOpenPositions);
            Assert.Equal(1, persistedAttempt.RiskProjectedOpenPositions);
            Assert.Equal(2, persistedAttempt.RiskMaxConcurrentPositions);
            Assert.Equal("BTC", persistedAttempt.RiskBaseAsset);
            Assert.Equal(10m, persistedAttempt.RiskCurrentCoinExposurePercentage);
            Assert.Equal(20m, persistedAttempt.RiskProjectedCoinExposurePercentage);
            Assert.Equal(25m, persistedAttempt.RiskMaxCoinExposurePercentage);
            Assert.Contains("Reason=SymbolExposureLimitBreached", persistedAttempt.RiskSummary, StringComparison.Ordinal);

            Assert.Equal("Blocked", dashboardSnapshot.MarketScanner.LatestHandoff.ExecutionRequestStatus);
            Assert.Equal("UserExecutionRiskSymbolExposureLimitBreached", dashboardSnapshot.MarketScanner.LatestHandoff.BlockerCode);
            Assert.Equal("Vetoed", dashboardSnapshot.MarketScanner.LatestHandoff.RiskOutcome);
            Assert.Equal("SymbolExposureLimitBreached", dashboardSnapshot.MarketScanner.LatestHandoff.RiskVetoReasonCode);
            Assert.Equal("Block", dashboardSnapshot.MarketScanner.LatestHandoff.DecisionOutcome);
            Assert.Equal("RiskVeto", dashboardSnapshot.MarketScanner.LatestHandoff.DecisionReasonType);
            Assert.Equal("UserExecutionRiskSymbolExposureLimitBreached", dashboardSnapshot.MarketScanner.LatestHandoff.DecisionReasonCode);
            Assert.Equal(6m, dashboardSnapshot.MarketScanner.LatestHandoff.RiskCurrentDailyLossPercentage);
            Assert.Equal(20m, dashboardSnapshot.MarketScanner.LatestHandoff.RiskProjectedSymbolExposurePercentage);
            Assert.Equal("BTC", dashboardSnapshot.MarketScanner.LatestHandoff.RiskBaseAsset);
            Assert.Contains("UserExecutionOverrideGuard=UserExecutionRiskSymbolExposureLimitBreached", dashboardSnapshot.MarketScanner.LatestHandoff.GuardSummary, StringComparison.Ordinal);
            Assert.Contains("RiskSummary=Reason=SymbolExposureLimitBreached", dashboardSnapshot.MarketScanner.LatestHandoff.GuardSummary, StringComparison.Ordinal);
        }
        finally
        {
            await dbContext.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task MarketScannerHandoffService_ProjectsStaleAndContinuityBlocks_Separately_OnSqlServer()
    {
        await using var staleHarness = await CreateDecisionHarnessAsync(
            $"CoinBotMarketScannerStaleInt_{Guid.NewGuid():N}",
            "user-stale",
            "scanner-handoff-stale",
            "BTCUSDT",
            new FakeExecutionGate(
                new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc),
                blockedSymbol: "BTCUSDT",
                blockedReason: ExecutionGateBlockedReason.StaleMarketData,
                blockedMessage: "Execution blocked because market data is stale. LatencyReason=MarketDataLatencyBreached; Symbol=BTCUSDT; Timeframe=1m; LastCandleAtUtc=2026-04-03T11:59:00.0000000Z; DataAgeMs=60000; ContinuityGapCount=0"));

        var staleAttempt = await staleHarness.Service.RunOnceAsync(staleHarness.ScanCycleId);
        var staleDashboard = await staleHarness.ReadModelService.GetSnapshotAsync();

        Assert.Equal("StaleMarketData", staleAttempt.BlockerCode);
        Assert.Equal("Block", staleDashboard.MarketScanner.LatestHandoff.DecisionOutcome);
        Assert.Equal("StaleData", staleDashboard.MarketScanner.LatestHandoff.DecisionReasonType);
        Assert.Equal("StaleMarketData", staleDashboard.MarketScanner.LatestHandoff.DecisionReasonCode);
        Assert.Equal("Execution blocked because market data is stale.", staleDashboard.MarketScanner.LatestHandoff.DecisionSummary);

        await staleHarness.DisposeAsync();

        await using var continuityHarness = await CreateDecisionHarnessAsync(
            $"CoinBotMarketScannerContinuityInt_{Guid.NewGuid():N}",
            "user-continuity",
            "scanner-handoff-continuity",
            "BTCUSDT",
            new FakeExecutionGate(
                new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc),
                blockedSymbol: "BTCUSDT",
                blockedReason: ExecutionGateBlockedReason.ContinuityGap,
                blockedMessage: "Execution blocked because the candle continuity guard is active. LatencyReason=CandleDataGapDetected; Symbol=BTCUSDT; Timeframe=1m; LastCandleAtUtc=2026-04-03T11:59:00.0000000Z; DataAgeMs=60000; ContinuityGapCount=2"));
        continuityHarness.DbContext.DegradedModeStates.Add(new DegradedModeState
        {
            Id = DegradedModeDefaults.ResolveStateId("BTCUSDT", "1m"),
            StateCode = DegradedModeStateCode.Normal,
            ReasonCode = DegradedModeReasonCode.None,
            SignalFlowBlocked = false,
            ExecutionFlowBlocked = false,
            LatestSymbol = "BTCUSDT",
            LatestTimeframe = "1m",
            LatestDataTimestampAtUtc = new DateTime(2026, 4, 3, 11, 59, 0, DateTimeKind.Utc),
            LatestExpectedOpenTimeUtc = new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc),
            LatestContinuityGapCount = 2,
            LatestContinuityGapStartedAtUtc = new DateTime(2026, 4, 3, 11, 57, 0, DateTimeKind.Utc),
            LatestContinuityGapLastSeenAtUtc = new DateTime(2026, 4, 3, 11, 59, 30, DateTimeKind.Utc),
            LatestContinuityRecoveredAtUtc = new DateTime(2026, 4, 3, 11, 59, 45, DateTimeKind.Utc)
        });
        await continuityHarness.DbContext.SaveChangesAsync();

        var continuityAttempt = await continuityHarness.Service.RunOnceAsync(continuityHarness.ScanCycleId);
        var continuityDashboard = await continuityHarness.ReadModelService.GetSnapshotAsync();

        Assert.Equal("ContinuityGap", continuityAttempt.BlockerCode);
        Assert.Equal("Block", continuityDashboard.MarketScanner.LatestHandoff.DecisionOutcome);
        Assert.Equal("ContinuityGap", continuityDashboard.MarketScanner.LatestHandoff.DecisionReasonType);
        Assert.Equal("ContinuityGap", continuityDashboard.MarketScanner.LatestHandoff.DecisionReasonCode);
        Assert.Equal("Recovered after backfill", continuityDashboard.MarketScanner.LatestHandoff.ContinuityState);
        Assert.Equal(2, continuityDashboard.MarketScanner.LatestHandoff.ContinuityGapCount);
        Assert.Equal(new DateTime(2026, 4, 3, 11, 57, 0, DateTimeKind.Utc), continuityDashboard.MarketScanner.LatestHandoff.ContinuityGapStartedAtUtc);
        Assert.Equal(new DateTime(2026, 4, 3, 11, 59, 30, DateTimeKind.Utc), continuityDashboard.MarketScanner.LatestHandoff.ContinuityGapLastSeenAtUtc);
        Assert.Equal(new DateTime(2026, 4, 3, 11, 59, 45, DateTimeKind.Utc), continuityDashboard.MarketScanner.LatestHandoff.ContinuityRecoveredAtUtc);
    }

    [Fact]
    public async Task MarketScannerHandoffService_SeparatesGlobalExecutionOff_FromRiskVeto_OnSqlServer()
    {
        await using var harness = await CreateDecisionHarnessAsync(
            $"CoinBotMarketScannerGlobalOffInt_{Guid.NewGuid():N}",
            "user-global-off",
            "scanner-handoff-global-off",
            "BTCUSDT",
            new FakeExecutionGate(
                new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc),
                blockedSymbol: "BTCUSDT",
                blockedReason: ExecutionGateBlockedReason.TradeMasterDisarmed,
                blockedMessage: "Execution blocked because TradeMaster is disarmed."));

        await harness.Service.RunOnceAsync(harness.ScanCycleId);
        var dashboardSnapshot = await harness.ReadModelService.GetSnapshotAsync();

        Assert.Equal("Block", dashboardSnapshot.MarketScanner.LatestHandoff.DecisionOutcome);
        Assert.Equal("GlobalExecutionOff", dashboardSnapshot.MarketScanner.LatestHandoff.DecisionReasonType);
        Assert.Equal("TradeMasterDisarmed", dashboardSnapshot.MarketScanner.LatestHandoff.DecisionReasonCode);
        Assert.NotEqual("RiskVeto", dashboardSnapshot.MarketScanner.LatestHandoff.DecisionReasonType);
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

    private static StrategySignalSnapshot CreateSignal(
        Guid strategyId,
        Guid versionId,
        string symbol,
        string timeframe,
        DateTime generatedAtUtc,
        StrategyTradeDirection direction = StrategyTradeDirection.Long)
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
                signal.ExplainabilityPayload.RuleResultSnapshot,
                [signal],
                [],
                0));
        }

        public Task<StrategySignalSnapshot?> GetAsync(Guid strategySignalId, CancellationToken cancellationToken = default) => Task.FromResult<StrategySignalSnapshot?>(signal);

        public Task<StrategySignalVetoSnapshot?> GetVetoAsync(Guid strategySignalVetoId, CancellationToken cancellationToken = default) => Task.FromResult<StrategySignalVetoSnapshot?>(null);
    }

    private sealed class FakeExecutionGate(
        DateTime nowUtc,
        string? blockedSymbol = null,
        ExecutionGateBlockedReason? blockedReason = null,
        string? blockedMessage = null) : IExecutionGate
    {
        public Task<GlobalExecutionSwitchSnapshot> EnsureExecutionAllowedAsync(ExecutionGateRequest request, CancellationToken cancellationToken = default)
        {
            if (blockedReason.HasValue &&
                string.Equals(request.Symbol, blockedSymbol, StringComparison.Ordinal))
            {
                throw new ExecutionGateRejectedException(
                    blockedReason.Value,
                    request.Environment,
                    blockedMessage ?? $"Execution blocked because {blockedReason.Value}.");
            }

            return Task.FromResult(new GlobalExecutionSwitchSnapshot(TradeMasterSwitchState.Armed, false, true, nowUtc));
        }
    }

    private sealed class FakeUserExecutionOverrideGuard(UserExecutionOverrideEvaluationResult? evaluationResult = null) : IUserExecutionOverrideGuard
    {
        public Task<UserExecutionOverrideEvaluationResult> EvaluateAsync(UserExecutionOverrideEvaluationRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(evaluationResult ?? new UserExecutionOverrideEvaluationResult(false, null, null));
        }
    }

    private sealed class FakeExecutionEngine(DbContextOptions<ApplicationDbContext> options, DateTime nowUtc) : IExecutionEngine
    {
        public async Task<ExecutionDispatchResult> DispatchAsync(ExecutionCommand command, CancellationToken cancellationToken = default)
        {
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

    private static async Task<DecisionHarness> CreateDecisionHarnessAsync(
        string databaseName,
        string ownerUserId,
        string strategyKey,
        string symbol,
        IExecutionGate executionGate)
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(connectionString).Options;
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContextAccessor());
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        var scanCycleId = Guid.NewGuid();
        var strategyId = Guid.NewGuid();
        var strategyVersionId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        dbContext.Users.Add(new ApplicationUser
        {
            Id = ownerUserId,
            UserName = ownerUserId,
            NormalizedUserName = ownerUserId.ToUpperInvariant(),
            Email = $"{ownerUserId}@coinbot.test",
            NormalizedEmail = $"{ownerUserId}@coinbot.test".ToUpperInvariant(),
            FullName = ownerUserId
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
            BestCandidateSymbol = symbol,
            BestCandidateScore = 250_000m,
            Summary = "integration-test"
        });
        dbContext.MarketScannerCandidates.Add(new MarketScannerCandidate
        {
            Id = Guid.NewGuid(),
            ScanCycleId = scanCycleId,
            Symbol = symbol,
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
            OwnerUserId = ownerUserId,
            StrategyKey = strategyKey,
            DisplayName = strategyKey,
            PromotionState = StrategyPromotionState.LivePublished,
            PublishedMode = ExecutionEnvironment.Live,
            PublishedAtUtc = nowUtc.UtcDateTime.AddMinutes(-1)
        });
        dbContext.TradingStrategyVersions.Add(new TradingStrategyVersion
        {
            Id = strategyVersionId,
            OwnerUserId = ownerUserId,
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
            OwnerUserId = ownerUserId,
            Name = $"{strategyKey}-bot",
            StrategyKey = strategyKey,
            Symbol = symbol,
            IsEnabled = true
        });
        await dbContext.SaveChangesAsync();

        var strategySignalService = new FakeStrategySignalService(CreateSignal(strategyId, strategyVersionId, symbol, "1m", nowUtc.UtcDateTime));
        var services = new ServiceCollection();
        services.AddScoped<IDataScopeContextAccessor, TestDataScopeContextAccessor>();
        services.AddScoped(provider => new ApplicationDbContext(options, provider.GetRequiredService<IDataScopeContextAccessor>()));
        services.AddSingleton<IStrategySignalService>(strategySignalService);
        services.AddSingleton(executionGate);
        services.AddSingleton<IUserExecutionOverrideGuard>(new FakeUserExecutionOverrideGuard());
        services.AddSingleton<IExecutionEngine>(new FakeExecutionEngine(options, nowUtc.UtcDateTime));
        var serviceProvider = services.BuildServiceProvider();
        var handoffService = new MarketScannerHandoffService(
            dbContext,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            new FakeMarketDataService(nowUtc.UtcDateTime),
            new FakeIndicatorDataService(CreateIndicatorSnapshot(symbol, "1m", nowUtc.UtcDateTime)),
            new FakeSharedSymbolRegistry(),
            new FakeDataLatencyCircuitBreaker(nowUtc.UtcDateTime),
            Options.Create(new MarketScannerOptions { HandoffEnabled = true, AllowedQuoteAssets = ["USDT"] }),
            Options.Create(new BinanceMarketDataOptions { KlineInterval = "1m" }),
            Options.Create(new BotExecutionPilotOptions { SignalEvaluationMode = ExecutionEnvironment.Live, PrimeHistoricalCandleCount = 34 }),
            new FixedTimeProvider(nowUtc),
            NullLogger<MarketScannerHandoffService>.Instance);
        var readModelService = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            new FixedTimeProvider(nowUtc),
            Options.Create(new DataLatencyGuardOptions()));

        return new DecisionHarness(dbContext, handoffService, readModelService, serviceProvider, scanCycleId);
    }

    private sealed class DecisionHarness(
        ApplicationDbContext dbContext,
        MarketScannerHandoffService service,
        AdminMonitoringReadModelService readModelService,
        ServiceProvider serviceProvider,
        Guid scanCycleId) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public MarketScannerHandoffService Service { get; } = service;

        public AdminMonitoringReadModelService ReadModelService { get; } = readModelService;

        public Guid ScanCycleId { get; } = scanCycleId;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await DbContext.Database.EnsureDeletedAsync();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                try
                {
                    await DbContext.DisposeAsync();
                }
                catch (ObjectDisposedException)
                {
                }

                await serviceProvider.DisposeAsync();
            }
        }
    }
}




