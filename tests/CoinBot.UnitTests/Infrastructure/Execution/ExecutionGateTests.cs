using CoinBot.Application.Abstractions.Administration;
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
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Execution;

public sealed class ExecutionGateTests
{
    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksWhenGlobalSwitchConfigurationIsMissing()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-100");

        var exception = await Assert.ThrowsAsync<ExecutionGateRejectedException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "worker-01",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-001",
                    Environment: ExecutionEnvironment.Demo,
                    Context: "Pre-dispatch gate",
                    CorrelationId: "corr-101")));

        var auditLog = await harness.DbContext.AuditLogs.SingleAsync();

        Assert.Equal(ExecutionGateBlockedReason.SwitchConfigurationMissing, exception.Reason);
        Assert.Equal("worker-01", auditLog.Actor);
        Assert.Equal("TradeExecution.Dispatch", auditLog.Action);
        Assert.Equal("Blocked:SwitchConfigurationMissing", auditLog.Outcome);
        Assert.Equal(nameof(ExecutionEnvironment.Demo), auditLog.Environment);
        Assert.Empty(await harness.DbContext.GlobalExecutionSwitches.ToListAsync());
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksWhenTradeMasterIsDisarmed()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-200");
        await harness.SwitchService.SetDemoModeAsync(
            isEnabled: false,
            actor: "admin-10",
            liveApproval: new TradingModeLiveApproval("chg-201"),
            context: "Prepare live mode",
            correlationId: "corr-201");

        var exception = await Assert.ThrowsAsync<ExecutionGateRejectedException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "worker-02",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-002",
                    Environment: ExecutionEnvironment.Live,
                    Context: "Live dispatch attempt",
                    CorrelationId: "corr-202")));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");

        Assert.Equal(ExecutionGateBlockedReason.TradeMasterDisarmed, exception.Reason);
        Assert.Equal("Blocked:TradeMasterDisarmed", auditLog.Outcome);
        Assert.Equal(nameof(ExecutionEnvironment.Live), auditLog.Environment);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksLiveExecutionWhenDemoModeIsEnabled()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-300");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-11",
            context: "Demo runtime armed",
            correlationId: "corr-301");

        var exception = await Assert.ThrowsAsync<ExecutionGateRejectedException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "worker-03",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-003",
                    Environment: ExecutionEnvironment.Live,
                    Context: "Live dispatch attempt",
                    CorrelationId: "corr-302")));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");

        Assert.Equal(ExecutionGateBlockedReason.LiveExecutionBlockedByDemoMode, exception.Reason);
        Assert.Equal("Blocked:LiveExecutionClosedByDemoMode", auditLog.Outcome);
        Assert.Equal(nameof(ExecutionEnvironment.Live), auditLog.Environment);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_AllowsDemoExecution_WhenTradeMasterIsArmed()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-400");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-12",
            context: "Demo execution window open",
            correlationId: "corr-401");

        var snapshot = await harness.ExecutionGate.EnsureExecutionAllowedAsync(
            new ExecutionGateRequest(
                Actor: "worker-04",
                Action: "TradeExecution.Dispatch",
                Target: "bot-004",
                Environment: ExecutionEnvironment.Demo,
                Context: "Demo dispatch attempt",
                CorrelationId: "corr-402"));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");

        Assert.True(snapshot.IsPersisted);
        Assert.True(snapshot.IsTradeMasterArmed);
        Assert.True(snapshot.DemoModeEnabled);
        Assert.Equal("Allowed", auditLog.Outcome);
        Assert.Equal(nameof(ExecutionEnvironment.Demo), auditLog.Environment);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksWhenDemoSessionDriftIsDetected()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-450");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-12",
            context: "Demo execution window open",
            correlationId: "corr-451");

        harness.DbContext.DemoSessions.Add(new DemoSession
        {
            OwnerUserId = "user-drift",
            SequenceNumber = 1,
            SeedAsset = "USDT",
            SeedAmount = 1000m,
            State = DemoSessionState.Active,
            ConsistencyStatus = DemoConsistencyStatus.Unknown,
            StartedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime.AddMinutes(-1)
        });
        harness.DbContext.DemoWallets.Add(new DemoWallet
        {
            OwnerUserId = "user-drift",
            Asset = "USDT",
            AvailableBalance = 1000m,
            ReservedBalance = 0m,
            LastActivityAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime
        });
        await harness.DbContext.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<ExecutionGateRejectedException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "worker-04b",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-004b",
                    Environment: ExecutionEnvironment.Demo,
                    Context: "Demo dispatch attempt",
                    CorrelationId: "corr-452",
                    UserId: "user-drift")));

        var session = await harness.DbContext.DemoSessions.SingleAsync(entity => entity.OwnerUserId == "user-drift");
        var auditLog = await harness.DbContext.AuditLogs.SingleAsync(entity => entity.Outcome == "Blocked:DemoSessionDriftDetected");

        Assert.Equal(ExecutionGateBlockedReason.DemoSessionDriftDetected, exception.Reason);
        Assert.Equal(DemoConsistencyStatus.DriftDetected, session.ConsistencyStatus);
        Assert.Equal(nameof(ExecutionEnvironment.Demo), auditLog.Environment);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksWhenRequestedEnvironmentDoesNotMatchResolvedScopedMode()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-500");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-13",
            context: "Execution open",
            correlationId: "corr-501");
        await harness.SwitchService.SetDemoModeAsync(
            isEnabled: false,
            actor: "admin-13",
            liveApproval: new TradingModeLiveApproval("chg-502"),
            context: "Global default moved to live",
            correlationId: "corr-502");

        var exception = await Assert.ThrowsAsync<ExecutionGateRejectedException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "worker-05",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-005",
                    Environment: ExecutionEnvironment.Demo,
                    Context: "Demo dispatch attempt against live scope",
                    CorrelationId: "corr-503")));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");

        Assert.Equal(ExecutionGateBlockedReason.RequestedEnvironmentDoesNotMatchResolvedMode, exception.Reason);
        Assert.Equal("Blocked:RequestedEnvironmentDoesNotMatchResolvedMode", auditLog.Outcome);
        Assert.Equal(nameof(ExecutionEnvironment.Demo), auditLog.Environment);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksLiveExecutionWhenDemoModeIsEnabled_EvenIfUserOverrideResolvesLive()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-550");
        await SeedUserAsync(
            harness.DbContext,
            "user-live-override",
            ExecutionEnvironment.Live,
            harness.TimeProvider.GetUtcNow().UtcDateTime.AddMinutes(-5));
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-override",
            context: "Execution open",
            correlationId: "corr-551");

        var exception = await Assert.ThrowsAsync<ExecutionGateRejectedException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "worker-override",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-override",
                    Environment: ExecutionEnvironment.Live,
                    Context: "Live dispatch attempt against user override",
                    CorrelationId: "corr-552",
                    UserId: "user-live-override")));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");

        Assert.Equal(ExecutionGateBlockedReason.LiveExecutionBlockedByDemoMode, exception.Reason);
        Assert.Equal("Blocked:LiveExecutionClosedByDemoMode", auditLog.Outcome);
        Assert.Contains("ResolvedMode=Live; Source=UserOverride", auditLog.Context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksWhenGlobalSystemStateIsMaintenance()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-gs-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-gs",
            context: "Execution open",
            correlationId: "corr-gs-2");
        await harness.GlobalSystemStateService.SetStateAsync(
            new GlobalSystemStateSetRequest(
                GlobalSystemStateKind.Maintenance,
                "PLANNED_MAINTENANCE",
                "Controlled window",
                "AdminPortal.Settings",
                "corr-gs-3",
                IsManualOverride: true,
                ExpiresAtUtc: null,
                UpdatedByUserId: "super-admin",
                UpdatedFromIp: "ip:masked"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "worker-gs",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-gs",
                    Environment: ExecutionEnvironment.Demo,
                    Context: "Dispatch during maintenance",
                    CorrelationId: "corr-gs-4")));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");

        Assert.Contains("Maintenance", exception.Message, StringComparison.Ordinal);
        Assert.Equal("Blocked:GlobalSystemMaintenance", auditLog.Outcome);
        Assert.Contains("GlobalSystemReason=PLANNED_MAINTENANCE", auditLog.Context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksWhenMarketDataIsStale()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-600");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-14",
            context: "Execution open",
            correlationId: "corr-601");
        harness.TimeProvider.Advance(TimeSpan.FromSeconds(3));

        var exception = await Assert.ThrowsAsync<ExecutionGateRejectedException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "worker-06",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-006",
                    Environment: ExecutionEnvironment.Demo,
                    Context: "Dispatch against stale market data",
                    CorrelationId: "corr-602")));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");

        Assert.Equal(ExecutionGateBlockedReason.StaleMarketData, exception.Reason);
        Assert.Equal("Blocked:StaleMarketData", auditLog.Outcome);
        Assert.Equal(nameof(ExecutionEnvironment.Demo), auditLog.Environment);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksWhenCandleGapDetected()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-700");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-15",
            context: "Execution open",
            correlationId: "corr-701");
        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "binance:kline",
                harness.TimeProvider.GetUtcNow().UtcDateTime,
                DegradedModeStateCode.Stopped,
                DegradedModeReasonCode.CandleDataGapDetected),
            correlationId: "corr-702");

        var exception = await Assert.ThrowsAsync<ExecutionGateRejectedException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "worker-07",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-007",
                    Environment: ExecutionEnvironment.Demo,
                    Context: "Dispatch against candle gap",
                    CorrelationId: "corr-703")));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");

        Assert.Equal(ExecutionGateBlockedReason.StaleMarketData, exception.Reason);
        Assert.Equal("Blocked:StaleMarketData", auditLog.Outcome);
        Assert.Contains("LatencyReason=CandleDataGapDetected", auditLog.Context, StringComparison.Ordinal);
    }

    private static TestHarness CreateHarness()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var auditLogService = new AuditLogService(dbContext, new CorrelationContextAccessor());
        var switchService = new GlobalExecutionSwitchService(dbContext, auditLogService);
        var globalSystemStateService = new GlobalSystemStateService(dbContext, auditLogService, timeProvider);
        var circuitBreaker = new DataLatencyCircuitBreaker(
            dbContext,
            new FakeAlertService(),
            Options.Create(new DataLatencyGuardOptions()),
            timeProvider,
            NullLogger<DataLatencyCircuitBreaker>.Instance);
        var tradingModeService = new TradingModeService(dbContext, auditLogService);
        var marketDataService = new FakeMarketDataService();
        var demoWalletValuationService = new DemoWalletValuationService(
            marketDataService,
            timeProvider,
            NullLogger<DemoWalletValuationService>.Instance);
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
        var executionGate = new ExecutionGate(
            demoSessionService,
            globalSystemStateService,
            switchService,
            circuitBreaker,
            tradingModeService,
            auditLogService,
            NullLogger<ExecutionGate>.Instance);

        return new TestHarness(dbContext, switchService, globalSystemStateService, circuitBreaker, executionGate, timeProvider);
    }

    private static async Task PrimeFreshMarketDataAsync(TestHarness harness, string correlationId)
    {
        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat("binance-btcusdt", harness.TimeProvider.GetUtcNow().UtcDateTime),
            correlationId);
    }

    private static async Task SeedUserAsync(
        ApplicationDbContext dbContext,
        string userId,
        ExecutionEnvironment? modeOverride,
        DateTime? approvedAtUtc = null)
    {
        dbContext.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = userId,
            NormalizedUserName = userId.ToUpperInvariant(),
            Email = $"{userId}@example.test",
            NormalizedEmail = $"{userId}@example.test".ToUpperInvariant(),
            FullName = userId,
            TradingModeOverride = modeOverride,
            TradingModeApprovedAtUtc = approvedAtUtc,
            TradingModeApprovalReference = approvedAtUtc.HasValue ? "approval-user-override" : null
        });

        await dbContext.SaveChangesAsync();
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeAlertService : IAlertService
    {
        public Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default)
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

    private sealed class TestHarness(
        ApplicationDbContext dbContext,
        IGlobalExecutionSwitchService switchService,
        IGlobalSystemStateService globalSystemStateService,
        IDataLatencyCircuitBreaker circuitBreaker,
        IExecutionGate executionGate,
        AdjustableTimeProvider timeProvider) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public IGlobalExecutionSwitchService SwitchService { get; } = switchService;

        public IGlobalSystemStateService GlobalSystemStateService { get; } = globalSystemStateService;

        public IDataLatencyCircuitBreaker CircuitBreaker { get; } = circuitBreaker;

        public IExecutionGate ExecutionGate { get; } = executionGate;

        public AdjustableTimeProvider TimeProvider { get; } = timeProvider;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }
    }
}
