using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Exchange;

public sealed class BinanceSpotPrivateStreamManagerTests
{
    [Fact]
    public async Task RunSessionCycleAsync_RefreshesSpotAccountState_AndReconnectsOnListenKeyExpiry()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        var databaseName = Guid.NewGuid().ToString("N");
        var exchangeAccountId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(now);
        var seedSnapshot = CreateSnapshot(exchangeAccountId, walletBalance: 100m, freeBalance: 90m, lockedBalance: 10m, observedAtUtc: now.UtcDateTime);
        var refreshedSnapshot = CreateSnapshot(exchangeAccountId, walletBalance: 150m, freeBalance: 130m, lockedBalance: 20m, observedAtUtc: now.UtcDateTime.AddSeconds(10));
        var updatedEventTimeUtc = now.UtcDateTime.AddSeconds(5);
        var restClient = new FakeSpotPrivateRestClient([seedSnapshot, refreshedSnapshot]);
        var streamClient = new FakeSpotPrivateStreamClient(
        [
            new BinancePrivateStreamEvent(
                "outboundAccountPosition",
                updatedEventTimeUtc,
                [
                    new ExchangeBalanceSnapshot(
                        "USDT",
                        120m,
                        120m,
                        110m,
                        110m,
                        updatedEventTimeUtc,
                        10m,
                        ExchangeDataPlane.Spot)
                ],
                [],
                [],
                RequiresAccountRefresh: false,
                Plane: ExchangeDataPlane.Spot),
            new BinancePrivateStreamEvent(
                "balanceUpdate",
                updatedEventTimeUtc.AddSeconds(2),
                [],
                [],
                [],
                RequiresAccountRefresh: true,
                Plane: ExchangeDataPlane.Spot),
            new BinancePrivateStreamEvent(
                "listenKeyExpired",
                updatedEventTimeUtc.AddSeconds(3),
                [],
                [],
                [],
                RequiresAccountRefresh: false,
                Plane: ExchangeDataPlane.Spot)
        ]);
        var snapshotHub = new ExchangeAccountSnapshotHub();
        using var provider = BuildProvider(databaseName, databaseRoot, new FakeExchangeCredentialService(exchangeAccountId), timeProvider);
        var manager = new BinanceSpotPrivateStreamManager(
            provider.GetRequiredService<IServiceScopeFactory>(),
            restClient,
            streamClient,
            snapshotHub,
            Options.Create(new BinancePrivateDataOptions
            {
                Enabled = true,
                SpotRestBaseUrl = "https://api.binance.com",
                SpotWebSocketBaseUrl = "wss://stream.binance.com:9443",
                SessionScanIntervalSeconds = 15,
                ReconnectDelaySeconds = 1,
                ListenKeyRenewalIntervalMinutes = 30,
                ReconciliationIntervalMinutes = 5,
                RecvWindowMilliseconds = 5000
            }),
            timeProvider,
            NullLogger<BinanceSpotPrivateStreamManager>.Instance);

        await using var enumerator = snapshotHub.SubscribeAsync().GetAsyncEnumerator();
        var firstMoveNext = enumerator.MoveNextAsync().AsTask();
        var runTask = manager.RunSessionCycleAsync(new ExchangeSyncAccountDescriptor(exchangeAccountId, "user-spot", "Binance", ExchangeDataPlane.Spot));

        Assert.True(await firstMoveNext);
        var firstSnapshot = enumerator.Current;

        var secondMoveNext = enumerator.MoveNextAsync().AsTask();
        Assert.True(await secondMoveNext);
        var secondSnapshot = enumerator.Current;

        var thirdMoveNext = enumerator.MoveNextAsync().AsTask();
        var cycleResult = await runTask;

        Assert.True(await thirdMoveNext);
        var thirdSnapshot = enumerator.Current;

