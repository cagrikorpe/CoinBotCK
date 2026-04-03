using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.DemoPortfolio;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Administration;

public sealed partial class CrisisEscalationServiceTests
{
    [Fact]
    public async Task SoftHalt_PreviewsImpact_AndDoesNotTouchOpenPositions()
    {
        await using var harness = CreateHarness();
        await SeedUserAsync(harness.DbContext, "user-soft");
        await SeedUserAsync(harness.DbContext, "user-live");
        await SeedExchangeAccountAsync(harness.DbContext, "user-live", harness.ExchangeAccountId);

        var demoPositionId = await SeedDemoPositionAsync(
            harness.DbContext,
            "user-soft",
            "BTCUSDT",
            quantity: 0.1m,
            averageEntryPrice: 60000m);
        var exchangePositionId = await SeedExchangePositionAsync(
            harness.DbContext,
            "user-live",
            harness.ExchangeAccountId,
            "ETHUSDT",
            quantity: 2m,
            entryPrice: 2000m);
        await SeedExecutionOrderAsync(
            harness.DbContext,
            "user-soft",
            "BTCUSDT",
            ExecutionOrderState.Submitted,
            ExecutionEnvironment.Demo,
            idempotencyKey: "soft-order-1");
        await SeedExecutionOrderAsync(
            harness.DbContext,
            "user-live",
            "ETHUSDT",
            ExecutionOrderState.Received,
            ExecutionEnvironment.Live,
            idempotencyKey: "soft-order-2",
            exchangeAccountId: harness.ExchangeAccountId);

        var preview = await harness.Service.PreviewAsync(
            new CrisisEscalationPreviewRequest(
                CrisisEscalationLevel.SoftHalt,
                "GLOBAL_SOFT_HALT"));

        Assert.Equal(2, preview.AffectedUserCount);
        Assert.Equal(2, preview.AffectedSymbolCount);
        Assert.Equal(2, preview.OpenPositionCount);
        Assert.Equal(2, preview.PendingOrderCount);
        Assert.Equal(10000m, preview.EstimatedExposure);
        Assert.False(preview.RequiresReauth);
        Assert.False(preview.RequiresSecondApproval);

        var result = await harness.Service.ExecuteAsync(
            new CrisisEscalationExecuteRequest(
                CrisisEscalationLevel.SoftHalt,
                "GLOBAL_SOFT_HALT",
                "cmd-soft-001",
                "super-admin",
                "admin:super-admin",
                "Stabilize execution entry",
                "CRISIS_SOFT_HALT",
                "Operator freeze",
                preview.PreviewStamp,
                "corr-soft-001",
                ReauthToken: null,
                SecondApprovalReference: null,
                RemoteIpAddress: "ip:masked"));

        var stateRequest = Assert.Single(harness.GlobalSystemStateService.SetRequests);
        var demoPosition = await harness.DbContext.DemoPositions.SingleAsync(entity => entity.Id == demoPositionId);
        var exchangePosition = await harness.DbContext.ExchangePositions.SingleAsync(entity => entity.Id == exchangePositionId);
        var orderStates = await harness.DbContext.ExecutionOrders
            .OrderBy(entity => entity.IdempotencyKey)
            .Select(entity => entity.State)
            .ToArrayAsync();

        Assert.Equal(GlobalSystemStateKind.SoftHalt, stateRequest.State);
        Assert.Equal(0.1m, demoPosition.Quantity);
        Assert.Equal(2m, exchangePosition.Quantity);
        Assert.Equal([ExecutionOrderState.Submitted, ExecutionOrderState.Received], orderStates);
        Assert.Equal("Level=SoftHalt | Scope=GLOBAL_SOFT_HALT | PurgedOrders=0 | FlattenDispatches=0 | FlattenReused=0 | Failures=0 | Reason=Stabilize execution entry", result.Summary);
        var recoveryRequest = Assert.Single(harness.IncidentHook.RecoveryRequests);
        Assert.Equal("cmd-soft-001", recoveryRequest.CommandId);
        Assert.Empty(harness.IncidentHook.IncidentRequests);
    }

