using CoinBot.Application.Abstractions.Alerts;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.DemoPortfolio;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.DemoPortfolio;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.IntegrationTests.Execution;

public sealed class PilotSafetyPackIntegrationTests
{
    [Fact]
    public async Task EnsureExecutionAllowedAsync_AllowsHealthyPilotRequest_AndPersistsAuditTrace_OnSqlServer()
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString($"CoinBotPilotSafetyAllow_{Guid.NewGuid():N}");
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();

        await using var harness = CreateHarness(connectionString, useTestnetEndpoints: true);

        try
        {
            await harness.DbContext.Database.EnsureDeletedAsync();
            await harness.DbContext.Database.MigrateAsync();
            await SeedPilotUserAsync(harness.DbContext, "user-pilot-int");
            await PrimeFreshMarketDataAsync(harness, "corr-pilot-int-1");
            await SeedPilotSafetyAsync(harness, "user-pilot-int", exchangeAccountId);
            harness.PilotOptions.AllowedUserIds = ["user-pilot-int"];
            harness.PilotOptions.AllowedBotIds = [botId.ToString("N")];
            harness.PilotOptions.AllowedSymbols = ["BTCUSDT"];
            await harness.SwitchService.SetTradeMasterStateAsync(
                TradeMasterSwitchState.Armed,
                actor: "admin-pilot-int",
                context: "Execution open",
                correlationId: "corr-pilot-int-2");

            var snapshot = await harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "system:bot-worker",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-pilot-int",
                    Environment: ExecutionEnvironment.Live,
                    Context: "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                    CorrelationId: "corr-pilot-int-3",
                    UserId: "user-pilot-int",
                    BotId: botId,
                    Symbol: "BTCUSDT",
                    Timeframe: "1m",
                    ExchangeAccountId: exchangeAccountId,
                    Plane: ExchangeDataPlane.Futures),
                CancellationToken.None);

            var auditLog = await harness.DbContext.AuditLogs.SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");
            var decisionTrace = await harness.DbContext.DecisionTraces.SingleAsync();

            Assert.True(snapshot.IsPersisted);
            Assert.Equal("Allowed", auditLog.Outcome);
            Assert.Contains("PilotGuardSummary=PilotRequest=True", auditLog.Context, StringComparison.Ordinal);
            Assert.Contains("PilotBlockedReasons=none", auditLog.Context, StringComparison.Ordinal);
            Assert.Contains("PrivatePlaneFreshness=Fresh", auditLog.Context, StringComparison.Ordinal);
            Assert.Equal("Allow", decisionTrace.DecisionOutcome);
            Assert.Equal("Allowed", decisionTrace.DecisionReasonCode);
            Assert.Contains("\"pilotSafety\"", decisionTrace.SnapshotJson, StringComparison.Ordinal);
        }
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksPilotRequest_WhenPrivatePlaneIsStale_OnSqlServer()
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString($"CoinBotPilotSafetyPrivatePlane_{Guid.NewGuid():N}");
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();

        await using var harness = CreateHarness(connectionString, useTestnetEndpoints: true);

        try
        {
            await harness.DbContext.Database.EnsureDeletedAsync();
            await harness.DbContext.Database.MigrateAsync();
            await SeedPilotUserAsync(harness.DbContext, "user-pilot-stale-int");
            await PrimeFreshMarketDataAsync(harness, "corr-pilot-stale-int-1");
            await SeedPilotSafetyAsync(
                harness,
                "user-pilot-stale-int",
                exchangeAccountId,
                lastPrivateSyncAtUtc: harness.TimeProvider.GetUtcNow().UtcDateTime.AddMinutes(-10));
            harness.PilotOptions.AllowedUserIds = ["user-pilot-stale-int"];
            harness.PilotOptions.AllowedBotIds = [botId.ToString("N")];
            harness.PilotOptions.AllowedSymbols = ["BTCUSDT"];
            await harness.SwitchService.SetTradeMasterStateAsync(
                TradeMasterSwitchState.Armed,
                actor: "admin-pilot-stale-int",
                context: "Execution open",
                correlationId: "corr-pilot-stale-int-2");

            var exception = await Assert.ThrowsAsync<ExecutionGateRejectedException>(() =>
                harness.ExecutionGate.EnsureExecutionAllowedAsync(
                    new ExecutionGateRequest(
                        Actor: "system:bot-worker",
                        Action: "TradeExecution.Dispatch",
                        Target: "bot-pilot-stale-int",
                        Environment: ExecutionEnvironment.Live,
                        Context: "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                        CorrelationId: "corr-pilot-stale-int-3",
                        UserId: "user-pilot-stale-int",
                        BotId: botId,
                        Symbol: "BTCUSDT",
                        Timeframe: "1m",
                        ExchangeAccountId: exchangeAccountId,
                        Plane: ExchangeDataPlane.Futures),
                    CancellationToken.None));

            var auditLog = await harness.DbContext.AuditLogs.SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");
            var decisionTrace = await harness.DbContext.DecisionTraces.SingleAsync();

            Assert.Equal(ExecutionGateBlockedReason.PrivatePlaneStale, exception.Reason);
            Assert.Contains("PilotBlockedReasons=PrivatePlaneStale", auditLog.Context, StringComparison.Ordinal);
            Assert.Contains("PrivatePlaneFreshness=Stale", auditLog.Context, StringComparison.Ordinal);
            Assert.Equal("PrivatePlaneStale", decisionTrace.DecisionReasonCode);
        }
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksPilotRequest_WhenConfiguredEndpointsResolveLive_OnSqlServer()
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString($"CoinBotPilotSafetyEndpoint_{Guid.NewGuid():N}");
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();

        await using var harness = CreateHarness(connectionString, useTestnetEndpoints: false);

        try
        {
            await harness.DbContext.Database.EnsureDeletedAsync();
            await harness.DbContext.Database.MigrateAsync();
            await SeedPilotUserAsync(harness.DbContext, "user-pilot-endpoint-int");
            await PrimeFreshMarketDataAsync(harness, "corr-pilot-endpoint-int-1");
            await SeedPilotSafetyAsync(harness, "user-pilot-endpoint-int", exchangeAccountId);
            harness.PilotOptions.AllowedUserIds = ["user-pilot-endpoint-int"];
            harness.PilotOptions.AllowedBotIds = [botId.ToString("N")];
            harness.PilotOptions.AllowedSymbols = ["BTCUSDT"];
            await harness.SwitchService.SetTradeMasterStateAsync(
                TradeMasterSwitchState.Armed,
                actor: "admin-pilot-endpoint-int",
                context: "Execution open",
                correlationId: "corr-pilot-endpoint-int-2");

            var exception = await Assert.ThrowsAsync<ExecutionGateRejectedException>(() =>
                harness.ExecutionGate.EnsureExecutionAllowedAsync(
                    new ExecutionGateRequest(
                        Actor: "system:bot-worker",
                        Action: "TradeExecution.Dispatch",
                        Target: "bot-pilot-endpoint-int",
                        Environment: ExecutionEnvironment.Live,
                        Context: "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                        CorrelationId: "corr-pilot-endpoint-int-3",
                        UserId: "user-pilot-endpoint-int",
                        BotId: botId,
                        Symbol: "BTCUSDT",
                        Timeframe: "1m",
                        ExchangeAccountId: exchangeAccountId,
                        Plane: ExchangeDataPlane.Futures),
                    CancellationToken.None));

            var auditLog = await harness.DbContext.AuditLogs.SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");

            Assert.Equal(ExecutionGateBlockedReason.PilotTestnetEndpointMismatch, exception.Reason);
            Assert.Contains("PilotBlockedReasons=PilotTestnetEndpointMismatch", auditLog.Context, StringComparison.Ordinal);
            Assert.Contains("EndpointScopes=PrivateRest:Live/PrivateWs:Live/MarketRest:Live/MarketWs:Live", auditLog.Context, StringComparison.Ordinal);
        }
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }

    private static TestHarness CreateHarness(string connectionString, bool useTestnetEndpoints)
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
        var marketDataService = new FakeMarketDataService();
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
        var traceService = new TraceService(dbContext, correlationContextAccessor, timeProvider);
        var pilotOptions = new BotExecutionPilotOptions
        {
            Enabled = true,
            AllowedSymbols = ["BTCUSDT"],
            MaxOpenPositionsPerUser = 1,
            PerBotCooldownSeconds = 300,
            PerSymbolCooldownSeconds = 300,
            MaxOrderNotional = 250m,
            MaxDailyLossPercentage = 5m,
            PrivatePlaneFreshnessThresholdSeconds = 120
        };
        var privateDataOptions = Options.Create(new BinancePrivateDataOptions
        {
            RestBaseUrl = useTestnetEndpoints
                ? "https://testnet.binance.example/futures-rest"
                : "https://fapi.binance.com",
            WebSocketBaseUrl = useTestnetEndpoints
                ? "wss://testnet.binance.example/futures-private"
                : "wss://fstream.binance.com"
        });
        var marketDataOptions = Options.Create(new BinanceMarketDataOptions
        {
            RestBaseUrl = useTestnetEndpoints
                ? "https://testnet.binance.example/futures-market-rest"
                : "https://fapi.binance.com",
            WebSocketBaseUrl = useTestnetEndpoints
                ? "wss://testnet.binance.example/futures-market-stream"
                : "wss://fstream.binance.com",
            KlineInterval = "1m"
        });
        var executionGate = new ExecutionGate(
            demoSessionService,
            globalSystemStateService,
            switchService,
            circuitBreaker,
            tradingModeService,
            auditLogService,
            NullLogger<ExecutionGate>.Instance,
            new TestHostEnvironment(Environments.Development),
            traceService,
            timeProvider,
            latencyOptions,
            dbContext,
            privateDataOptions,
            marketDataOptions,
            Options.Create(pilotOptions));

        return new TestHarness(dbContext, switchService, circuitBreaker, executionGate, timeProvider, pilotOptions);
    }

    private static async Task SeedPilotUserAsync(ApplicationDbContext dbContext, string userId)
    {
        dbContext.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = userId,
            NormalizedUserName = userId.ToUpperInvariant(),
            Email = $"{userId}@coinbot.test",
            NormalizedEmail = $"{userId}@coinbot.test".ToUpperInvariant(),
            FullName = userId,
            EmailConfirmed = true
        });

        await dbContext.SaveChangesAsync();
    }

    private static async Task PrimeFreshMarketDataAsync(TestHarness harness, string correlationId)
    {
        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "binance:kline",
                harness.TimeProvider.GetUtcNow().UtcDateTime,
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                ExpectedOpenTimeUtc: harness.TimeProvider.GetUtcNow().UtcDateTime.AddMinutes(1),
                ContinuityGapCount: 0),
            correlationId);
    }

    private static async Task SeedPilotSafetyAsync(
        TestHarness harness,
        string ownerUserId,
        Guid exchangeAccountId,
        DateTime? lastPrivateSyncAtUtc = null)
    {
        var observedAtUtc = lastPrivateSyncAtUtc ?? harness.TimeProvider.GetUtcNow().UtcDateTime;
        var apiCredentialId = Guid.NewGuid();

        harness.DbContext.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = ownerUserId,
            ExchangeName = "Binance",
            DisplayName = "Pilot Futures",
            IsReadOnly = false,
            CredentialStatus = ExchangeCredentialStatus.Active,
            ApiKeyCiphertext = "cipher-api-key",
            ApiSecretCiphertext = "cipher-api-secret"
        });
        harness.DbContext.ApiCredentials.Add(new ApiCredential
        {
            Id = apiCredentialId,
            ExchangeAccountId = exchangeAccountId,
            OwnerUserId = ownerUserId,
            ApiKeyCiphertext = "cipher-api-key",
            ApiSecretCiphertext = "cipher-api-secret",
            CredentialFingerprint = "pilot-fingerprint",
            KeyVersion = "credential-v1",
            EncryptedBlobVersion = 1,
            ValidationStatus = "Valid",
            PermissionSummary = "Trade=Y; Futures=Y; Testnet=Y",
            StoredAtUtc = observedAtUtc,
            LastValidatedAtUtc = observedAtUtc
        });
        harness.DbContext.ApiCredentialValidations.Add(new ApiCredentialValidation
        {
            Id = Guid.NewGuid(),
            ApiCredentialId = apiCredentialId,
            ExchangeAccountId = exchangeAccountId,
            OwnerUserId = ownerUserId,
            IsKeyValid = true,
            CanTrade = true,
            SupportsSpot = false,
            SupportsFutures = true,
            EnvironmentScope = "Testnet",
            IsEnvironmentMatch = true,
            ValidationStatus = "Valid",
            PermissionSummary = "Trade=Y; Futures=Y; Testnet=Y",
            ValidatedAtUtc = observedAtUtc
        });
        harness.DbContext.ExchangeAccountSyncStates.Add(new ExchangeAccountSyncState
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

        await harness.DbContext.SaveChangesAsync();
    }

    private sealed class FakeAlertService : IAlertService
    {
        public Task SendAsync(CoinBot.Application.Abstractions.Alerts.AlertNotification notification, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMarketDataService : IMarketDataService
    {
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
            return ValueTask.FromResult<MarketPriceSnapshot?>(null);
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(null);
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
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
        IGlobalExecutionSwitchService switchService,
        IDataLatencyCircuitBreaker circuitBreaker,
        IExecutionGate executionGate,
        AdjustableTimeProvider timeProvider,
        BotExecutionPilotOptions pilotOptions) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public IGlobalExecutionSwitchService SwitchService { get; } = switchService;

        public IDataLatencyCircuitBreaker CircuitBreaker { get; } = circuitBreaker;

        public IExecutionGate ExecutionGate { get; } = executionGate;

        public AdjustableTimeProvider TimeProvider { get; } = timeProvider;

        public BotExecutionPilotOptions PilotOptions { get; } = pilotOptions;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }
    }
}