        Assert.NotNull(cycleResult);
        Assert.True(cycleResult!.ShouldReconnect);
        Assert.Equal(ExchangePrivateStreamConnectionState.ListenKeyExpired, cycleResult.ConnectionState);
        Assert.Equal(1, restClient.StartListenKeyCalls);
        Assert.Equal(1, restClient.CloseListenKeyCalls);
        Assert.Equal(100m, Assert.Single(firstSnapshot.Balances).WalletBalance);
        Assert.Equal(120m, Assert.Single(secondSnapshot.Balances).WalletBalance);
        Assert.Equal(150m, Assert.Single(thirdSnapshot.Balances).WalletBalance);
        Assert.Equal(20m, Assert.Single(thirdSnapshot.Balances).LockedBalance);
        Assert.Equal(ExchangeDataPlane.Spot, thirdSnapshot.Plane);

        await using var scope = provider.CreateAsyncScope();
        using var bypass = scope.ServiceProvider
            .GetRequiredService<IDataScopeContextAccessor>()
            .BeginScope(hasIsolationBypass: true);
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var state = await dbContext.ExchangeAccountSyncStates.SingleAsync(entity =>
            entity.ExchangeAccountId == exchangeAccountId &&
            entity.Plane == ExchangeDataPlane.Spot);

