using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Autonomy;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Autonomy;
using CoinBot.Infrastructure.Monitoring;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Autonomy;

public sealed class AutonomyFoundationTests
{
    [Fact]
    public async Task ReviewQueue_ReusesPendingEntry_AndExpiresPendingEntries()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 24, 12, 0, 0, TimeSpan.Zero));
        await using var dbContext = CreateDbContext();
        var auditLogService = new FakeAdminAuditLogService();
        var service = new AutonomyReviewQueueService(dbContext, auditLogService, timeProvider);

        var first = await service.EnqueueAsync(
            new AutonomyReviewQueueEnqueueRequest(
                ApprovalId: "approval-001",
                ScopeKey: "BREAKER:WEBSOCKET",
                SuggestedAction: AutonomySuggestedActions.WebSocketReconnect,
                ConfidenceScore: 0.92m,
                AffectedUsers: ["user-1"],
                AffectedSymbols: ["BTCUSDT"],
                ExpiresAtUtc: timeProvider.GetUtcNow().UtcDateTime.AddMinutes(5),
                Reason: "Dependency breaker WebSocket requires review."));
        var duplicate = await service.EnqueueAsync(
            new AutonomyReviewQueueEnqueueRequest(
                ApprovalId: "approval-002",
                ScopeKey: "BREAKER:WEBSOCKET",
                SuggestedAction: AutonomySuggestedActions.WebSocketReconnect,
                ConfidenceScore: 0.93m,
                AffectedUsers: ["user-2"],
                AffectedSymbols: ["ETHUSDT"],
                ExpiresAtUtc: timeProvider.GetUtcNow().UtcDateTime.AddMinutes(5),
                Reason: "Dependency breaker WebSocket requires review."));

        timeProvider.Advance(TimeSpan.FromMinutes(6));
        var expiredCount = await service.ExpirePendingAsync();
        var entries = await service.ListAsync();
        var expiredEntry = Assert.Single(entries);

        Assert.Equal(first.ApprovalId, duplicate.ApprovalId);
        Assert.Equal(1, expiredCount);
        Assert.Equal(AutonomyReviewStatus.Expired, expiredEntry.Status);
        Assert.Single(auditLogService.Requests);
    }

    [Fact]
    public async Task PreFlightSimulation_ReportsExposureRateLimitRiskAndRestrictionImpact()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 24, 12, 0, 0, TimeSpan.Zero));
        SeedScopeState(dbContext);

        var policyEngine = new FakeGlobalPolicyEngine
        {
            EvaluationResult = new GlobalPolicyEvaluationResult(
                IsBlocked: true,
                BlockCode: "RiskLimitBlocked",
                Message: "Risk limit would be exceeded.",
                PolicyVersion: 4,
                MatchedRestrictionState: SymbolRestrictionState.Blocked,
                EffectiveAutonomyMode: AutonomyPolicyMode.LowRiskAutoAct)
        };
        var telemetryCollector = new MonitoringTelemetryCollector(timeProvider, NullLogger<MonitoringTelemetryCollector>.Instance);
        telemetryCollector.RecordBinancePing(TimeSpan.FromMilliseconds(125), rateLimitUsage: 1005, observedAtUtc: timeProvider.GetUtcNow().UtcDateTime);
        var service = new AutonomyService(
            dbContext,
            policyEngine,
            new FakeMarketDataService(),
            telemetryCollector,
            new FakeReviewQueueService(),
            new FakeSelfHealingExecutor(),
            new FakeAutonomyIncidentHook(),
            Options.Create(new AutonomyOptions()),
            timeProvider,
            NullLogger<AutonomyService>.Instance);

        var result = await service.SimulateAsync(
            new PreFlightSimulationRequest(
                SuggestedAction: AutonomySuggestedActions.CacheRebuild,
                ConfidenceScore: 0.80m,
                Symbol: "BTCUSDT",
                Environment: ExecutionEnvironment.Live,
                Side: ExecutionOrderSide.Buy,
                Quantity: 2m,
                Price: 25_000m));

        Assert.Equal(2, result.AffectedOpenPositionCount);
        Assert.Equal(45_500m, result.EstimatedOpenPositionExposure);
        Assert.Equal("RiskLimitBlocked", result.RiskLimitImpact);
        Assert.Equal("Low", result.LiquidityImpact);
        Assert.Equal("High", result.RateLimitImpact);
        Assert.Equal(0.20m, result.FalsePositiveProbability);
        Assert.False(result.IsGlobalPolicyCompliant);
        Assert.True(result.HasRestrictionConflict);
        Assert.Equal(["user-1", "user-2", "user-3"], result.AffectedUsers);
        Assert.Equal(["BTCUSDT"], result.AffectedSymbols);
    }

    [Fact]
    public async Task DependencyBreaker_EntersCooldownAfterThirdFailure_QueuesReviewAndIncident()
    {
        var harness = CreateBreakerHarness();

        await harness.Manager.RecordFailureAsync(
            new DependencyCircuitBreakerFailureRequest(
                DependencyCircuitBreakerKind.WebSocket,
                "system:test",
                "SocketClosed",
                "Connection lost"));
        await harness.Manager.RecordFailureAsync(
            new DependencyCircuitBreakerFailureRequest(
                DependencyCircuitBreakerKind.WebSocket,
                "system:test",
                "SocketClosed",
                "Connection lost"));
        var snapshot = await harness.Manager.RecordFailureAsync(
            new DependencyCircuitBreakerFailureRequest(
                DependencyCircuitBreakerKind.WebSocket,
                "system:test",
                "SocketClosed",
                "Connection lost"));

        var reviewItems = await harness.ReviewQueueService.ListAsync(AutonomyReviewStatus.Pending);

        Assert.Equal(CircuitBreakerStateCode.Cooldown, snapshot.StateCode);
        Assert.Equal(3, snapshot.ConsecutiveFailureCount);
        Assert.Single(reviewItems);
        Assert.Single(harness.IncidentHook.IncidentRequests);
        Assert.Equal(GlobalSystemStateKind.Degraded, Assert.Single(harness.GlobalStateService.SetRequests).State);
        Assert.NotEmpty(harness.AdminAuditLogService.Requests);
    }

    [Fact]
    public async Task DependencyBreaker_HalfOpenThenSuccess_ClosesBreaker_AndRecoversGlobalState()
    {
        var harness = CreateBreakerHarness();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await harness.Manager.RecordFailureAsync(
                new DependencyCircuitBreakerFailureRequest(
                    DependencyCircuitBreakerKind.RestMarketData,
                    "system:test",
                    "Http503",
                    "Metadata unavailable"));
        }

        harness.TimeProvider.Advance(TimeSpan.FromSeconds(61));

        var halfOpen = await harness.Manager.TryBeginHalfOpenAsync(
            new DependencyCircuitBreakerHalfOpenRequest(
                DependencyCircuitBreakerKind.RestMarketData,
                "system:test",
                "corr-half-open"));
        var recovered = await harness.Manager.RecordSuccessAsync(
            new DependencyCircuitBreakerSuccessRequest(
                DependencyCircuitBreakerKind.RestMarketData,
                "system:test",
                "corr-recovered"));

        Assert.NotNull(halfOpen);
        Assert.Equal(CircuitBreakerStateCode.HalfOpen, halfOpen!.StateCode);
        Assert.Equal(CircuitBreakerStateCode.Closed, recovered.StateCode);
        Assert.Equal(GlobalSystemStateKind.Active, harness.GlobalStateService.SetRequests.Last().State);
        Assert.Single(harness.IncidentHook.RecoveryRequests);
    }

    [Fact]
    public async Task AutonomyService_QueuesReview_WhenActionIsDisallowed()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 24, 12, 0, 0, TimeSpan.Zero));
        var reviewQueue = new AutonomyReviewQueueService(dbContext, new FakeAdminAuditLogService(), timeProvider);
        var incidentHook = new FakeAutonomyIncidentHook();
        var healingExecutor = new FakeSelfHealingExecutor();
        var policyEngine = new FakeGlobalPolicyEngine();
        var service = new AutonomyService(
            dbContext,
            policyEngine,
            new FakeMarketDataService(),
            new MonitoringTelemetryCollector(timeProvider, NullLogger<MonitoringTelemetryCollector>.Instance),
            reviewQueue,
            healingExecutor,
            incidentHook,
            Options.Create(new AutonomyOptions()),
            timeProvider,
            NullLogger<AutonomyService>.Instance);

        var result = await service.EvaluateAsync(
            new AutonomyDecisionRequest(
                ActorUserId: "system:test",
                SuggestedAction: AutonomySuggestedActions.EmergencyFlatten,
                Reason: "Unsafe action should require review.",
                ConfidenceScore: 0.95m));

        var queuedItems = await reviewQueue.ListAsync(AutonomyReviewStatus.Pending);

        Assert.False(result.AutoExecuted);
        Assert.True(result.ReviewQueued);
        Assert.Single(queuedItems);
        Assert.Empty(healingExecutor.ExecuteRequests);
        Assert.Single(incidentHook.IncidentRequests);
    }

    [Fact]
    public async Task AutonomyService_ExecutesAllowedSelfHealing_WhenLowRiskPolicyAllows()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 24, 12, 0, 0, TimeSpan.Zero));
        var reviewQueue = new AutonomyReviewQueueService(dbContext, new FakeAdminAuditLogService(), timeProvider);
        var incidentHook = new FakeAutonomyIncidentHook();
        var healingExecutor = new FakeSelfHealingExecutor();
        var policyEngine = new FakeGlobalPolicyEngine();
        var service = new AutonomyService(
            dbContext,
            policyEngine,
            new FakeMarketDataService(),
            new MonitoringTelemetryCollector(timeProvider, NullLogger<MonitoringTelemetryCollector>.Instance),
            reviewQueue,
            healingExecutor,
            incidentHook,
            Options.Create(new AutonomyOptions()),
            timeProvider,
            NullLogger<AutonomyService>.Instance);

        var result = await service.EvaluateAsync(
            new AutonomyDecisionRequest(
                ActorUserId: "system:test",
                SuggestedAction: AutonomySuggestedActions.WebSocketReconnect,
                Reason: "Recover public market websocket.",
                ConfidenceScore: 0.92m,
                BreakerKind: DependencyCircuitBreakerKind.WebSocket));

        Assert.True(result.AutoExecuted);
        Assert.False(result.ReviewQueued);
        Assert.Single(healingExecutor.ExecuteRequests);
        Assert.Empty(incidentHook.IncidentRequests);
        Assert.Single(incidentHook.RecoveryRequests);
    }

    [Fact]
    public async Task AutonomyService_QueuesReviewAndIncident_WhenProbeFailsAfterExecution()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 24, 12, 0, 0, TimeSpan.Zero));
        var reviewQueue = new AutonomyReviewQueueService(dbContext, new FakeAdminAuditLogService(), timeProvider);
        var incidentHook = new FakeAutonomyIncidentHook();
        var healingExecutor = new FakeSelfHealingExecutor
        {
            ProbeResult = new SelfHealingExecutionResult(false, "ProbeFailed", "post-execution probe failed")
        };
        var policyEngine = new FakeGlobalPolicyEngine();
        var service = new AutonomyService(
            dbContext,
            policyEngine,
            new FakeMarketDataService(),
            new MonitoringTelemetryCollector(timeProvider, NullLogger<MonitoringTelemetryCollector>.Instance),
            reviewQueue,
            healingExecutor,
            incidentHook,
            Options.Create(new AutonomyOptions()),
            timeProvider,
            NullLogger<AutonomyService>.Instance);

        var result = await service.EvaluateAsync(
            new AutonomyDecisionRequest(
                ActorUserId: "system:test",
                SuggestedAction: AutonomySuggestedActions.CacheRebuild,
                Reason: "Recover market data cache.",
                ConfidenceScore: 0.88m,
                BreakerKind: DependencyCircuitBreakerKind.RestMarketData));

        var queuedItems = await reviewQueue.ListAsync(AutonomyReviewStatus.Pending);

        Assert.True(result.AutoExecuted);
        Assert.True(result.ReviewQueued);
        Assert.Equal("ProbeFailed", result.Outcome);
        Assert.Single(healingExecutor.ExecuteRequests);
        Assert.Single(healingExecutor.ProbeRequests);
        Assert.Single(queuedItems);
        Assert.Single(incidentHook.IncidentRequests);
        Assert.Empty(incidentHook.RecoveryRequests);
    }

    [Fact]
    public async Task SelfHealingWorker_RunOnce_RequestsAutonomyEvaluation_ForDueHalfOpenBreaker()
    {
        var fakeReviewQueue = new FakeReviewQueueService();
        var fakeAutonomyService = new FakeAutonomyService();
        var fakeBreakerManager = new FakeBreakerManager(DependencyCircuitBreakerKind.WebSocket);
        var services = new ServiceCollection();
        services.AddSingleton<IAutonomyReviewQueueService>(fakeReviewQueue);
        services.AddSingleton<IAutonomyService>(fakeAutonomyService);
        services.AddSingleton<IDependencyCircuitBreakerStateManager>(fakeBreakerManager);
        services.AddLogging();

        using var provider = services.BuildServiceProvider();
        var worker = new AutonomySelfHealingWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AutonomyOptions()),
            NullLogger<AutonomySelfHealingWorker>.Instance);

        await worker.RunOnceAsync();

        Assert.Equal(1, fakeReviewQueue.ExpireCalls);
        var evaluation = Assert.Single(fakeAutonomyService.Requests);
        Assert.Equal(AutonomySuggestedActions.WebSocketReconnect, evaluation.SuggestedAction);
        Assert.Equal(DependencyCircuitBreakerKind.WebSocket, evaluation.BreakerKind);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private static BreakerHarness CreateBreakerHarness()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 24, 12, 0, 0, TimeSpan.Zero));
        var dbContext = CreateDbContext();
        var adminAuditLogService = new FakeAdminAuditLogService();
        var reviewQueueService = new AutonomyReviewQueueService(dbContext, adminAuditLogService, timeProvider);
        var incidentHook = new FakeAutonomyIncidentHook();
        var globalStateService = new FakeGlobalSystemStateService();
        var manager = new DependencyCircuitBreakerStateManager(
            dbContext,
            globalStateService,
            reviewQueueService,
            incidentHook,
            adminAuditLogService,
            Options.Create(new DependencyCircuitBreakerOptions()),
            timeProvider,
            NullLogger<DependencyCircuitBreakerStateManager>.Instance);

        return new BreakerHarness(
            dbContext,
            manager,
            reviewQueueService,
            incidentHook,
            globalStateService,
            adminAuditLogService,
            timeProvider);
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeAdminAuditLogService : IAdminAuditLogService
    {
        public List<AdminAuditLogWriteRequest> Requests { get; } = [];

        public Task WriteAsync(AdminAuditLogWriteRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAutonomyIncidentHook : IAutonomyIncidentHook
    {
        public List<AutonomyIncidentHookRequest> IncidentRequests { get; } = [];

        public List<AutonomyRecoveryHookRequest> RecoveryRequests { get; } = [];

        public Task WriteIncidentAsync(AutonomyIncidentHookRequest request, CancellationToken cancellationToken = default)
        {
            IncidentRequests.Add(request);
            return Task.CompletedTask;
        }

        public Task WriteRecoveryAsync(AutonomyRecoveryHookRequest request, CancellationToken cancellationToken = default)
        {
            RecoveryRequests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGlobalSystemStateService : IGlobalSystemStateService
    {
        public List<GlobalSystemStateSetRequest> SetRequests { get; } = [];

        private GlobalSystemStateSnapshot current = new(
            GlobalSystemStateKind.Active,
            "SYSTEM_ACTIVE",
            Message: null,
            "SystemDefault",
            CorrelationId: null,
            IsManualOverride: false,
            ExpiresAtUtc: null,
            UpdatedAtUtc: null,
            UpdatedByUserId: null,
            UpdatedFromIp: null,
            Version: 0,
            IsPersisted: true);

        public Task<GlobalSystemStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(current);
        }

        public Task<GlobalSystemStateSnapshot> SetStateAsync(GlobalSystemStateSetRequest request, CancellationToken cancellationToken = default)
        {
            SetRequests.Add(request);
            current = new GlobalSystemStateSnapshot(
                request.State,
                request.ReasonCode,
                request.Message,
                request.Source,
                request.CorrelationId,
                request.IsManualOverride,
                request.ExpiresAtUtc,
                UpdatedAtUtc: DateTime.UtcNow,
                request.UpdatedByUserId,
                request.UpdatedFromIp,
                Version: SetRequests.Count,
                IsPersisted: true);
            return Task.FromResult(current);
        }
    }

    private sealed class FakeGlobalPolicyEngine : IGlobalPolicyEngine
    {
        public GlobalPolicySnapshot Snapshot { get; set; } = GlobalPolicySnapshot.CreateDefault(new DateTime(2026, 3, 24, 12, 0, 0, DateTimeKind.Utc));

        public GlobalPolicyEvaluationResult EvaluationResult { get; set; } = new(
            IsBlocked: false,
            BlockCode: null,
            Message: null,
            PolicyVersion: 1,
            MatchedRestrictionState: null,
            EffectiveAutonomyMode: AutonomyPolicyMode.LowRiskAutoAct);

        public Task<GlobalPolicySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Snapshot);
        }

        public Task<GlobalPolicyEvaluationResult> EvaluateAsync(GlobalPolicyEvaluationRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(EvaluationResult);
        }

        public Task<GlobalPolicySnapshot> UpdateAsync(GlobalPolicyUpdateRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GlobalPolicySnapshot> RollbackAsync(GlobalPolicyRollbackRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeSelfHealingExecutor : ISelfHealingExecutor
    {
        public List<SelfHealingActionRequest> ExecuteRequests { get; } = [];

        public List<DependencyCircuitBreakerKind> ProbeRequests { get; } = [];

        public SelfHealingExecutionResult ExecuteResult { get; set; } = new(true, "Executed", "ok");

        public SelfHealingExecutionResult ProbeResult { get; set; } = new(true, "ProbeSucceeded", "ok");

        public Task<SelfHealingExecutionResult> ExecuteAsync(SelfHealingActionRequest request, CancellationToken cancellationToken = default)
        {
            ExecuteRequests.Add(request);
            return Task.FromResult(ExecuteResult);
        }

        public Task<SelfHealingExecutionResult> ProbeAsync(
            DependencyCircuitBreakerKind breakerKind,
            string actorUserId,
            string? correlationId = null,
            string? jobKey = null,
            string? symbol = null,
            CancellationToken cancellationToken = default)
        {
            ProbeRequests.Add(breakerKind);
            return Task.FromResult(ProbeResult);
        }
    }

    private sealed class FakeMarketDataService : IMarketDataService
    {
        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

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

    private sealed class FakeReviewQueueService : IAutonomyReviewQueueService
    {
        public int ExpireCalls { get; private set; }

        public Task<AutonomyReviewQueueItem> EnqueueAsync(AutonomyReviewQueueEnqueueRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyCollection<AutonomyReviewQueueItem>> ListAsync(AutonomyReviewStatus? status = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyCollection<AutonomyReviewQueueItem>>(Array.Empty<AutonomyReviewQueueItem>());
        }

        public Task<int> ExpirePendingAsync(CancellationToken cancellationToken = default)
        {
            ExpireCalls++;
            return Task.FromResult(0);
        }
    }

    private sealed class FakeAutonomyService : IAutonomyService
    {
        public List<AutonomyDecisionRequest> Requests { get; } = [];

        public Task<PreFlightSimulationResult> SimulateAsync(PreFlightSimulationRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<AutonomyDecisionResult> EvaluateAsync(AutonomyDecisionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(
                new AutonomyDecisionResult(
                    new PreFlightSimulationResult(0, 0m, "None", "None", "None", 0.1m, true, false, Array.Empty<string>(), Array.Empty<string>()),
                    AutoExecuted: true,
                    ReviewQueued: false,
                    ApprovalId: null,
                    Outcome: "Executed",
                    Detail: null));
        }
    }

    private sealed class FakeBreakerManager(DependencyCircuitBreakerKind dueBreakerKind) : IDependencyCircuitBreakerStateManager
    {
        public List<DependencyCircuitBreakerKind> HalfOpenRequests { get; } = [];

        public Task<DependencyCircuitBreakerSnapshot> GetSnapshotAsync(DependencyCircuitBreakerKind breakerKind, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CreateSnapshot(breakerKind, CircuitBreakerStateCode.Closed));
        }

        public Task<IReadOnlyCollection<DependencyCircuitBreakerSnapshot>> ListSnapshotsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyCollection<DependencyCircuitBreakerSnapshot>>(Array.Empty<DependencyCircuitBreakerSnapshot>());
        }

        public Task<DependencyCircuitBreakerSnapshot> RecordFailureAsync(DependencyCircuitBreakerFailureRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<DependencyCircuitBreakerSnapshot> RecordSuccessAsync(DependencyCircuitBreakerSuccessRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<DependencyCircuitBreakerSnapshot?> TryBeginHalfOpenAsync(DependencyCircuitBreakerHalfOpenRequest request, CancellationToken cancellationToken = default)
        {
            HalfOpenRequests.Add(request.BreakerKind);

            return Task.FromResult<DependencyCircuitBreakerSnapshot?>(
                request.BreakerKind == dueBreakerKind
                    ? CreateSnapshot(request.BreakerKind, CircuitBreakerStateCode.HalfOpen)
                    : null);
        }

        private static DependencyCircuitBreakerSnapshot CreateSnapshot(DependencyCircuitBreakerKind breakerKind, CircuitBreakerStateCode stateCode)
        {
            return new DependencyCircuitBreakerSnapshot(
                breakerKind,
                stateCode,
                ConsecutiveFailureCount: stateCode == CircuitBreakerStateCode.HalfOpen ? 3 : 0,
                LastFailureAtUtc: DateTime.UtcNow,
                LastSuccessAtUtc: null,
                CooldownUntilUtc: DateTime.UtcNow,
                HalfOpenStartedAtUtc: stateCode == CircuitBreakerStateCode.HalfOpen ? DateTime.UtcNow : null,
                LastProbeAtUtc: stateCode == CircuitBreakerStateCode.HalfOpen ? DateTime.UtcNow : null,
                LastErrorCode: null,
                LastErrorMessage: null,
                CorrelationId: "corr-worker",
                IsPersisted: true);
        }
    }

    private static void SeedScopeState(ApplicationDbContext dbContext)
    {
        dbContext.DemoPositions.Add(
            new DemoPosition
            {
                Id = Guid.NewGuid(),
                OwnerUserId = "user-1",
                PositionScopeKey = "demo:user-1:BTCUSDT",
                Symbol = "BTCUSDT",
                BaseAsset = "BTC",
                QuoteAsset = "USDT",
                Quantity = 0.5m,
                AverageEntryPrice = 40_000m,
                LastMarkPrice = 41_000m,
                CreatedDate = DateTime.UtcNow,
                UpdatedDate = DateTime.UtcNow
            });
        dbContext.ExchangePositions.Add(
            new ExchangePosition
            {
                Id = Guid.NewGuid(),
                OwnerUserId = "user-2",
                ExchangeAccountId = Guid.NewGuid(),
                Symbol = "BTCUSDT",
                PositionSide = "LONG",
                Quantity = 1m,
                EntryPrice = 25_000m,
                BreakEvenPrice = 25_000m,
                MarginType = "cross",
                ExchangeUpdatedAtUtc = DateTime.UtcNow,
                SyncedAtUtc = DateTime.UtcNow,
                CreatedDate = DateTime.UtcNow,
                UpdatedDate = DateTime.UtcNow
            });
        dbContext.ExecutionOrders.Add(
            new ExecutionOrder
            {
                Id = Guid.NewGuid(),
                OwnerUserId = "user-3",
                TradingStrategyId = Guid.NewGuid(),
                TradingStrategyVersionId = Guid.NewGuid(),
                StrategySignalId = Guid.NewGuid(),
                SignalType = StrategySignalType.Entry,
                StrategyKey = "autonomy-test",
                Symbol = "BTCUSDT",
                Timeframe = "1m",
                BaseAsset = "BTC",
                QuoteAsset = "USDT",
                Side = ExecutionOrderSide.Buy,
                OrderType = ExecutionOrderType.Market,
                Quantity = 0.25m,
                Price = 25_000m,
                ExecutionEnvironment = ExecutionEnvironment.Live,
                State = ExecutionOrderState.Submitted,
                IdempotencyKey = "autonomy-test-order",
                RootCorrelationId = "corr-autonomy-test",
                LastStateChangedAtUtc = DateTime.UtcNow,
                CreatedDate = DateTime.UtcNow,
                UpdatedDate = DateTime.UtcNow
            });

        dbContext.SaveChanges();
    }

    private sealed class BreakerHarness(
        ApplicationDbContext dbContext,
        DependencyCircuitBreakerStateManager manager,
        AutonomyReviewQueueService reviewQueueService,
        FakeAutonomyIncidentHook incidentHook,
        FakeGlobalSystemStateService globalStateService,
        FakeAdminAuditLogService adminAuditLogService,
        AdjustableTimeProvider timeProvider)
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public DependencyCircuitBreakerStateManager Manager { get; } = manager;

        public AutonomyReviewQueueService ReviewQueueService { get; } = reviewQueueService;

        public FakeAutonomyIncidentHook IncidentHook { get; } = incidentHook;

        public FakeGlobalSystemStateService GlobalStateService { get; } = globalStateService;

        public FakeAdminAuditLogService AdminAuditLogService { get; } = adminAuditLogService;

        public AdjustableTimeProvider TimeProvider { get; } = timeProvider;
    }
}