    [Fact]
    public async Task OrderPurge_OnlyPurgesPendingOrders()
    {
        await using var harness = CreateHarness();
        await SeedUserAsync(harness.DbContext, "user-purge");

        var pendingOrderId = await SeedExecutionOrderAsync(
            harness.DbContext,
            "user-purge",
            "BTCUSDT",
            ExecutionOrderState.Submitted,
            ExecutionEnvironment.Demo,
            idempotencyKey: "purge-pending-1",
            quantity: 1m,
            price: 100m);
        var filledOrderId = await SeedExecutionOrderAsync(
            harness.DbContext,
            "user-purge",
            "BTCUSDT",
            ExecutionOrderState.Filled,
            ExecutionEnvironment.Demo,
            idempotencyKey: "purge-filled-1");
        var cancelledOrderId = await SeedExecutionOrderAsync(
            harness.DbContext,
            "user-purge",
            "BTCUSDT",
            ExecutionOrderState.Cancelled,
            ExecutionEnvironment.Demo,
            idempotencyKey: "purge-cancelled-1");

        var preview = await harness.Service.PreviewAsync(
            new CrisisEscalationPreviewRequest(
                CrisisEscalationLevel.OrderPurge,
                "PURGE:USER:user-purge"));

        Assert.Equal(1, preview.PendingOrderCount);

        var result = await harness.Service.ExecuteAsync(
            new CrisisEscalationExecuteRequest(
                CrisisEscalationLevel.OrderPurge,
                "PURGE:USER:user-purge",
                "cmd-purge-001",
                "super-admin",
                "admin:super-admin",
                "Remove pending risk",
                "CRISIS_ORDER_PURGE",
                "User-scoped purge",
                preview.PreviewStamp,
                "corr-purge-001",
                ReauthToken: null,
                SecondApprovalReference: null,
                RemoteIpAddress: "ip:masked"));

        var pendingOrder = await harness.DbContext.ExecutionOrders.SingleAsync(entity => entity.Id == pendingOrderId);
        var filledOrder = await harness.DbContext.ExecutionOrders.SingleAsync(entity => entity.Id == filledOrderId);
        var cancelledOrder = await harness.DbContext.ExecutionOrders.SingleAsync(entity => entity.Id == cancelledOrderId);

        Assert.Equal(ExecutionOrderState.Cancelled, pendingOrder.State);
        Assert.Equal(ExecutionOrderState.Filled, filledOrder.State);
        Assert.Equal(ExecutionOrderState.Cancelled, cancelledOrder.State);
        Assert.Single(harness.DemoPortfolioAccountingService.ReleaseRequests);
        Assert.Equal(1, result.PurgedOrderCount);
        Assert.Equal(0, result.FailedOperationCount);
        Assert.Empty(harness.ExecutionEngine.DispatchCalls);
        Assert.Equal("cmd-purge-001", Assert.Single(harness.IncidentHook.RecoveryRequests).CommandId);
    }

    [Fact]
    public async Task EmergencyFlatten_ReusesExistingCrisisExitOrder_AndRequiresApprovalHooks()
    {
        await using var harness = CreateHarness();
        await SeedUserAsync(harness.DbContext, "user-flatten");

        var lastValuationAtUtc = new DateTime(2026, 3, 24, 10, 0, 0, DateTimeKind.Utc);
        var position = new DemoPosition
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-flatten",
            PositionScopeKey = "position-user-flatten-ethusdt",
            Symbol = "ETHUSDT",
            BaseAsset = "ETH",
            QuoteAsset = "USDT",
            Quantity = 0.5m,
            CostBasis = 1000m,
            AverageEntryPrice = 2000m,
            RealizedPnl = 0m,
            UnrealizedPnl = 0m,
            TotalFeesInQuote = 0m,
            LastValuationAtUtc = lastValuationAtUtc
        };
        harness.DbContext.DemoPositions.Add(position);
        harness.DbContext.ExecutionOrders.Add(
            CreateExecutionOrder(
                ownerUserId: "user-flatten",
                symbol: "ETHUSDT",
                state: ExecutionOrderState.Submitted,
                executionEnvironment: ExecutionEnvironment.Demo,
                idempotencyKey: $"{BuildDemoPositionReusePrefix(position)}existing",
                strategyKey: "__crisis_flatten__"));
        await harness.DbContext.SaveChangesAsync();

