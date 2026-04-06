using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.DemoPortfolio;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Risk;
using CoinBot.Infrastructure.Strategies;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Jobs;

public sealed class BotWorkerJobProcessorTests
{
    [Fact]
    public async Task ProcessAsync_GeneratesEntrySignal_AndSubmitsDevelopmentFuturesPilotOrder()
    {
        await using var harness = CreateHarness();
        var bot = await SeedBotGraphAsync(harness.DbContext);
        ConfigurePilotScope(harness, bot);
        await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-bot-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: "corr-bot-2");

        var result = await harness.Processor.ProcessAsync(
            bot,
            "job-bot-1",
            CancellationToken.None);

        var persistedSignal = await harness.DbContext.TradingStrategySignals.SingleAsync();
        var persistedOrder = await harness.DbContext.ExecutionOrders.SingleAsync();

        Assert.True(result.IsSuccessful);
        Assert.Equal(StrategySignalType.Entry, persistedSignal.SignalType);
        Assert.Equal(ExecutionOrderState.Submitted, persistedOrder.State);
        Assert.Equal(ExecutionEnvironment.Live, persistedOrder.ExecutionEnvironment);
        Assert.Equal(ExecutionOrderExecutorKind.Binance, persistedOrder.ExecutorKind);
        Assert.Equal(0.002m, persistedOrder.Quantity);
        Assert.Equal(1, harness.PrivateRestClient.EnsureMarginTypeCalls);
        Assert.Equal(1, harness.PrivateRestClient.EnsureLeverageCalls);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.StartsWith("cbp0_", harness.PrivateRestClient.LastPlacedClientOrderId, StringComparison.Ordinal);
        Assert.Equal("BTCUSDT", persistedOrder.Symbol);
    }

    [Fact]
    public async Task ProcessAsync_RefreshesSymbolScopedLatencyFromHistoricalBackfill_WhenHeartbeatWasNotPrimed()
    {
        await using var harness = CreateHarness();
        var bot = await SeedBotGraphAsync(harness.DbContext);
        ConfigurePilotScope(harness, bot);
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: "corr-bot-rest-2");

        var result = await harness.Processor.ProcessAsync(
            bot,
            "job-bot-rest-1",
            CancellationToken.None);

        var stateId = DegradedModeDefaults.ResolveStateId("BTCUSDT", "1m");
        var scopedState = await harness.DbContext.DegradedModeStates.SingleAsync(entity => entity.Id == stateId);
        var persistedOrder = await harness.DbContext.ExecutionOrders.SingleAsync();

        Assert.True(result.IsSuccessful);
        Assert.Equal(ExecutionOrderState.Submitted, persistedOrder.State);
        Assert.Equal(DegradedModeStateCode.Normal, scopedState.StateCode);
        Assert.Equal(DegradedModeReasonCode.None, scopedState.ReasonCode);
        Assert.Equal("BTCUSDT", scopedState.LatestSymbol);
        Assert.Equal("1m", scopedState.LatestTimeframe);
        Assert.Equal("binance:rest-backfill", scopedState.LatestHeartbeatSource);
        Assert.Equal(0, scopedState.LatestContinuityGapCount);
        Assert.NotNull(scopedState.LatestDataTimestampAtUtc);
    }

    [Fact]
    public async Task ProcessAsync_FailsClosed_WhenPilotSymbolIsNotAllowed()
    {
        await using var harness = CreateHarness();
        var bot = await SeedBotGraphAsync(harness.DbContext, symbol: "XRPUSDT");
        ConfigurePilotScope(harness, bot, "BTCUSDT");
        await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-bot-eth-1", symbol: "ETHUSDT");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: "corr-bot-eth-2");

        var result = await harness.Processor.ProcessAsync(
            bot,
            "job-bot-eth-1",
            CancellationToken.None);

        Assert.False(result.IsSuccessful);
        Assert.False(result.IsRetryableFailure);
        Assert.Equal("PilotSymbolNotAllowed", result.ErrorCode);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Empty(harness.DbContext.ExecutionOrders);
        Assert.Empty(harness.DbContext.TradingStrategySignals);
    }

    [Fact]
    public async Task ProcessAsync_FailsClosed_WhenTradeMasterIsDisarmed()
    {
        await using var harness = CreateHarness();
        var bot = await SeedBotGraphAsync(harness.DbContext);
        ConfigurePilotScope(harness, bot);
        await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-bot-disarmed-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: "corr-bot-disarmed-2");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Disarmed,
            actor: "admin-bot",
            context: "Execution frozen",
            correlationId: "corr-bot-disarmed-3");

        var result = await harness.Processor.ProcessAsync(
            bot,
            "job-bot-disarmed-1",
            CancellationToken.None);

        var persistedOrder = await harness.DbContext.ExecutionOrders.SingleAsync();

        Assert.False(result.IsSuccessful);
        Assert.False(result.IsRetryableFailure);
        Assert.Equal("TradeMasterDisarmed", result.ErrorCode);
        Assert.Equal(ExecutionOrderState.Rejected, persistedOrder.State);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task ProcessAsync_FailsClosed_WhenSameOwnerHasMultipleEnabledBotsOnSameSymbol()
    {
        await using var harness = CreateHarness();
        var firstBot = await SeedBotGraphAsync(harness.DbContext);
        ConfigurePilotScope(harness, firstBot);
        _ = await SeedBotGraphAsync(harness.DbContext, ownerUserId: firstBot.OwnerUserId);
        await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-bot-multi-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: "corr-bot-multi-2");

        var result = await harness.Processor.ProcessAsync(
            firstBot,
            "job-bot-multi-1",
            CancellationToken.None);

        Assert.False(result.IsSuccessful);
        Assert.False(result.IsRetryableFailure);
        Assert.Equal("PilotSymbolConflictMultipleEnabledBots", result.ErrorCode);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Empty(harness.DbContext.ExecutionOrders);
    }

    [Fact]
    public async Task ProcessAsync_AllowsMultipleEnabledBotsAcrossDifferentSymbols()
    {
        await using var harness = CreateHarness();
        var firstBot = await SeedBotGraphAsync(harness.DbContext, symbol: "BTCUSDT");
        ConfigurePilotScope(harness, firstBot);
        _ = await SeedBotGraphAsync(harness.DbContext, ownerUserId: firstBot.OwnerUserId, symbol: "ETHUSDT");
        await PrimeFreshMarketDataAsync(harness.CircuitBreaker, harness.TimeProvider, "corr-bot-eth-ok-1", symbol: "BTCUSDT");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-bot",
            context: "Execution open",
            correlationId: "corr-bot-eth-ok-2");

        var result = await harness.Processor.ProcessAsync(
            firstBot,
            "job-bot-eth-ok-1",
            CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
    }

    private static TestHarness CreateHarness()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero));
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var correlationContextAccessor = new CorrelationContextAccessor();
        var auditLogService = new AuditLogService(dbContext, correlationContextAccessor);
        var switchService = new GlobalExecutionSwitchService(dbContext, auditLogService);
        var globalSystemStateService = new GlobalSystemStateService(dbContext, auditLogService, timeProvider);
        var marketDataService = new FakeMarketDataService(timeProvider);
        var demoWalletValuationService = new DemoWalletValuationService(
            marketDataService,
            timeProvider,
            NullLogger<DemoWalletValuationService>.Instance);
        var latencyOptions = Options.Create(new DataLatencyGuardOptions());
        var circuitBreaker = new DataLatencyCircuitBreaker(
            dbContext,
            new FakeAlertService(),
            latencyOptions,
            timeProvider,
            NullLogger<DataLatencyCircuitBreaker>.Instance);
        var tradingModeService = new TradingModeService(dbContext, auditLogService);
        var demoSessionService = new DemoSessionService(
            dbContext,
            new DemoConsistencyWatchdogService(
                dbContext,
                Options.Create(new DemoSessionOptions()),
                timeProvider,
                NullLogger<DemoConsistencyWatchdogService>.Instance),
            demoWalletValuationService,
            auditLogService,
            Options.Create(new DemoSessionOptions()),
            timeProvider,
            NullLogger<DemoSessionService>.Instance);
        var hostEnvironment = new TestHostEnvironment(Environments.Development);
        var traceService = new TraceService(
            dbContext,
            correlationContextAccessor,
            timeProvider);
        var pilotOptions = new BotExecutionPilotOptions
        {
            Enabled = true,
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
        var riskPolicyEvaluator = new RiskPolicyEvaluator(
            dbContext,
            timeProvider,
            NullLogger<RiskPolicyEvaluator>.Instance);
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
        var lifecycleService = new ExecutionOrderLifecycleService(
            dbContext,
            auditLogService,
            timeProvider,
            NullLogger<ExecutionOrderLifecycleService>.Instance);
        var demoPortfolioAccountingService = new DemoPortfolioAccountingService(
            dbContext,
            demoSessionService,
            demoWalletValuationService,
            timeProvider,
            NullLogger<DemoPortfolioAccountingService>.Instance);
        var demoFillSimulator = new DemoFillSimulator(
            marketDataService,
            Options.Create(new DemoFillSimulatorOptions()),
            timeProvider,
            NullLogger<DemoFillSimulator>.Instance);
        var credentialService = new FakeExchangeCredentialService();
        var privateRestClient = new FakePrivateRestClient(timeProvider);
        var spotPrivateRestClient = new FakeSpotPrivateRestClient(timeProvider);
        var strategySignalService = new StrategySignalService(
            dbContext,
            new StrategyEvaluatorService(new StrategyRuleParser()),
            riskPolicyEvaluator,
            traceService,
            correlationContextAccessor,
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
            new BinanceExecutor(
                dbContext,
                credentialService,
                privateRestClient,
                NullLogger<BinanceExecutor>.Instance,
                marketDataService: marketDataService),
            new BinanceSpotExecutor(
                dbContext,
                credentialService,
                spotPrivateRestClient,
                NullLogger<BinanceSpotExecutor>.Instance,
                marketDataService: marketDataService),
            lifecycleService,
            timeProvider,
            NullLogger<ExecutionEngine>.Instance);
        var processor = new BotWorkerJobProcessor(
            dbContext,
            new IndicatorDataService(
                marketDataService,
                new IndicatorStreamHub(),
                Options.Create(new IndicatorEngineOptions()),
                NullLogger<IndicatorDataService>.Instance),
            marketDataService,
            new FakeExchangeInfoClient(marketDataService.SymbolMetadata),
            new FakeHistoricalKlineClient(timeProvider),
            strategySignalService,
            executionEngine,
            circuitBreaker,
            correlationContextAccessor,
            Options.Create(pilotOptions),
            hostEnvironment,
            timeProvider,
            NullLogger<BotWorkerJobProcessor>.Instance);

        return new TestHarness(
            dbContext,
            processor,
            switchService,
            circuitBreaker,
            timeProvider,
            privateRestClient,
            pilotOptions);
    }

    private static async Task<TradingBot> SeedBotGraphAsync(
        ApplicationDbContext dbContext,
        string symbol = "BTCUSDT",
        string ownerUserId = "user-bot-pilot")
    {
        var observedAtUtc = new DateTime(2026, 4, 1, 11, 59, 0, DateTimeKind.Utc);
        var strategy = new TradingStrategy
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            StrategyKey = "pilot-core",
            DisplayName = "Pilot Core"
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
            PublishedAtUtc = new DateTime(2026, 4, 1, 11, 50, 0, DateTimeKind.Utc)
        };
        var exchangeAccountId = Guid.NewGuid();
        var bot = new TradingBot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = "Pilot Bot",
            StrategyKey = strategy.StrategyKey,
            Symbol = symbol,
            ExchangeAccountId = exchangeAccountId,
            IsEnabled = true
        };

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
            DisplayName = "Pilot Futures",
            IsReadOnly = false,
            CredentialStatus = ExchangeCredentialStatus.Active
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
            ApiCredentialId = Guid.NewGuid(),
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

    private static void ConfigurePilotScope(TestHarness harness, TradingBot bot, string? allowedSymbol = null)
    {
        harness.PilotOptions.AllowedUserIds = [bot.OwnerUserId];
        harness.PilotOptions.AllowedBotIds = [bot.Id.ToString("N")];
        harness.PilotOptions.AllowedSymbols =
        [
            MarketDataSymbolNormalizer.Normalize(
                string.IsNullOrWhiteSpace(allowedSymbol)
                    ? bot.Symbol
                    : allowedSymbol)
        ];
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

    private sealed class FakeMarketDataService(TimeProvider timeProvider) : IMarketDataService
    {
        private readonly Dictionary<string, SymbolMetadataSnapshot> symbolMetadata = new(StringComparer.Ordinal)
        {
            ["BTCUSDT"] = CreateSymbolMetadata("BTCUSDT"),
            ["ETHUSDT"] = CreateSymbolMetadata("ETHUSDT"),
            ["SOLUSDT"] = CreateSymbolMetadata("SOLUSDT")
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
                    "UnitTest"));
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            symbolMetadata.TryGetValue(symbol.Trim().ToUpperInvariant(), out var snapshot);
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(snapshot);
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
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
        public Task<IReadOnlyCollection<SymbolMetadataSnapshot>> GetSymbolMetadataAsync(
            IReadOnlyCollection<string> symbols,
            CancellationToken cancellationToken = default)
        {
            var snapshots = symbols
                .Select(symbol => symbolMetadata[symbol.Trim().ToUpperInvariant()])
                .ToArray();
            return Task.FromResult<IReadOnlyCollection<SymbolMetadataSnapshot>>(snapshots);
        }

        public Task<DateTime?> GetServerTimeUtcAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<DateTime?>(DateTime.UtcNow);
        }
    }

    private sealed class FakeHistoricalKlineClient(TimeProvider timeProvider) : IBinanceHistoricalKlineClient
    {
        public Task<IReadOnlyCollection<MarketCandleSnapshot>> GetClosedCandlesAsync(
            string symbol,
            string interval,
            DateTime startOpenTimeUtc,
            DateTime endOpenTimeUtc,
            int limit,
            CancellationToken cancellationToken = default)
        {
            var snapshots = Enumerable.Range(0, limit)
                .Select(index =>
                {
                    var openTimeUtc = startOpenTimeUtc.AddMinutes(index);
                    var closeTimeUtc = openTimeUtc.AddMinutes(1).AddMilliseconds(-1);

                    return new MarketCandleSnapshot(
                        "BTCUSDT",
                        "1m",
                        openTimeUtc,
                        closeTimeUtc,
                        65000m,
                        65010m,
                        64990m,
                        65000m,
                        10m,
                        IsClosed: true,
                        timeProvider.GetUtcNow().UtcDateTime,
                        "UnitTest.History");
                })
                .ToArray();

            return Task.FromResult<IReadOnlyCollection<MarketCandleSnapshot>>(snapshots);
        }
    }

    private sealed class FakeExchangeCredentialService : IExchangeCredentialService
    {
        public Task<ExchangeCredentialStateSnapshot> StoreAsync(
            StoreExchangeCredentialsRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExchangeCredentialAccessResult> GetAsync(
            ExchangeCredentialAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                new ExchangeCredentialAccessResult(
                    "api-key",
                    "api-secret",
                    new ExchangeCredentialStateSnapshot(
                        request.ExchangeAccountId,
                        ExchangeCredentialStatus.Active,
                        "fingerprint",
                        "v1",
                        StoredAtUtc: new DateTime(2026, 4, 1, 11, 0, 0, DateTimeKind.Utc),
                        LastValidatedAtUtc: new DateTime(2026, 4, 1, 11, 5, 0, DateTimeKind.Utc),
                        LastAccessedAtUtc: new DateTime(2026, 4, 1, 11, 5, 0, DateTimeKind.Utc),
                        LastRotatedAtUtc: new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc),
                        RevalidateAfterUtc: new DateTime(2026, 5, 1, 11, 5, 0, DateTimeKind.Utc),
                        RotateAfterUtc: new DateTime(2026, 7, 1, 11, 5, 0, DateTimeKind.Utc))));
        }

        public Task<ExchangeCredentialStateSnapshot> SetValidationStateAsync(
            SetExchangeCredentialValidationStateRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExchangeCredentialStateSnapshot> GetStateAsync(
            Guid exchangeAccountId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakePrivateRestClient(TimeProvider timeProvider) : IBinancePrivateRestClient
    {
        public int EnsureMarginTypeCalls { get; private set; }

        public int EnsureLeverageCalls { get; private set; }

        public int PlaceOrderCalls { get; private set; }

        public string? LastPlacedClientOrderId { get; private set; }

        public Task EnsureMarginTypeAsync(
            Guid exchangeAccountId,
            string symbol,
            string marginType,
            string apiKey,
            string apiSecret,
            CancellationToken cancellationToken = default)
        {
            EnsureMarginTypeCalls++;
            return Task.CompletedTask;
        }

        public Task EnsureLeverageAsync(
            Guid exchangeAccountId,
            string symbol,
            decimal leverage,
            string apiKey,
            string apiSecret,
            CancellationToken cancellationToken = default)
        {
            EnsureLeverageCalls++;
            return Task.CompletedTask;
        }

        public Task<BinanceOrderPlacementResult> PlaceOrderAsync(
            BinanceOrderPlacementRequest request,
            CancellationToken cancellationToken = default)
        {
            PlaceOrderCalls++;
            LastPlacedClientOrderId = request.ClientOrderId;

            return Task.FromResult(
                new BinanceOrderPlacementResult(
                    $"binance-order-{PlaceOrderCalls}",
                    request.ClientOrderId,
                    timeProvider.GetUtcNow().UtcDateTime));
        }

        public Task<BinanceOrderStatusSnapshot> CancelOrderAsync(
            BinanceOrderCancelRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<BinanceOrderStatusSnapshot> GetOrderAsync(
            BinanceOrderQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string> StartListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task KeepAliveListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task CloseListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExchangeAccountSnapshot> GetAccountSnapshotAsync(
            Guid exchangeAccountId,
            string ownerUserId,
            string exchangeName,
            string apiKey,
            string apiSecret,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeSpotPrivateRestClient(TimeProvider timeProvider) : IBinanceSpotPrivateRestClient
    {
        public Task<BinanceOrderPlacementResult> PlaceOrderAsync(
            BinanceOrderPlacementRequest request,
            CancellationToken cancellationToken = default)
        {
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
                "Binance.SpotPrivateRest.OrderPlacement",
                Plane: ExchangeDataPlane.Spot);

            return Task.FromResult(new BinanceOrderPlacementResult("spot-order-1", request.ClientOrderId, timeProvider.GetUtcNow().UtcDateTime, snapshot));
        }

        public Task<ExchangeAccountSnapshot> GetAccountSnapshotAsync(
            Guid exchangeAccountId,
            string ownerUserId,
            string exchangeName,
            string apiKey,
            string apiSecret,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<BinanceOrderStatusSnapshot> GetOrderAsync(
            BinanceOrderQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyCollection<BinanceSpotTradeFillSnapshot>> GetTradeFillsAsync(
            BinanceOrderQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string> StartListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task KeepAliveListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task CloseListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeAlertService : CoinBot.Application.Abstractions.Alerts.IAlertService
    {
        public Task SendAsync(CoinBot.Application.Abstractions.Alerts.AlertNotification notification, CancellationToken cancellationToken = default)
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

        public string ApplicationName { get; set; } = "CoinBot.UnitTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestHarness(
        ApplicationDbContext dbContext,
        BotWorkerJobProcessor processor,
        IGlobalExecutionSwitchService switchService,
        IDataLatencyCircuitBreaker circuitBreaker,
        AdjustableTimeProvider timeProvider,
        FakePrivateRestClient privateRestClient,
        BotExecutionPilotOptions pilotOptions) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public BotWorkerJobProcessor Processor { get; } = processor;

        public IGlobalExecutionSwitchService SwitchService { get; } = switchService;

        public IDataLatencyCircuitBreaker CircuitBreaker { get; } = circuitBreaker;

        public AdjustableTimeProvider TimeProvider { get; } = timeProvider;

        public FakePrivateRestClient PrivateRestClient { get; } = privateRestClient;

        public BotExecutionPilotOptions PilotOptions { get; } = pilotOptions;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }
    }
}






