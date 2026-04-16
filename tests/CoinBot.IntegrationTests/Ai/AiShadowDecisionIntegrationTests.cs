using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Ai;
using CoinBot.Application.Abstractions.Alerts;
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
using CoinBot.Infrastructure.Dashboard;
using CoinBot.Infrastructure.DemoPortfolio;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Features;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Risk;
using CoinBot.Infrastructure.Strategies;
using CoinBot.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.IntegrationTests.Ai;

public sealed class AiShadowDecisionIntegrationTests
{
    [Fact]
    public async Task ProcessAsync_PersistsShadowOnlyDecision_WithoutExecutionOrder_OnSqlServer()
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString($"CoinBotAiShadow_{Guid.NewGuid():N}");
        await using var harness = CreateHarness(connectionString, CreateEnabledAiOptions(), aiSignalEvaluatorOverride: new FixedAiSignalEvaluator(AiSignalDirection.Long, 0.91m, false, null));

        try
        {
            await harness.DbContext.Database.EnsureDeletedAsync();
            await harness.DbContext.Database.MigrateAsync();

            var bot = await SeedBotGraphAsync(harness.DbContext, "shadow-int-user");
            ConfigurePilotScope(harness.PilotOptions, bot);
            await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-ai-shadow-int-1");
            await harness.SwitchService.SetTradeMasterStateAsync(
                TradeMasterSwitchState.Armed,
                actor: "admin-shadow-int",
                context: "Execution open",
                correlationId: "corr-ai-shadow-int-2");

            var result = await harness.Processor.ProcessAsync(bot, "job-ai-shadow-int-1", CancellationToken.None);
            var shadowDecision = await harness.DbContext.AiShadowDecisions.SingleAsync();

            Assert.True(result.IsSuccessful);
            Assert.Equal("ShadowOnly", shadowDecision.FinalAction);
            Assert.True(shadowDecision.HypotheticalSubmitAllowed);
            Assert.Equal("ShadowModeActive", shadowDecision.NoSubmitReason);
            Assert.Empty(harness.DbContext.ExecutionOrders);
            Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
        }
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task ProcessAsync_PersistsNoSubmitShadowDecision_WhenFeatureSnapshotIsUnavailable_OnSqlServer()
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString($"CoinBotAiShadowFallback_{Guid.NewGuid():N}");
        await using var harness = CreateHarness(connectionString, CreateEnabledAiOptions(), new ThrowingFeatureSnapshotService());

        try
        {
            await harness.DbContext.Database.EnsureDeletedAsync();
            await harness.DbContext.Database.MigrateAsync();

            var bot = await SeedBotGraphAsync(harness.DbContext, "shadow-fallback-int-user");
            ConfigurePilotScope(harness.PilotOptions, bot);
            await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-ai-shadow-fallback-1");
            await harness.SwitchService.SetTradeMasterStateAsync(
                TradeMasterSwitchState.Armed,
                actor: "admin-shadow-int",
                context: "Execution open",
                correlationId: "corr-ai-shadow-fallback-2");

            var result = await harness.Processor.ProcessAsync(bot, "job-ai-shadow-fallback-1", CancellationToken.None);
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
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }
    [Fact]
    public async Task ProcessAsync_ProjectsRejectTelemetryToLiveReadModel_WhenTradeMasterIsDisarmed_OnSqlServer()
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString($"CoinBotPilotReject_{Guid.NewGuid():N}");
        await using var harness = CreateHarness(connectionString, new AiSignalOptions());