        var preview = await harness.Service.PreviewAsync(
            new CrisisEscalationPreviewRequest(
                CrisisEscalationLevel.EmergencyFlatten,
                "FLATTEN:USER:user-flatten"));

        Assert.True(preview.RequiresReauth);
        Assert.True(preview.RequiresSecondApproval);

        var result = await harness.Service.ExecuteAsync(
            new CrisisEscalationExecuteRequest(
                CrisisEscalationLevel.EmergencyFlatten,
                "FLATTEN:USER:user-flatten",
                "cmd-flatten-001",
                "super-admin",
                "admin:super-admin",
                "Flatten user exposure",
                "CRISIS_EMERGENCY_FLATTEN",
                "Targeted flatten",
                preview.PreviewStamp,
                "corr-flatten-001",
                ReauthToken: "reauth-ok",
                SecondApprovalReference: "approval-ok",
                RemoteIpAddress: "ip:masked"));

        Assert.Equal(0, result.FlattenAttemptCount);
        Assert.Equal(1, result.FlattenReuseCount);
        Assert.Equal(0, result.FailedOperationCount);
        Assert.Empty(harness.ExecutionEngine.DispatchCalls);
        Assert.Single(harness.AuthorizationService.ReauthRequests);
        Assert.Single(harness.AuthorizationService.SecondApprovalRequests);
        Assert.Single(harness.IncidentHook.RecoveryRequests);
    }

    [Fact]
    public async Task EmergencyFlatten_RequiresReauth_AndSecondApproval()
    {
        await using var harness = CreateHarness();

        var preview = await harness.Service.PreviewAsync(
            new CrisisEscalationPreviewRequest(
                CrisisEscalationLevel.EmergencyFlatten,
                "GLOBAL_FLATTEN"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.ExecuteAsync(
                new CrisisEscalationExecuteRequest(
                    CrisisEscalationLevel.EmergencyFlatten,
                    "GLOBAL_FLATTEN",
                    "cmd-auth-001",
                    "super-admin",
                    "admin:super-admin",
                    "Flatten everything",
                    "CRISIS_EMERGENCY_FLATTEN",
                    "Global flatten",
                    preview.PreviewStamp,
                    "corr-auth-001",
                    ReauthToken: null,
                    SecondApprovalReference: "approval-ok",
                    RemoteIpAddress: "ip:masked")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.ExecuteAsync(
                new CrisisEscalationExecuteRequest(
                    CrisisEscalationLevel.EmergencyFlatten,
                    "GLOBAL_FLATTEN",
                    "cmd-auth-002",
                    "super-admin",
                    "admin:super-admin",
                    "Flatten everything",
                    "CRISIS_EMERGENCY_FLATTEN",
                    "Global flatten",
                    preview.PreviewStamp,
                    "corr-auth-002",
                    ReauthToken: "reauth-ok",
                    SecondApprovalReference: null,
                    RemoteIpAddress: "ip:masked")));

        Assert.Equal(2, harness.AuthorizationService.ReauthRequests.Count);
        Assert.Single(harness.AuthorizationService.SecondApprovalRequests);
    }

    [Fact]
    public async Task EmergencyFlatten_PartialFailure_WritesIncidentAndRecoveryHooks()
    {
        await using var harness = CreateHarness();
        await SeedUserAsync(harness.DbContext, "user-failure");
        await SeedDemoPositionAsync(
            harness.DbContext,
            "user-failure",
            "BTCUSDT",
            quantity: 0.25m,
            averageEntryPrice: 60000m);
        harness.ExecutionEngine.DispatchState = ExecutionOrderState.Rejected;
        harness.ExecutionEngine.FailureCode = "ExchangeRejected";
        harness.ExecutionEngine.FailureDetail = "Market exit rejected";

        var preview = await harness.Service.PreviewAsync(
            new CrisisEscalationPreviewRequest(
                CrisisEscalationLevel.EmergencyFlatten,
                "FLATTEN:USER:user-failure"));

        var result = await harness.Service.ExecuteAsync(
            new CrisisEscalationExecuteRequest(
                CrisisEscalationLevel.EmergencyFlatten,
                "FLATTEN:USER:user-failure",
                "cmd-failure-001",
                "super-admin",
                "admin:super-admin",
                "Flatten stuck position",
                "CRISIS_EMERGENCY_FLATTEN",
                "Targeted failure path",
                preview.PreviewStamp,
                "corr-failure-001",
                ReauthToken: "reauth-ok",
                SecondApprovalReference: "approval-ok",
                RemoteIpAddress: "ip:masked"));

        Assert.Equal(0, result.FlattenAttemptCount);
        Assert.Equal(1, result.FailedOperationCount);
        Assert.Single(harness.ExecutionEngine.DispatchCalls);
        Assert.Equal("cmd-failure-001", Assert.Single(harness.IncidentHook.IncidentRequests).CommandId);
        Assert.Equal("cmd-failure-001", Assert.Single(harness.IncidentHook.RecoveryRequests).CommandId);
        Assert.Contains("Failures=1", result.Summary, StringComparison.Ordinal);
    }

    private static TestHarness CreateHarness()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 24, 12, 0, 0, TimeSpan.Zero));
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var globalSystemStateService = new FakeGlobalSystemStateService();
        var executionEngine = new FakeExecutionEngine(timeProvider);
        var exchangeCredentialService = new FakeExchangeCredentialService();
        var privateRestClient = new FakePrivateRestClient(timeProvider);
        var demoPortfolioAccountingService = new FakeDemoPortfolioAccountingService(timeProvider);
        var marketDataService = new FakeMarketDataService();
        var authorizationService = new FakeCrisisEscalationAuthorizationService();
        var incidentHook = new FakeCrisisIncidentHook();
        var auditLogService = new FakeAuditLogService();
        var lifecycleService = new ExecutionOrderLifecycleService(
            dbContext,
            auditLogService,
            timeProvider,
            NullLogger<ExecutionOrderLifecycleService>.Instance);
        var service = new CrisisEscalationService(
            dbContext,
            globalSystemStateService,
            executionEngine,
            exchangeCredentialService,
            privateRestClient,
            lifecycleService,
            demoPortfolioAccountingService,
            marketDataService,
            authorizationService,
            incidentHook,
            Options.Create(new DemoFillSimulatorOptions()),
            timeProvider,
            NullLogger<CrisisEscalationService>.Instance);

        return new TestHarness(
            dbContext,
            service,
            globalSystemStateService,
            executionEngine,
            authorizationService,
            incidentHook,
            demoPortfolioAccountingService,
            timeProvider);
    }

    private static async Task SeedUserAsync(ApplicationDbContext dbContext, string userId)
    {
        dbContext.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = userId,
            NormalizedUserName = userId.ToUpperInvariant(),
            Email = $"{userId}@example.test",
            NormalizedEmail = $"{userId}@example.test".ToUpperInvariant(),
            FullName = userId
        });

        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedExchangeAccountAsync(
        ApplicationDbContext dbContext,
        string ownerUserId,
        Guid exchangeAccountId)
    {
        dbContext.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = ownerUserId,
            ExchangeName = "Binance",
            DisplayName = "Main Binance",
            IsReadOnly = false
        });

        await dbContext.SaveChangesAsync();
    }

    private static async Task<Guid> SeedDemoPositionAsync(
        ApplicationDbContext dbContext,
        string ownerUserId,
        string symbol,
        decimal quantity,
        decimal averageEntryPrice)
    {
        var position = new DemoPosition
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            PositionScopeKey = $"pos-{ownerUserId}-{symbol}".ToLowerInvariant(),
            Symbol = symbol,
            BaseAsset = symbol[..^4],
            QuoteAsset = symbol[^4..],
            Quantity = quantity,
            CostBasis = Math.Abs(quantity * averageEntryPrice),
            AverageEntryPrice = averageEntryPrice,
            RealizedPnl = 0m,
            UnrealizedPnl = 0m,
            TotalFeesInQuote = 0m
        };
        dbContext.DemoPositions.Add(position);
        await dbContext.SaveChangesAsync();
        return position.Id;
    }

    private static async Task<Guid> SeedExchangePositionAsync(
        ApplicationDbContext dbContext,
        string ownerUserId,
        Guid exchangeAccountId,
        string symbol,
        decimal quantity,
        decimal entryPrice)
    {
        var position = new ExchangePosition
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            ExchangeAccountId = exchangeAccountId,
            Symbol = symbol,
            PositionSide = quantity >= 0m ? "LONG" : "SHORT",
            Quantity = quantity,
            EntryPrice = entryPrice,
            BreakEvenPrice = entryPrice,
            UnrealizedProfit = 0m,
            MarginType = "cross",
            IsolatedWallet = 0m,
            ExchangeUpdatedAtUtc = new DateTime(2026, 3, 24, 10, 0, 0, DateTimeKind.Utc),
            SyncedAtUtc = new DateTime(2026, 3, 24, 10, 1, 0, DateTimeKind.Utc)
        };
        dbContext.ExchangePositions.Add(position);
        await dbContext.SaveChangesAsync();
        return position.Id;
    }

    private static async Task<Guid> SeedExecutionOrderAsync(
        ApplicationDbContext dbContext,
        string ownerUserId,
        string symbol,
        ExecutionOrderState state,
        ExecutionEnvironment executionEnvironment,
        string idempotencyKey,
        decimal quantity = 0.1m,
        decimal price = 60000m,
        Guid? exchangeAccountId = null)
    {
        var order = CreateExecutionOrder(
            ownerUserId,
            symbol,
            state,
            executionEnvironment,
            idempotencyKey,
            quantity,
            price,
            exchangeAccountId: exchangeAccountId);
        dbContext.ExecutionOrders.Add(order);
        await dbContext.SaveChangesAsync();
        return order.Id;
    }

    private static ExecutionOrder CreateExecutionOrder(
        string ownerUserId,
        string symbol,
        ExecutionOrderState state,
        ExecutionEnvironment executionEnvironment,
        string idempotencyKey,
        decimal quantity = 0.1m,
        decimal price = 60000m,
        Guid? exchangeAccountId = null,
        string strategyKey = "protect-core")
    {
        return new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Exit,
            StrategyKey = strategyKey,
            Symbol = symbol,
            Timeframe = "1m",
            BaseAsset = symbol[..^4],
            QuoteAsset = symbol[^4..],
            Side = ExecutionOrderSide.Sell,
            OrderType = ExecutionOrderType.Market,
            Quantity = quantity,
            Price = price,
            FilledQuantity = state == ExecutionOrderState.Filled ? quantity : 0m,
            ExecutionEnvironment = executionEnvironment,
            ExecutorKind = executionEnvironment == ExecutionEnvironment.Demo
                ? ExecutionOrderExecutorKind.Virtual
                : ExecutionOrderExecutorKind.Binance,
            State = state,
            IdempotencyKey = idempotencyKey,
            RootCorrelationId = $"corr-{idempotencyKey}",
            ExternalOrderId = executionEnvironment == ExecutionEnvironment.Live
                ? $"binance-{idempotencyKey}"
                : $"virtual-{idempotencyKey}",
            ExchangeAccountId = exchangeAccountId,
            SubmittedAtUtc = new DateTime(2026, 3, 24, 9, 0, 0, DateTimeKind.Utc),
            LastStateChangedAtUtc = new DateTime(2026, 3, 24, 9, 0, 0, DateTimeKind.Utc)
        };
    }

    private static string BuildDemoPositionReusePrefix(DemoPosition position)
    {
        var payload = string.Join(
            "|",
            "demo",
            position.OwnerUserId,
            position.Symbol,
            position.Quantity.ToString("0.##################", CultureInfo.InvariantCulture),
            position.AverageEntryPrice.ToString("0.##################", CultureInfo.InvariantCulture),
            position.LastFilledAtUtc?.ToString("O") ?? "none",
            position.LastValuationAtUtc?.ToString("O") ?? "none");

        return $"cfx_{ShortHash(payload, 24)}_";
    }

    private static string ShortHash(string value, int length)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash)[..length];
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeGlobalSystemStateService : IGlobalSystemStateService
    {
        public List<GlobalSystemStateSetRequest> SetRequests { get; } = [];

        public Task<GlobalSystemStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                new GlobalSystemStateSnapshot(
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
                    IsPersisted: true));
        }

        public Task<GlobalSystemStateSnapshot> SetStateAsync(
            GlobalSystemStateSetRequest request,
            CancellationToken cancellationToken = default)
        {
            SetRequests.Add(request);
            return Task.FromResult(
                new GlobalSystemStateSnapshot(
                    request.State,
                    request.ReasonCode,
                    request.Message,
                    request.Source,
                    request.CorrelationId,
                    request.IsManualOverride,
                    request.ExpiresAtUtc,
                    UpdatedAtUtc: request.ExpiresAtUtc,
                    request.UpdatedByUserId,
                    request.UpdatedFromIp,
                    Version: SetRequests.Count,
                    IsPersisted: true));
        }
    }

    private sealed class FakeExecutionEngine(TimeProvider timeProvider) : IExecutionEngine
    {
        public List<ExecutionCommand> DispatchCalls { get; } = [];

        public ExecutionOrderState DispatchState { get; set; } = ExecutionOrderState.Submitted;

        public string? FailureCode { get; set; }

        public string? FailureDetail { get; set; }

        public Task<ExecutionDispatchResult> DispatchAsync(
            ExecutionCommand command,
            CancellationToken cancellationToken = default)
        {
            DispatchCalls.Add(command);

            var snapshot = new ExecutionOrderSnapshot(
                ExecutionOrderId: Guid.NewGuid(),
                TradingStrategyId: command.TradingStrategyId,
                TradingStrategyVersionId: command.TradingStrategyVersionId,
                StrategySignalId: command.StrategySignalId,
                SignalType: command.SignalType,
                BotId: command.BotId,
                ExchangeAccountId: command.ExchangeAccountId,
                StrategyKey: command.StrategyKey,
                Symbol: command.Symbol,
                Timeframe: command.Timeframe,
                BaseAsset: command.BaseAsset,
                QuoteAsset: command.QuoteAsset,
                Side: command.Side,
                OrderType: command.OrderType,
                Quantity: command.Quantity,
                Price: command.Price,
                FilledQuantity: 0m,
                AverageFillPrice: null,
                LastFilledAtUtc: null,
                StopLossPrice: command.StopLossPrice,
                TakeProfitPrice: command.TakeProfitPrice,
                ReduceOnly: command.ReduceOnly,
                ReplacesExecutionOrderId: command.ReplacesExecutionOrderId,
                ExecutionEnvironment: command.IsDemo == true ? ExecutionEnvironment.Demo : ExecutionEnvironment.Live,
                ExecutorKind: command.IsDemo == true ? ExecutionOrderExecutorKind.Virtual : ExecutionOrderExecutorKind.Binance,
                State: DispatchState,
                IdempotencyKey: command.IdempotencyKey ?? Guid.NewGuid().ToString("N"),
                RootCorrelationId: command.CorrelationId ?? Guid.NewGuid().ToString("N"),
                ParentCorrelationId: command.ParentCorrelationId,
                ExternalOrderId: null,
                FailureCode: FailureCode,
                FailureDetail: FailureDetail,
                RejectionStage: ExecutionRejectionStage.None,
                SubmittedToBroker: DispatchState is ExecutionOrderState.Submitted or ExecutionOrderState.PartiallyFilled or ExecutionOrderState.CancelRequested or ExecutionOrderState.Filled or ExecutionOrderState.Cancelled,
                RetryEligible: false,
                CooldownApplied: false,
                DuplicateSuppressed: false,
                StopLossAttached: command.StopLossPrice.HasValue,
                TakeProfitAttached: command.TakeProfitPrice.HasValue,
                ClientOrderId: null,
                SubmittedAtUtc: timeProvider.GetUtcNow().UtcDateTime,
                LastReconciledAtUtc: null,
                ReconciliationStatus: ExchangeStateDriftStatus.Unknown,
                ReconciliationSummary: null,
                LastDriftDetectedAtUtc: null,
                LastStateChangedAtUtc: timeProvider.GetUtcNow().UtcDateTime,
                Transitions: Array.Empty<ExecutionOrderTransitionSnapshot>());

            return Task.FromResult(new ExecutionDispatchResult(snapshot, IsDuplicate: false));
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
                        StoredAtUtc: null,
                        LastValidatedAtUtc: null,
                        LastAccessedAtUtc: null,
                        LastRotatedAtUtc: null,
                        RevalidateAfterUtc: null,
                        RotateAfterUtc: null)));
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
        public Task<BinanceOrderPlacementResult> PlaceOrderAsync(
            BinanceOrderPlacementRequest request,
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

        public Task<BinanceOrderStatusSnapshot> CancelOrderAsync(
            BinanceOrderCancelRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                new BinanceOrderStatusSnapshot(
                    request.Symbol,
                    request.ExchangeOrderId ?? "cancelled-order",
                    request.ClientOrderId ?? request.ExecutionOrderId?.ToString("N") ?? "cancelled-client-order",
                    "CANCELED",
                    OriginalQuantity: 0m,
                    ExecutedQuantity: 0m,
                    CumulativeQuoteQuantity: 0m,
                    AveragePrice: 0m,
                    LastExecutedQuantity: 0m,
                    LastExecutedPrice: 0m,
                    EventTimeUtc: timeProvider.GetUtcNow().UtcDateTime,
                    Source: "unit-test"));
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

    private sealed class FakeDemoPortfolioAccountingService(TimeProvider timeProvider) : IDemoPortfolioAccountingService
    {
        public List<DemoFundsReleaseRequest> ReleaseRequests { get; } = [];

        public Task<DemoPortfolioAccountingResult> SeedWalletAsync(
            DemoWalletSeedRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<DemoPortfolioAccountingResult> ReserveFundsAsync(
            DemoFundsReservationRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<DemoPortfolioAccountingResult> ReleaseFundsAsync(
            DemoFundsReleaseRequest request,
            CancellationToken cancellationToken = default)
        {
            ReleaseRequests.Add(request);
            return Task.FromResult(
                new DemoPortfolioAccountingResult(
                    new DemoLedgerTransactionSnapshot(
                        Guid.NewGuid(),
                        "demo-release",
                        DemoLedgerTransactionType.FundsReleased,
                        BotId: null,
                        PositionScopeKey: "release",
                        OrderId: null,
                        FillId: null,
                        Symbol: null,
                        BaseAsset: null,
                        QuoteAsset: null,
                        Side: null,
                        Quantity: null,
                        Price: null,
                        FeeAsset: null,
                        FeeAmount: null,
                        FeeAmountInQuote: null,
                        RealizedPnlDelta: null,
                        PositionQuantityAfter: null,
                        PositionCostBasisAfter: null,
                        PositionAverageEntryPriceAfter: null,
                        CumulativeRealizedPnlAfter: null,
                        UnrealizedPnlAfter: null,
                        CumulativeFeesInQuoteAfter: null,
                        MarkPriceAfter: null,
                        OccurredAtUtc: timeProvider.GetUtcNow().UtcDateTime,
                        Entries: Array.Empty<DemoLedgerEntrySnapshot>()),
                    Position: null,
                    Wallets: Array.Empty<DemoWalletBalanceSnapshot>(),
                    IsReplay: false));
        }

        public Task<DemoPortfolioAccountingResult> ApplyFillAsync(
            DemoFillAccountingRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<DemoPortfolioAccountingResult> UpdateMarkPriceAsync(
            DemoMarkPriceUpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
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

    private sealed class FakeCrisisEscalationAuthorizationService : ICrisisEscalationAuthorizationService
    {
        public List<CrisisReauthValidationRequest> ReauthRequests { get; } = [];

        public List<CrisisSecondApprovalValidationRequest> SecondApprovalRequests { get; } = [];

        public Task ValidateReauthAsync(
            CrisisReauthValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            ReauthRequests.Add(request);

            if (string.IsNullOrWhiteSpace(request.Token))
            {
                throw new InvalidOperationException("Reauth token is required.");
            }

            return Task.CompletedTask;
        }

        public Task ValidateSecondApprovalAsync(
            CrisisSecondApprovalValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            SecondApprovalRequests.Add(request);

            if (string.IsNullOrWhiteSpace(request.ApprovalReference))
            {
                throw new InvalidOperationException("Second approval reference is required.");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeCrisisIncidentHook : ICrisisIncidentHook
    {
        public List<CrisisIncidentHookRequest> IncidentRequests { get; } = [];

        public List<CrisisRecoveryHookRequest> RecoveryRequests { get; } = [];

        public Task WriteIncidentAsync(
            CrisisIncidentHookRequest request,
            CancellationToken cancellationToken = default)
        {
            IncidentRequests.Add(request);
            return Task.CompletedTask;
        }

        public Task WriteRecoveryAsync(
            CrisisRecoveryHookRequest request,
            CancellationToken cancellationToken = default)
        {
            RecoveryRequests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAuditLogService : IAuditLogService
    {
        public Task WriteAsync(AuditLogWriteRequest request, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class TestHarness(
        ApplicationDbContext dbContext,
        CrisisEscalationService service,
        FakeGlobalSystemStateService globalSystemStateService,
        FakeExecutionEngine executionEngine,
        FakeCrisisEscalationAuthorizationService authorizationService,
        FakeCrisisIncidentHook incidentHook,
        FakeDemoPortfolioAccountingService demoPortfolioAccountingService,
        AdjustableTimeProvider timeProvider) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public CrisisEscalationService Service { get; } = service;

        public FakeGlobalSystemStateService GlobalSystemStateService { get; } = globalSystemStateService;

        public FakeExecutionEngine ExecutionEngine { get; } = executionEngine;

        public FakeCrisisEscalationAuthorizationService AuthorizationService { get; } = authorizationService;

        public FakeCrisisIncidentHook IncidentHook { get; } = incidentHook;

        public FakeDemoPortfolioAccountingService DemoPortfolioAccountingService { get; } = demoPortfolioAccountingService;

        public AdjustableTimeProvider TimeProvider { get; } = timeProvider;

        public Guid ExchangeAccountId { get; } = Guid.NewGuid();

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }
    }
}