        Assert.Equal(ExchangePrivateStreamConnectionState.Connected, state.PrivateStreamConnectionState);
        Assert.NotNull(state.LastListenKeyStartedAtUtc);
        Assert.Equal(updatedEventTimeUtc.AddSeconds(2), state.LastPrivateStreamEventAtUtc);
    }

    [Fact]
    public async Task RunSessionCycleAsync_AppliesSpotExecutionReport_DuplicateSafely()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        var databaseName = Guid.NewGuid().ToString("N");
        var exchangeAccountId = Guid.NewGuid();
        var executionOrderId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(now);
        var seedSnapshot = CreateSnapshot(exchangeAccountId, walletBalance: 100m, freeBalance: 90m, lockedBalance: 10m, observedAtUtc: now.UtcDateTime);
        var refreshedSnapshot = CreateSnapshot(exchangeAccountId, walletBalance: 98m, freeBalance: 88m, lockedBalance: 10m, observedAtUtc: now.UtcDateTime.AddSeconds(5));
        var updateTimeUtc = now.UtcDateTime.AddSeconds(5);
        var restClient = new FakeSpotPrivateRestClient([seedSnapshot, refreshedSnapshot, refreshedSnapshot]);
        var streamClient = new FakeSpotPrivateStreamClient(
        [
            CreateExecutionReportEvent(executionOrderId, updateTimeUtc),
            CreateExecutionReportEvent(executionOrderId, updateTimeUtc),
            new BinancePrivateStreamEvent(
                "listenKeyExpired",
                updateTimeUtc.AddSeconds(1),
                [],
                [],
                [],
                RequiresAccountRefresh: false,
                Plane: ExchangeDataPlane.Spot)
        ]);
        var snapshotHub = new ExchangeAccountSnapshotHub();
        using var provider = BuildProvider(databaseName, databaseRoot, new FakeExchangeCredentialService(exchangeAccountId), timeProvider);
        await SeedExecutionOrderAsync(provider, exchangeAccountId, executionOrderId);
        var manager = new BinanceSpotPrivateStreamManager(
            provider.GetRequiredService<IServiceScopeFactory>(),
            restClient,
            streamClient,
            snapshotHub,
            Options.Create(new BinancePrivateDataOptions
            {
                Enabled = true,
                SpotRestBaseUrl = "https://api.binance.com",
                SpotWebSocketBaseUrl = "wss://stream.binance.com:9443",
                SessionScanIntervalSeconds = 15,
                ReconnectDelaySeconds = 1,
                ListenKeyRenewalIntervalMinutes = 30,
                ReconciliationIntervalMinutes = 5,
                RecvWindowMilliseconds = 5000
            }),
            timeProvider,
            NullLogger<BinanceSpotPrivateStreamManager>.Instance);

        await manager.RunSessionCycleAsync(new ExchangeSyncAccountDescriptor(exchangeAccountId, "user-spot", "Binance", ExchangeDataPlane.Spot));

        await using var scope = provider.CreateAsyncScope();
        using var bypass = scope.ServiceProvider
            .GetRequiredService<IDataScopeContextAccessor>()
            .BeginScope(hasIsolationBypass: true);
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var order = await dbContext.ExecutionOrders.SingleAsync(entity => entity.Id == executionOrderId);
        var transitions = await dbContext.ExecutionOrderTransitions
            .Where(entity => entity.ExecutionOrderId == executionOrderId && !entity.IsDeleted)
            .OrderBy(entity => entity.SequenceNumber)
            .ToListAsync();
        var auditLog = await dbContext.AuditLogs
            .OrderByDescending(entity => entity.CreatedDate)
            .FirstAsync();

        Assert.Equal(ExecutionOrderState.PartiallyFilled, order.State);
        Assert.Equal(0.02m, order.FilledQuantity);
        Assert.Equal(64000m, order.AverageFillPrice);
        var transition = Assert.Single(transitions);
        Assert.Contains("TradeId=77", transition.Detail, StringComparison.Ordinal);
        Assert.Contains("Fee=BNB:0.0001", transition.Detail, StringComparison.Ordinal);
        Assert.Contains("Plane=Spot", auditLog.Context, StringComparison.Ordinal);
    }

    private static BinancePrivateStreamEvent CreateExecutionReportEvent(Guid executionOrderId, DateTime eventTimeUtc)
    {
        return new BinancePrivateStreamEvent(
            "executionReport",
            eventTimeUtc,
            [],
            [],
            [
                new BinanceOrderStatusSnapshot(
                    "BTCUSDT",
                    "spot-order-1",
                    ExecutionClientOrderId.Create(executionOrderId),
                    "PARTIALLY_FILLED",
                    0.05m,
                    0.02m,
                    1280m,
                    64000m,
                    0.02m,
                    64000m,
                    eventTimeUtc,
                    "Binance.SpotPrivateStream.ExecutionReport",
                    TradeId: 77,
                    FeeAsset: "BNB",
                    FeeAmount: 0.0001m,
                    Plane: ExchangeDataPlane.Spot)
            ],
            RequiresAccountRefresh: true,
            Plane: ExchangeDataPlane.Spot);
    }

    private static ExchangeAccountSnapshot CreateSnapshot(
        Guid exchangeAccountId,
        decimal walletBalance,
        decimal freeBalance,
        decimal lockedBalance,
        DateTime observedAtUtc)
    {
        return new ExchangeAccountSnapshot(
            exchangeAccountId,
            "user-spot",
            "Binance",
            [
                new ExchangeBalanceSnapshot(
                    "USDT",
                    walletBalance,
                    walletBalance,
                    freeBalance,
                    freeBalance,
                    observedAtUtc,
                    lockedBalance,
                    ExchangeDataPlane.Spot)
            ],
            [],
            observedAtUtc,
            observedAtUtc,
            "Binance.SpotPrivateRest.Account",
            ExchangeDataPlane.Spot);
    }

    private static ServiceProvider BuildProvider(
        string databaseName,
        InMemoryDatabaseRoot databaseRoot,
        IExchangeCredentialService exchangeCredentialService,
        TimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddSingleton<ICorrelationContextAccessor, CorrelationContextAccessor>();
        services.AddSingleton(timeProvider);
        services.AddScoped<IDataScopeContextAccessor, DataScopeContextAccessor>();
        services.AddScoped<IDataScopeContext>(serviceProvider => serviceProvider.GetRequiredService<IDataScopeContextAccessor>());
        services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(databaseName, databaseRoot));
        services.AddScoped(_ => exchangeCredentialService);
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<ExchangeAccountSyncStateService>();
        services.AddScoped<ExecutionOrderLifecycleService>();

        return services.BuildServiceProvider();
    }

    private static async Task SeedExecutionOrderAsync(
        ServiceProvider provider,
        Guid exchangeAccountId,
        Guid executionOrderId)
    {
        await using var scope = provider.CreateAsyncScope();
        using var bypass = scope.ServiceProvider
            .GetRequiredService<IDataScopeContextAccessor>()
            .BeginScope(hasIsolationBypass: true);
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        dbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = executionOrderId,
            OwnerUserId = "user-spot",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            ExchangeAccountId = exchangeAccountId,
            StrategyKey = "spot-stream-core",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.05m,
            Price = 65000m,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            State = ExecutionOrderState.Submitted,
            IdempotencyKey = $"spot_stream_{executionOrderId:N}",
            RootCorrelationId = "root-spot-order-correlation-1",
            ExternalOrderId = "spot-order-1",
            SubmittedAtUtc = new DateTime(2026, 4, 5, 11, 55, 0, DateTimeKind.Utc),
            LastStateChangedAtUtc = new DateTime(2026, 4, 5, 11, 55, 0, DateTimeKind.Utc)
        });

        await dbContext.SaveChangesAsync();
    }

    private sealed class FakeExchangeCredentialService(Guid exchangeAccountId) : IExchangeCredentialService
    {
        public Task<ExchangeCredentialAccessResult> GetAsync(
            ExchangeCredentialAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(exchangeAccountId, request.ExchangeAccountId);
            Assert.Equal(ExchangeCredentialAccessPurpose.Synchronization, request.Purpose);

            return Task.FromResult(new ExchangeCredentialAccessResult(
                "api-key",
                "api-secret",
                new ExchangeCredentialStateSnapshot(
                    exchangeAccountId,
                    ExchangeCredentialStatus.Active,
                    Fingerprint: "fingerprint",
                    KeyVersion: "credential-v1",
                    StoredAtUtc: null,
                    LastValidatedAtUtc: null,
                    LastAccessedAtUtc: null,
                    LastRotatedAtUtc: null,
                    RevalidateAfterUtc: null,
                    RotateAfterUtc: null)));
        }

        public Task<ExchangeCredentialStateSnapshot> StoreAsync(StoreExchangeCredentialsRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExchangeCredentialStateSnapshot> SetValidationStateAsync(SetExchangeCredentialValidationStateRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExchangeCredentialStateSnapshot> GetStateAsync(Guid exchangeAccountId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeSpotPrivateRestClient(IEnumerable<ExchangeAccountSnapshot> snapshots) : IBinanceSpotPrivateRestClient
    {
        private readonly Queue<ExchangeAccountSnapshot> accountSnapshots = new(snapshots);

        public int StartListenKeyCalls { get; private set; }

        public int CloseListenKeyCalls { get; private set; }

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
                DateTime.UtcNow,
                "Binance.SpotPrivateRest.OrderPlacement",
                Plane: ExchangeDataPlane.Spot);

            return Task.FromResult(new BinanceOrderPlacementResult("spot-order-1", request.ClientOrderId, DateTime.UtcNow, snapshot));
        }

        public Task<ExchangeAccountSnapshot> GetAccountSnapshotAsync(
            Guid exchangeAccountId,
            string ownerUserId,
            string exchangeName,
            string apiKey,
            string apiSecret,
            CancellationToken cancellationToken = default)
        {
            if (accountSnapshots.Count == 0)
            {
                throw new InvalidOperationException("No spot snapshots queued.");
            }

            return Task.FromResult(accountSnapshots.Dequeue());
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
            StartListenKeyCalls++;
            return Task.FromResult("spot-listen-key");
        }

        public Task KeepAliveListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task CloseListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            CloseListenKeyCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSpotPrivateStreamClient(IReadOnlyCollection<BinancePrivateStreamEvent> events) : IBinanceSpotPrivateStreamClient
    {
        public async IAsyncEnumerable<BinancePrivateStreamEvent> StreamAsync(
            string listenKey,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var streamEvent in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return streamEvent;
                await Task.Yield();
            }
        }
    }
}