        try
        {
            await harness.DbContext.Database.EnsureDeletedAsync();
            await harness.DbContext.Database.MigrateAsync();

            var bot = await SeedBotGraphAsync(harness.DbContext, "pilot-reject-int-user");
            ConfigurePilotScope(harness.PilotOptions, bot);
            harness.PilotOptions.PilotActivationEnabled = true;
            await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-pilot-reject-int-1");
            await harness.SwitchService.SetTradeMasterStateAsync(
                TradeMasterSwitchState.Armed,
                actor: "admin-pilot-int",
                context: "Execution open",
                correlationId: "corr-pilot-reject-int-2");
            await harness.SwitchService.SetTradeMasterStateAsync(
                TradeMasterSwitchState.Disarmed,
                actor: "admin-pilot-int",
                context: "Execution frozen",
                correlationId: "corr-pilot-reject-int-3");

            var result = await harness.Processor.ProcessAsync(bot, "job-pilot-reject-int-1", CancellationToken.None);
            var order = await harness.DbContext.ExecutionOrders.SingleAsync();
            var liveReadModel = new UserDashboardLiveReadModelService(
                harness.DbContext,
                harness.SwitchService,
                new AiShadowDecisionService(harness.DbContext, harness.TimeProvider),
                Options.Create(harness.PilotOptions),
                harness.TimeProvider);
            var snapshot = await liveReadModel.GetSnapshotAsync(bot.OwnerUserId);

            Assert.False(result.IsSuccessful);
            Assert.Equal("TradeMasterDisarmed", result.ErrorCode);
            Assert.Equal(ExecutionOrderState.Rejected, order.State);
            Assert.Equal("TradeMasterDisarmed", order.FailureCode);
            Assert.Equal("Disarmed", snapshot.Control.TradeMasterLabel);
            Assert.Equal("PilotEnabled", snapshot.Control.PilotActivationLabel);
            Assert.Equal("TradeMasterDisarmed", snapshot.LatestReject.Code);
            Assert.Contains("Execution blocked", snapshot.LatestReject.Summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task ProcessAsync_SubmitsReconcilesAndProjectsPortfolio_WhenPilotActivationIsEnabled_OnSqlServer()
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString($"CoinBotPilotEnable_{Guid.NewGuid():N}");
        await using var harness = CreateHarness(
            connectionString,
            CreateEnabledAiOptions(),
            aiSignalEvaluatorOverride: new FixedAiSignalEvaluator(AiSignalDirection.Long, 0.91m, false, null));

        try
        {
            await harness.DbContext.Database.EnsureDeletedAsync();
            await harness.DbContext.Database.MigrateAsync();

            var bot = await SeedBotGraphAsync(harness.DbContext, "pilot-enable-int-user");
            ConfigurePilotScope(harness.PilotOptions, bot);
            harness.PilotOptions.PilotActivationEnabled = true;
            await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-pilot-enable-int-1");
            await harness.SwitchService.SetTradeMasterStateAsync(
                TradeMasterSwitchState.Armed,
                actor: "admin-pilot-int",
                context: "Execution open",
                correlationId: "corr-pilot-enable-int-2");

            var result = await harness.Processor.ProcessAsync(bot, "job-pilot-enable-int-1", CancellationToken.None);
            var submittedOrder = await harness.DbContext.ExecutionOrders.SingleAsync();

            Assert.True(result.IsSuccessful);
            Assert.Equal(ExecutionOrderState.Submitted, submittedOrder.State);
            Assert.Equal(ExchangeDataPlane.Futures, submittedOrder.Plane);
            Assert.Equal(ExchangeStateDriftStatus.Unknown, submittedOrder.ReconciliationStatus);
            Assert.Empty(harness.DbContext.AiShadowDecisions);
            Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
            Assert.Equal(0, harness.SpotPrivateRestClient.PlaceOrderCalls);

            harness.TimeProvider.Advance(TimeSpan.FromMinutes(1));
            var filledAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;
            harness.PrivateRestClient.QuerySnapshot = new BinanceOrderStatusSnapshot(
                "BTCUSDT",
                submittedOrder.ExternalOrderId!,
                ExecutionClientOrderId.Create(submittedOrder.Id),
                "FILLED",
                0.002m,
                0.002m,
                130m,
                65000m,
                0.002m,
                65000m,
                filledAtUtc,
                "IntegrationTest.FuturesOrderQuery",
                TradeId: 77,
                FeeAsset: "USDT",
                FeeAmount: 0.13m);

            var auditLogService = new AuditLogService(harness.DbContext, new CorrelationContextAccessor());
            var lifecycleService = new ExecutionOrderLifecycleService(
                harness.DbContext,
                auditLogService,
                harness.TimeProvider,
                NullLogger<ExecutionOrderLifecycleService>.Instance);
            var reconciliationService = new ExecutionReconciliationService(
                harness.DbContext,
                new FakeExchangeCredentialService(),
                harness.PrivateRestClient,
                new FakeSpotPrivateRestClient(harness.TimeProvider),
                lifecycleService,
                NullLogger<ExecutionReconciliationService>.Instance);

            var reconciledCount = await reconciliationService.RunOnceAsync();

            var accountSnapshot = new ExchangeAccountSnapshot(
                submittedOrder.ExchangeAccountId!.Value,
                bot.OwnerUserId,
                "Binance",
                [
                    new ExchangeBalanceSnapshot(
                        "USDT",
                        999.87m,
                        999.87m,
                        999.87m,
                        999.87m,
                        filledAtUtc,
                        0m,
                        ExchangeDataPlane.Futures)
                ],
                [
                    new ExchangePositionSnapshot(
                        "BTCUSDT",
                        "LONG",
                        0.002m,
                        65000m,
                        65000m,
                        10m,
                        "cross",
                        0m,
                        filledAtUtc,
                        ExchangeDataPlane.Futures)
                ],
                filledAtUtc,
                filledAtUtc,
                "IntegrationTest.FuturesAccount",
                ExchangeDataPlane.Futures);
            var balanceSyncService = new ExchangeBalanceSyncService(harness.DbContext, NullLogger<ExchangeBalanceSyncService>.Instance);
            var positionSyncService = new ExchangePositionSyncService(harness.DbContext, NullLogger<ExchangePositionSyncService>.Instance);
            var syncStateService = new ExchangeAccountSyncStateService(harness.DbContext);

            await balanceSyncService.ApplyAsync(accountSnapshot);
            await positionSyncService.ApplyAsync(accountSnapshot);
            await syncStateService.RecordBalanceSyncAsync(accountSnapshot);
            await syncStateService.RecordPositionSyncAsync(accountSnapshot);

            var persistedOrder = await harness.DbContext.ExecutionOrders.SingleAsync();
            var portfolio = await new UserDashboardPortfolioReadModelService(harness.DbContext).GetSnapshotAsync(bot.OwnerUserId);
            var futuresPosition = Assert.Single(portfolio.Positions, entity => entity.Symbol == "BTCUSDT" && entity.Plane == ExchangeDataPlane.Futures);
            var futuresHistory = Assert.Single(portfolio.TradeHistory, entity => entity.Symbol == "BTCUSDT" && entity.Plane == ExchangeDataPlane.Futures);

            Assert.Equal(1, reconciledCount);
            Assert.Equal(1, harness.PrivateRestClient.GetOrderCalls);
            Assert.Equal(ExecutionOrderState.Filled, persistedOrder.State);
            Assert.Equal(0.002m, persistedOrder.FilledQuantity);
            Assert.Equal(65000m, persistedOrder.AverageFillPrice);
            Assert.Equal(ExchangeStateDriftStatus.DriftDetected, persistedOrder.ReconciliationStatus);
            Assert.Contains("LocalState=Submitted", persistedOrder.ReconciliationSummary, StringComparison.Ordinal);
            Assert.Contains(portfolio.Balances, entity => entity.Asset == "USDT" && entity.Plane == ExchangeDataPlane.Futures);
            Assert.Equal(10m, portfolio.UnrealizedPnl);
            Assert.Equal(10m, futuresPosition.UnrealizedProfit);
            Assert.Equal(70000m, futuresPosition.MarkPrice);
            Assert.Equal(0.13m, futuresHistory.FeeAmountInQuote);
            Assert.Equal("77", futuresHistory.TradeIdsSummary);
            Assert.Contains("TradeId=77", futuresHistory.ExecutionResultSummary, StringComparison.Ordinal);
            Assert.Contains("ReconciliationStatus=DriftDetected", futuresHistory.ReasonChainSummary, StringComparison.Ordinal);
            Assert.Contains("ReconciliationSummary=LocalState=Submitted", futuresHistory.ExecutionResultSummary, StringComparison.Ordinal);
        }
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }
    private static TestHarness CreateHarness(
        string connectionString,
        AiSignalOptions aiSignalOptions,
        ITradingFeatureSnapshotService? featureSnapshotServiceOverride = null,
        IAiSignalEvaluator? aiSignalEvaluatorOverride = null)
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 6, 12, 0, 0, TimeSpan.Zero));
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var correlationContextAccessor = new CorrelationContextAccessor();
        var auditLogService = new AuditLogService(dbContext, correlationContextAccessor);
        var switchService = new GlobalExecutionSwitchService(dbContext, auditLogService);
        var globalSystemStateService = new GlobalSystemStateService(dbContext, auditLogService, timeProvider);
        var marketDataService = new FakeMarketDataService(timeProvider);
        var demoWalletValuationService = new DemoWalletValuationService(marketDataService, timeProvider, NullLogger<DemoWalletValuationService>.Instance);
        var latencyOptions = Options.Create(new DataLatencyGuardOptions());
        var circuitBreaker = new DataLatencyCircuitBreaker(dbContext, new FakeAlertService(), latencyOptions, timeProvider, NullLogger<DataLatencyCircuitBreaker>.Instance);
        var tradingModeService = new TradingModeService(dbContext, auditLogService);
        var demoSessionService = new DemoSessionService(
            dbContext,
            new DemoConsistencyWatchdogService(dbContext, Options.Create(new DemoSessionOptions()), timeProvider, NullLogger<DemoConsistencyWatchdogService>.Instance),
            demoWalletValuationService,
            auditLogService,
            Options.Create(new DemoSessionOptions()),
            timeProvider,
            NullLogger<DemoSessionService>.Instance);
        var hostEnvironment = new TestHostEnvironment(Environments.Development);
        var traceService = new TraceService(dbContext, correlationContextAccessor, timeProvider);
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
        var riskPolicyEvaluator = new RiskPolicyEvaluator(dbContext, timeProvider, NullLogger<RiskPolicyEvaluator>.Instance);
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
        var lifecycleService = new ExecutionOrderLifecycleService(dbContext, auditLogService, timeProvider, NullLogger<ExecutionOrderLifecycleService>.Instance);
        var demoPortfolioAccountingService = new DemoPortfolioAccountingService(dbContext, demoSessionService, demoWalletValuationService, timeProvider, NullLogger<DemoPortfolioAccountingService>.Instance);
        var demoFillSimulator = new DemoFillSimulator(marketDataService, Options.Create(new DemoFillSimulatorOptions()), timeProvider, NullLogger<DemoFillSimulator>.Instance);
        var credentialService = new FakeExchangeCredentialService();
        var privateRestClient = new FakePrivateRestClient(timeProvider);
        var spotPrivateRestClient = new FakeSpotPrivateRestClient(timeProvider);
        var strategySignalService = new StrategySignalService(
            dbContext,
            new StrategyEvaluatorService(new StrategyRuleParser()),
            riskPolicyEvaluator,
            traceService,
            correlationContextAccessor,
            aiSignalEvaluatorOverride ?? new AiSignalEvaluator(
                [new DeterministicStubAiSignalProviderAdapter(), new OfflineAiSignalProviderAdapter(), new OpenAiSignalProviderAdapter(), new GeminiAiSignalProviderAdapter()],
                Options.Create(aiSignalOptions),
                timeProvider,
                NullLogger<AiSignalEvaluator>.Instance),
            Options.Create(aiSignalOptions),
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
            new BinanceExecutor(dbContext, credentialService, privateRestClient, NullLogger<BinanceExecutor>.Instance, marketDataService: marketDataService),
            new BinanceSpotExecutor(dbContext, credentialService, spotPrivateRestClient, NullLogger<BinanceSpotExecutor>.Instance, marketDataService: marketDataService),
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
            new IndicatorDataService(marketDataService, new IndicatorStreamHub(), Options.Create(new IndicatorEngineOptions()), NullLogger<IndicatorDataService>.Instance),
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
            traceService,
            correlationContextAccessor,
            Options.Create(pilotOptions),
            Options.Create(aiSignalOptions),
            hostEnvironment,
            timeProvider,
            NullLogger<BotWorkerJobProcessor>.Instance);

        return new TestHarness(dbContext, processor, switchService, circuitBreaker, timeProvider, privateRestClient, spotPrivateRestClient, pilotOptions);
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
        public Task<AiSignalEvaluationResult> EvaluateAsync(AiSignalEvaluationRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nowUtc = request.FeatureSnapshot?.EvaluatedAtUtc ?? new DateTime(2026, 4, 6, 12, 0, 0, DateTimeKind.Utc);

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
    private static async Task<TradingBot> SeedBotGraphAsync(ApplicationDbContext dbContext, string ownerUserId)
    {
        var observedAtUtc = new DateTime(2026, 4, 6, 11, 59, 0, DateTimeKind.Utc);
        var apiCredentialId = Guid.NewGuid();
        var strategy = new TradingStrategy
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            StrategyKey = "shadow-core",
            DisplayName = "Shadow Core"
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
            PublishedAtUtc = observedAtUtc.AddMinutes(-10)
        };
        var exchangeAccountId = Guid.NewGuid();
        var bot = new TradingBot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = "Shadow Bot",
            StrategyKey = strategy.StrategyKey,
            Symbol = "BTCUSDT",
            ExchangeAccountId = exchangeAccountId,
            IsEnabled = true
        };
        dbContext.Users.Add(new ApplicationUser
        {
            Id = ownerUserId,
            UserName = ownerUserId,
            NormalizedUserName = ownerUserId.ToUpperInvariant(),
            Email = $"{ownerUserId}@coinbot.test",
            NormalizedEmail = $"{ownerUserId}@coinbot.test".ToUpperInvariant(),
            FullName = ownerUserId,
            EmailConfirmed = true
        });
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
            DisplayName = "Shadow Futures",
            IsReadOnly = false,
            CredentialStatus = ExchangeCredentialStatus.Active,
            ApiKeyCiphertext = "cipher-api-key",
            ApiSecretCiphertext = "cipher-api-secret"
        });
        dbContext.ApiCredentials.Add(new ApiCredential
        {
            Id = apiCredentialId,
            ExchangeAccountId = exchangeAccountId,
            OwnerUserId = ownerUserId,
            ApiKeyCiphertext = "cipher-api-key",
            ApiSecretCiphertext = "cipher-api-secret",
            CredentialFingerprint = "shadow-fingerprint",
            KeyVersion = "credential-v1",
            EncryptedBlobVersion = 1,
            ValidationStatus = "Valid",
            PermissionSummary = "Trade=Y; Futures=Y; Testnet=Y",
            StoredAtUtc = observedAtUtc,
            LastValidatedAtUtc = observedAtUtc
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
            ApiCredentialId = apiCredentialId,
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

    private static void ConfigurePilotScope(BotExecutionPilotOptions pilotOptions, TradingBot bot)
    {
        pilotOptions.AllowedUserIds = [bot.OwnerUserId];
        pilotOptions.AllowedBotIds = [bot.Id.ToString("N")];
        pilotOptions.AllowedSymbols = [MarketDataSymbolNormalizer.Normalize(bot.Symbol)];
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

    private sealed class ThrowingFeatureSnapshotService : ITradingFeatureSnapshotService
    {
        public Task<TradingFeatureSnapshotModel> CaptureAsync(TradingFeatureCaptureRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Feature snapshot capture failed.");
        }

        public Task<TradingFeatureSnapshotModel?> GetLatestAsync(string userId, Guid botId, string symbol, string timeframe, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<TradingFeatureSnapshotModel?>(null);
        }

        public Task<IReadOnlyCollection<TradingFeatureSnapshotModel>> ListRecentAsync(string userId, Guid botId, string symbol, string timeframe, int take = 20, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyCollection<TradingFeatureSnapshotModel>>(Array.Empty<TradingFeatureSnapshotModel>());
        }
    }

    private sealed class FakeMarketDataService(TimeProvider timeProvider) : IMarketDataService
    {
        private readonly Dictionary<string, SymbolMetadataSnapshot> symbolMetadata = new(StringComparer.Ordinal)
        {
            ["BTCUSDT"] = CreateSymbolMetadata("BTCUSDT")
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
                    "IntegrationTest"));
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            symbolMetadata.TryGetValue(symbol.Trim().ToUpperInvariant(), out var snapshot);
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(snapshot);
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(IEnumerable<string> symbols, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
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
        public Task<IReadOnlyCollection<SymbolMetadataSnapshot>> GetSymbolMetadataAsync(IReadOnlyCollection<string> symbols, CancellationToken cancellationToken = default)
        {
            var snapshots = symbols.Select(symbol => symbolMetadata[symbol.Trim().ToUpperInvariant()]).ToArray();
            return Task.FromResult<IReadOnlyCollection<SymbolMetadataSnapshot>>(snapshots);
        }

        public Task<DateTime?> GetServerTimeUtcAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<DateTime?>(DateTime.UtcNow);
        }
    }

    private sealed class FakeHistoricalKlineClient(TimeProvider timeProvider) : IBinanceHistoricalKlineClient
    {
        public Task<IReadOnlyCollection<MarketCandleSnapshot>> GetClosedCandlesAsync(string symbol, string interval, DateTime startOpenTimeUtc, DateTime endOpenTimeUtc, int limit, CancellationToken cancellationToken = default)
        {
            var snapshots = Enumerable.Range(0, limit)
                .Select(index =>
                {
                    var openTimeUtc = startOpenTimeUtc.AddMinutes(index);
                    var closeTimeUtc = openTimeUtc.AddMinutes(1).AddMilliseconds(-1);

                    return new MarketCandleSnapshot(
                        symbol,
                        interval,
                        openTimeUtc,
                        closeTimeUtc,
                        65000m,
                        65010m,
                        64990m,
                        65000m,
                        10m,
                        IsClosed: true,
                        timeProvider.GetUtcNow().UtcDateTime,
                        "IntegrationTest.History");
                })
                .ToArray();

            return Task.FromResult<IReadOnlyCollection<MarketCandleSnapshot>>(snapshots);
        }
    }

    private sealed class FakeExchangeCredentialService : IExchangeCredentialService
    {
        public Task<ExchangeCredentialStateSnapshot> StoreAsync(StoreExchangeCredentialsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExchangeCredentialAccessResult> GetAsync(ExchangeCredentialAccessRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExchangeCredentialAccessResult("api-key", "api-secret", new ExchangeCredentialStateSnapshot(
                request.ExchangeAccountId,
                ExchangeCredentialStatus.Active,
                Fingerprint: "shadow-fingerprint",
                KeyVersion: "credential-v1",
                StoredAtUtc: DateTime.UtcNow,
                LastValidatedAtUtc: DateTime.UtcNow,
                LastAccessedAtUtc: DateTime.UtcNow,
                LastRotatedAtUtc: null,
                RevalidateAfterUtc: DateTime.UtcNow.AddDays(1),
                RotateAfterUtc: DateTime.UtcNow.AddDays(30))));
        public Task<ExchangeCredentialStateSnapshot> SetValidationStateAsync(SetExchangeCredentialValidationStateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExchangeCredentialStateSnapshot> GetStateAsync(Guid exchangeAccountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakePrivateRestClient(TimeProvider timeProvider) : IBinancePrivateRestClient
    {
        public int PlaceOrderCalls { get; private set; }

        public int GetOrderCalls { get; private set; }

        public BinanceOrderStatusSnapshot? PlacementSnapshot { get; set; }

        public BinanceOrderStatusSnapshot? QuerySnapshot { get; set; }

        public Task EnsureMarginTypeAsync(Guid exchangeAccountId, string symbol, string marginType, string apiKey, string apiSecret, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task EnsureLeverageAsync(Guid exchangeAccountId, string symbol, decimal leverage, string apiKey, string apiSecret, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<BinanceOrderPlacementResult> PlaceOrderAsync(BinanceOrderPlacementRequest request, CancellationToken cancellationToken = default)
        {
            PlaceOrderCalls++;
            var orderId = $"binance-order-{PlaceOrderCalls}";
            var snapshot = PlacementSnapshot ?? new BinanceOrderStatusSnapshot(
                request.Symbol,
                orderId,
                request.ClientOrderId,
                "NEW",
                request.Quantity,
                0m,
                0m,
                0m,
                0m,
                0m,
                timeProvider.GetUtcNow().UtcDateTime,
                "IntegrationTest.FuturesOrderPlacement");

            return Task.FromResult(new BinanceOrderPlacementResult(orderId, request.ClientOrderId, timeProvider.GetUtcNow().UtcDateTime, snapshot));
        }

        public Task<BinanceOrderStatusSnapshot> CancelOrderAsync(BinanceOrderCancelRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BinanceOrderStatusSnapshot> GetOrderAsync(BinanceOrderQueryRequest request, CancellationToken cancellationToken = default)
        {
            GetOrderCalls++;
            return Task.FromResult(QuerySnapshot ?? throw new NotSupportedException());
        }

        public Task<string> StartListenKeyAsync(string apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task KeepAliveListenKeyAsync(string apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CloseListenKeyAsync(string apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExchangeAccountSnapshot> GetAccountSnapshotAsync(Guid exchangeAccountId, string ownerUserId, string exchangeName, string apiKey, string apiSecret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeSpotPrivateRestClient(TimeProvider timeProvider) : IBinanceSpotPrivateRestClient
    {
        public int PlaceOrderCalls { get; private set; }

        public Task<BinanceOrderPlacementResult> PlaceOrderAsync(BinanceOrderPlacementRequest request, CancellationToken cancellationToken = default)
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
                "IntegrationTest.SpotOrderPlacement",
                Plane: ExchangeDataPlane.Spot);

            return Task.FromResult(new BinanceOrderPlacementResult("spot-order-1", request.ClientOrderId, timeProvider.GetUtcNow().UtcDateTime, snapshot));
        }

        public Task<ExchangeAccountSnapshot> GetAccountSnapshotAsync(Guid exchangeAccountId, string ownerUserId, string exchangeName, string apiKey, string apiSecret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BinanceOrderStatusSnapshot> GetOrderAsync(BinanceOrderQueryRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<BinanceSpotTradeFillSnapshot>> GetTradeFillsAsync(BinanceOrderQueryRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> StartListenKeyAsync(string apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task KeepAliveListenKeyAsync(string apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CloseListenKeyAsync(string apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "CoinBot.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset currentUtcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => currentUtcNow;

        public void Advance(TimeSpan timeSpan)
        {
            currentUtcNow = currentUtcNow.Add(timeSpan);
        }
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







