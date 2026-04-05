using System.Net;
using System.Text;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.Dashboard;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.IntegrationTests.Exchange;

public sealed class SpotPrivatePlaneIntegrationTests
{
    [Fact]
    public async Task SpotSnapshotSync_PersistsLockedBalances_WithoutOverwritingFuturesPlane()
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString($"CoinBotSpotSyncInt_{Guid.NewGuid():N}");
        var observedAtUtc = new DateTime(2026, 4, 5, 12, 0, 0, DateTimeKind.Utc);
        var exchangeAccountId = Guid.NewGuid();
        await using var context = CreateContext(connectionString);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        try
        {
            context.Users.Add(new CoinBot.Infrastructure.Identity.ApplicationUser
            {
                Id = "user-spot-int",
                UserName = "user-spot-int",
                NormalizedUserName = "USER-SPOT-INT",
                Email = "user-spot-int@coinbot.test",
                NormalizedEmail = "USER-SPOT-INT@COINBOT.TEST",
                FullName = "user-spot-int",
                EmailConfirmed = true
            });
            context.ExchangeAccounts.Add(new ExchangeAccount
            {
                Id = exchangeAccountId,
                OwnerUserId = "user-spot-int",
                ExchangeName = "Binance",
                DisplayName = "Primary",
                CredentialStatus = ExchangeCredentialStatus.Active,
                ApiKeyCiphertext = "cipher-api-key",
                ApiSecretCiphertext = "cipher-api-secret"
            });
            context.ExchangeBalances.Add(new ExchangeBalance
            {
                OwnerUserId = "user-spot-int",
                ExchangeAccountId = exchangeAccountId,
                Plane = ExchangeDataPlane.Futures,
                Asset = "BUSD",
                WalletBalance = 50m,
                CrossWalletBalance = 50m,
                AvailableBalance = 50m,
                MaxWithdrawAmount = 50m,
                ExchangeUpdatedAtUtc = observedAtUtc,
                SyncedAtUtc = observedAtUtc
            });
            await context.SaveChangesAsync();

            var spotSnapshot = new ExchangeAccountSnapshot(
                exchangeAccountId,
                "user-spot-int",
                "Binance",
                [
                    new ExchangeBalanceSnapshot("USDT", 150m, 150m, 130m, 130m, observedAtUtc, 20m, ExchangeDataPlane.Spot),
                    new ExchangeBalanceSnapshot("BTC", 0.5m, 0.5m, 0.5m, 0.5m, observedAtUtc, 0m, ExchangeDataPlane.Spot)
                ],
                [],
                observedAtUtc,
                observedAtUtc,
                "Binance.SpotPrivateRest.Account",
                ExchangeDataPlane.Spot);

            var balanceSyncService = new ExchangeBalanceSyncService(context, NullLogger<ExchangeBalanceSyncService>.Instance);
            var positionSyncService = new ExchangePositionSyncService(context, NullLogger<ExchangePositionSyncService>.Instance);
            var syncStateService = new ExchangeAccountSyncStateService(context);

            await balanceSyncService.ApplyAsync(spotSnapshot);
            await positionSyncService.ApplyAsync(spotSnapshot);
            await syncStateService.RecordBalanceSyncAsync(spotSnapshot);
            await syncStateService.RecordPositionSyncAsync(spotSnapshot);

            var futuresBalance = await context.ExchangeBalances.SingleAsync(entity =>
                entity.ExchangeAccountId == exchangeAccountId &&
                entity.Plane == ExchangeDataPlane.Futures &&
                entity.Asset == "BUSD");
            var spotBalances = await context.ExchangeBalances
                .Where(entity =>
                    entity.ExchangeAccountId == exchangeAccountId &&
                    entity.Plane == ExchangeDataPlane.Spot &&
                    !entity.IsDeleted)
                .OrderBy(entity => entity.Asset)
                .ToListAsync();
            var spotSyncState = await context.ExchangeAccountSyncStates.SingleAsync(entity =>
                entity.ExchangeAccountId == exchangeAccountId &&
                entity.Plane == ExchangeDataPlane.Spot);
            var readModel = new UserDashboardPortfolioReadModelService(context);
            var portfolio = await readModel.GetSnapshotAsync("user-spot-int");

            Assert.False(futuresBalance.IsDeleted);
            Assert.Equal(["BTC", "USDT"], spotBalances.Select(entity => entity.Asset).ToArray());
            Assert.Equal(20m, spotBalances.Single(entity => entity.Asset == "USDT").LockedBalance);
            Assert.NotNull(spotSyncState.LastBalanceSyncedAtUtc);
            Assert.Contains(portfolio.Balances, entity => entity.Asset == "USDT" && entity.LockedBalance == 20m && entity.Plane == ExchangeDataPlane.Spot);
            Assert.Contains(portfolio.Balances, entity => entity.Asset == "BUSD" && entity.Plane == ExchangeDataPlane.Futures);
        }
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task ExchangeSyncAccountSelection_SeparatesSpotAndFuturesAccounts_FailClosed()
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString($"CoinBotSpotSelectionInt_{Guid.NewGuid():N}");
        await using var context = CreateContext(connectionString);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        try
        {
            var spotOnlyAccountId = Guid.NewGuid();
            var futuresOnlyAccountId = Guid.NewGuid();
            var bothAccountId = Guid.NewGuid();

            context.Users.AddRange(
                CreateUser("user-spot-only"),
                CreateUser("user-futures-only"),
                CreateUser("user-both"));
            var spotOnlyCredentialId = Guid.NewGuid();
            var futuresOnlyCredentialId = Guid.NewGuid();
            var bothCredentialId = Guid.NewGuid();
            context.ExchangeAccounts.AddRange(
                CreateExchangeAccount(spotOnlyAccountId, "user-spot-only"),
                CreateExchangeAccount(futuresOnlyAccountId, "user-futures-only"),
                CreateExchangeAccount(bothAccountId, "user-both"));
            context.ApiCredentials.AddRange(
                CreateCredential(spotOnlyCredentialId, spotOnlyAccountId, "user-spot-only"),
                CreateCredential(futuresOnlyCredentialId, futuresOnlyAccountId, "user-futures-only"),
                CreateCredential(bothCredentialId, bothAccountId, "user-both"));
            context.ApiCredentialValidations.AddRange(
                CreateValidation(spotOnlyCredentialId, spotOnlyAccountId, "user-spot-only", supportsSpot: true, supportsFutures: false),
                CreateValidation(futuresOnlyCredentialId, futuresOnlyAccountId, "user-futures-only", supportsSpot: false, supportsFutures: true),
                CreateValidation(bothCredentialId, bothAccountId, "user-both", supportsSpot: true, supportsFutures: true));
            await context.SaveChangesAsync();

            var spotAccounts = await ExchangeSyncAccountSelection.ListAsync(context, ExchangeDataPlane.Spot);
            var futuresAccounts = await ExchangeSyncAccountSelection.ListAsync(context, ExchangeDataPlane.Futures);

            Assert.Equal(
                new[] { bothAccountId, spotOnlyAccountId }.OrderBy(entity => entity.ToString("N")).ToArray(),
                spotAccounts.Select(entity => entity.ExchangeAccountId).OrderBy(entity => entity.ToString("N")).ToArray());
            Assert.Equal(
                new[] { bothAccountId, futuresOnlyAccountId }.OrderBy(entity => entity.ToString("N")).ToArray(),
                futuresAccounts.Select(entity => entity.ExchangeAccountId).OrderBy(entity => entity.ToString("N")).ToArray());
            Assert.DoesNotContain(futuresAccounts, entity => entity.ExchangeAccountId == spotOnlyAccountId);
            Assert.DoesNotContain(spotAccounts, entity => entity.ExchangeAccountId == futuresOnlyAccountId);
        }
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task SpotOrderStatusReadPath_AppliesSpotSnapshotToExecutionOrder()
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString($"CoinBotSpotOrderReadInt_{Guid.NewGuid():N}");
        var exchangeAccountId = Guid.NewGuid();
        var executionOrderId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);

        await using (var setupContext = CreateContext(connectionString))
        {
            await setupContext.Database.EnsureDeletedAsync();
            await setupContext.Database.EnsureCreatedAsync();
            setupContext.Users.Add(CreateUser("user-spot-stream"));
            await setupContext.SaveChangesAsync();
        }

        using var provider = BuildProvider(connectionString, new FakeExchangeCredentialService(exchangeAccountId), timeProvider);
        await SeedExecutionOrderAsync(provider, exchangeAccountId, executionOrderId);

        using var handler = new RecordingMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"symbol":"BTCUSDT","orderId":"spot-order-1","clientOrderId":"{{ExecutionClientOrderId.Create(executionOrderId)}}","status":"PARTIALLY_FILLED","origQty":"0.05","executedQty":"0.02","cummulativeQuoteQty":"1280.00","updateTime":1710000000123}""",
                Encoding.UTF8,
                "application/json")
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.binance.com") };
        var restClient = new BinanceSpotPrivateRestClient(
            httpClient,
            Options.Create(new BinancePrivateDataOptions
            {
                Enabled = true,
                SpotRestBaseUrl = "https://api.binance.com",
                SpotWebSocketBaseUrl = "wss://stream.binance.com:9443",
                RecvWindowMilliseconds = 5000
            }),
            timeProvider,
            new FakeSpotTimeSyncService(),
            NullLogger<BinanceSpotPrivateRestClient>.Instance);

        var snapshot = await restClient.GetOrderAsync(
            new BinanceOrderQueryRequest(
                exchangeAccountId,
                "BTCUSDT",
                ExchangeOrderId: null,
                ClientOrderId: ExecutionClientOrderId.Create(executionOrderId),
                ApiKey: "api-key",
                ApiSecret: "api-secret"));

        await using var scope = provider.CreateAsyncScope();
        using var bypass = scope.ServiceProvider.GetRequiredService<IDataScopeContextAccessor>().BeginScope(hasIsolationBypass: true);
        var lifecycleService = scope.ServiceProvider.GetRequiredService<ExecutionOrderLifecycleService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await lifecycleService.ApplyReconciliationAsync(
            executionOrderId,
            snapshot,
            ExchangeStateDriftStatus.InSync,
            "Spot order status integration read path.");

        var order = await dbContext.ExecutionOrders.SingleAsync(entity => entity.Id == executionOrderId);
        var transition = await dbContext.ExecutionOrderTransitions
            .Where(entity => entity.ExecutionOrderId == executionOrderId && !entity.IsDeleted)
            .SingleAsync();

        Assert.NotNull(handler.LastRequestUri);
        Assert.Contains("/api/v3/order", handler.LastRequestUri!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("/fapi/", handler.LastRequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains("origClientOrderId=", handler.LastRequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExecutionOrderState.PartiallyFilled, order.State);
        Assert.Equal(0.02m, order.FilledQuantity);
        Assert.Equal(64000m, order.AverageFillPrice);
        Assert.Contains("Plane=Spot", transition.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpotPrivateStreamManager_ProcessesFakeEvents_EndToEnd()
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString($"CoinBotSpotStreamInt_{Guid.NewGuid():N}");
        var exchangeAccountId = Guid.NewGuid();
        var executionOrderId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        var seedSnapshot = CreateSpotSnapshot(exchangeAccountId, 100m, 90m, 10m, now.UtcDateTime, "user-spot-stream");
        var refreshedSnapshot = CreateSpotSnapshot(exchangeAccountId, 98m, 88m, 10m, now.UtcDateTime.AddSeconds(5), "user-spot-stream");
        var restClient = new FakeSpotPrivateRestClient([seedSnapshot, refreshedSnapshot]);
        var streamClient = new FakeSpotPrivateStreamClient(
        [
            new BinancePrivateStreamEvent(
                "executionReport",
                now.UtcDateTime.AddSeconds(5),
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
                        now.UtcDateTime.AddSeconds(5),
                        "Binance.SpotPrivateStream.ExecutionReport",
                        TradeId: 77,
                        FeeAsset: "BNB",
                        FeeAmount: 0.0001m,
                        Plane: ExchangeDataPlane.Spot)
                ],
                RequiresAccountRefresh: true,
                Plane: ExchangeDataPlane.Spot),
            new BinancePrivateStreamEvent(
                "listenKeyExpired",
                now.UtcDateTime.AddSeconds(6),
                [],
                [],
                [],
                RequiresAccountRefresh: false,
                Plane: ExchangeDataPlane.Spot)
        ]);
        await using (var setupContext = CreateContext(connectionString))
        {
            await setupContext.Database.EnsureDeletedAsync();
            await setupContext.Database.EnsureCreatedAsync();
            setupContext.Users.Add(CreateUser("user-spot-stream"));
            await setupContext.SaveChangesAsync();
        }

        using var provider = BuildProvider(connectionString, new FakeExchangeCredentialService(exchangeAccountId), timeProvider);
        await SeedExecutionOrderAsync(provider, exchangeAccountId, executionOrderId);
        var manager = new BinanceSpotPrivateStreamManager(
            provider.GetRequiredService<IServiceScopeFactory>(),
            restClient,
            streamClient,
            new ExchangeAccountSnapshotHub(),
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

        try
        {
            var cycleResult = await manager.RunSessionCycleAsync(new ExchangeSyncAccountDescriptor(exchangeAccountId, "user-spot-stream", "Binance", ExchangeDataPlane.Spot));

            Assert.NotNull(cycleResult);
            Assert.True(cycleResult!.ShouldReconnect);
            Assert.Equal(ExchangePrivateStreamConnectionState.ListenKeyExpired, cycleResult.ConnectionState);

            await using var scope = provider.CreateAsyncScope();
            using var bypass = scope.ServiceProvider.GetRequiredService<IDataScopeContextAccessor>().BeginScope(hasIsolationBypass: true);
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var order = await dbContext.ExecutionOrders.SingleAsync(entity => entity.Id == executionOrderId);
            var transition = await dbContext.ExecutionOrderTransitions.SingleAsync(entity => entity.ExecutionOrderId == executionOrderId);
            var spotState = await dbContext.ExchangeAccountSyncStates.SingleAsync(entity =>
                entity.ExchangeAccountId == exchangeAccountId &&
                entity.Plane == ExchangeDataPlane.Spot);

            Assert.Equal(ExecutionOrderState.PartiallyFilled, order.State);
            Assert.Equal(0.02m, order.FilledQuantity);
            Assert.Contains("TradeId=77", transition.Detail, StringComparison.Ordinal);
            Assert.Equal(ExchangePrivateStreamConnectionState.Connected, spotState.PrivateStreamConnectionState);
        }
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task SpotExecutionSubmitPath_UsesSpotEndpoint_AndPersistsLifecycle()
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString($"CoinBotSpotExecSubmitInt_{Guid.NewGuid():N}");
        var exchangeAccountId = Guid.NewGuid();
        var executionOrderId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        await using var context = CreateContext(connectionString);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        try
        {
            context.Users.Add(CreateUser("user-spot-submit"));
            var credentialId = Guid.NewGuid();
            context.ExchangeAccounts.Add(CreateExchangeAccount(exchangeAccountId, "user-spot-submit"));
            context.ApiCredentials.Add(CreateCredential(credentialId, exchangeAccountId, "user-spot-submit"));
            context.ApiCredentialValidations.Add(CreateValidation(credentialId, exchangeAccountId, "user-spot-submit", supportsSpot: true, supportsFutures: true));
            context.ExchangeBalances.AddRange(
                new ExchangeBalance
                {
                    OwnerUserId = "user-spot-submit",
                    ExchangeAccountId = exchangeAccountId,
                    Plane = ExchangeDataPlane.Spot,
                    Asset = "USDT",
                    WalletBalance = 5000m,
                    CrossWalletBalance = 5000m,
                    AvailableBalance = 4950m,
                    MaxWithdrawAmount = 4950m,
                    LockedBalance = 50m,
                    ExchangeUpdatedAtUtc = now.UtcDateTime,
                    SyncedAtUtc = now.UtcDateTime
                },
                new ExchangeBalance
                {
                    OwnerUserId = "user-spot-submit",
                    ExchangeAccountId = exchangeAccountId,
                    Plane = ExchangeDataPlane.Spot,
                    Asset = "BTC",
                    WalletBalance = 1m,
                    CrossWalletBalance = 1m,
                    AvailableBalance = 0.9m,
                    MaxWithdrawAmount = 0.9m,
                    LockedBalance = 0.1m,
                    ExchangeUpdatedAtUtc = now.UtcDateTime,
                    SyncedAtUtc = now.UtcDateTime
                },
                new ExchangeBalance
                {
                    OwnerUserId = "user-spot-submit",
                    ExchangeAccountId = exchangeAccountId,
                    Plane = ExchangeDataPlane.Futures,
                    Asset = "USDT",
                    WalletBalance = 25m,
                    CrossWalletBalance = 25m,
                    AvailableBalance = 25m,
                    MaxWithdrawAmount = 25m,
                    LockedBalance = 0m,
                    ExchangeUpdatedAtUtc = now.UtcDateTime,
                    SyncedAtUtc = now.UtcDateTime
                });
            context.ExecutionOrders.Add(new ExecutionOrder
            {
                Id = executionOrderId,
                OwnerUserId = "user-spot-submit",
                TradingStrategyId = Guid.NewGuid(),
                TradingStrategyVersionId = Guid.NewGuid(),
                StrategySignalId = Guid.NewGuid(),
                SignalType = StrategySignalType.Entry,
                ExchangeAccountId = exchangeAccountId,
                Plane = ExchangeDataPlane.Spot,
                StrategyKey = "spot-submit-core",
                Symbol = "BTCUSDT",
                Timeframe = "1m",
                BaseAsset = "BTC",
                QuoteAsset = "USDT",
                Side = ExecutionOrderSide.Buy,
                OrderType = ExecutionOrderType.Market,
                Quantity = 0.05m,
                Price = 64000m,
                ExecutionEnvironment = ExecutionEnvironment.Live,
                ExecutorKind = ExecutionOrderExecutorKind.Binance,
                State = ExecutionOrderState.Submitted,
                SubmittedToBroker = true,
                IdempotencyKey = $"spot_submit_{executionOrderId:N}",
                RootCorrelationId = "root-spot-submit-correlation-1",
                SubmittedAtUtc = now.UtcDateTime,
                LastStateChangedAtUtc = now.UtcDateTime
            });
            await context.SaveChangesAsync();

            using var handler = new RecordingMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"symbol":"BTCUSDT","orderId":"spot-order-1","clientOrderId":"{{ExecutionClientOrderId.Create(executionOrderId)}}","status":"PARTIALLY_FILLED","origQty":"0.05","executedQty":"0.02","cummulativeQuoteQty":"1280.00","transactTime":1710000000123,"fills":[{"tradeId":77,"price":"64000","qty":"0.02","commission":"0.0001","commissionAsset":"BNB"}]}""",
                    Encoding.UTF8,
                    "application/json")
            });
            using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.binance.com") };
            var restClient = new BinanceSpotPrivateRestClient(
                httpClient,
                Options.Create(new BinancePrivateDataOptions
                {
                    Enabled = true,
                    SpotRestBaseUrl = "https://api.binance.com",
                    SpotWebSocketBaseUrl = "wss://stream.binance.com:9443",
                    RecvWindowMilliseconds = 5000
                }),
                timeProvider,
                new FakeSpotTimeSyncService(),
                NullLogger<BinanceSpotPrivateRestClient>.Instance);
            var executor = new BinanceSpotExecutor(
                context,
                new FakeExchangeCredentialService(exchangeAccountId),
                restClient,
                NullLogger<BinanceSpotExecutor>.Instance,
                marketDataService: new FakeMarketDataService(CreateSpotSymbolMetadata(now.UtcDateTime)),
                exchangeInfoClient: new FakeExchangeInfoClient(CreateSpotSymbolMetadata(now.UtcDateTime)));
            var lifecycleService = new ExecutionOrderLifecycleService(
                context,
                new AuditLogService(context, new CorrelationContextAccessor()),
                timeProvider,
                NullLogger<ExecutionOrderLifecycleService>.Instance);
            var order = await context.ExecutionOrders.SingleAsync(entity => entity.Id == executionOrderId);

            var dispatchResult = await executor.DispatchAsync(
                order,
                new ExecutionCommand(
                    Actor: "system:spot-submit-int",
                    OwnerUserId: "user-spot-submit",
                    TradingStrategyId: order.TradingStrategyId,
                    TradingStrategyVersionId: order.TradingStrategyVersionId,
                    StrategySignalId: order.StrategySignalId,
                    SignalType: StrategySignalType.Entry,
                    StrategyKey: "spot-submit-core",
                    Symbol: "BTCUSDT",
                    Timeframe: "1m",
                    BaseAsset: "BTC",
                    QuoteAsset: "USDT",
                    Side: ExecutionOrderSide.Buy,
                    OrderType: ExecutionOrderType.Market,
                    Quantity: 0.05m,
                    Price: 64000m,
                    ExchangeAccountId: exchangeAccountId,
                    IsDemo: false,
                    CorrelationId: "corr-spot-submit-int",
                    Context: "SpotExecutionSubmitIntegration",
                    Plane: ExchangeDataPlane.Spot),
                CancellationToken.None);
            var applied = await lifecycleService.ApplyExchangeUpdateAsync(dispatchResult.InitialSnapshot!, CancellationToken.None);
            context.ChangeTracker.Clear();

            var persistedOrder = await context.ExecutionOrders.SingleAsync(entity => entity.Id == executionOrderId);
            var transitions = await context.ExecutionOrderTransitions
                .Where(entity => entity.ExecutionOrderId == executionOrderId && !entity.IsDeleted)
                .OrderBy(entity => entity.SequenceNumber)
                .ToListAsync();

            Assert.True(applied);
            Assert.NotNull(handler.LastRequestUri);
            Assert.Contains("/api/v3/order", handler.LastRequestUri!.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("/fapi/", handler.LastRequestUri!.ToString(), StringComparison.Ordinal);
            Assert.Equal("spot-order-1", dispatchResult.ExternalOrderId);
            Assert.Equal(ExchangeDataPlane.Spot, dispatchResult.InitialSnapshot!.Plane);
            Assert.Equal(ExecutionOrderState.PartiallyFilled, persistedOrder.State);
            Assert.Equal(0.02m, persistedOrder.FilledQuantity);
            Assert.Equal(64000m, persistedOrder.AverageFillPrice);
            Assert.Single(transitions);
            Assert.Contains("Plane=Spot", transitions[0].Detail, StringComparison.Ordinal);
            Assert.Contains("TradeId=77", transitions[0].Detail, StringComparison.Ordinal);
        }
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task SpotExecutionLifecycle_DuplicatePartialUpdate_DoesNotCorrupt_ThenFills()
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString($"CoinBotSpotExecLifecycleInt_{Guid.NewGuid():N}");
        var exchangeAccountId = Guid.NewGuid();
        var executionOrderId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        await using var context = CreateContext(connectionString);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        try
        {
            context.Users.Add(CreateUser("user-spot-lifecycle"));
            context.ExchangeAccounts.Add(CreateExchangeAccount(exchangeAccountId, "user-spot-lifecycle"));
            context.ExecutionOrders.Add(new ExecutionOrder
            {
                Id = executionOrderId,
                OwnerUserId = "user-spot-lifecycle",
                TradingStrategyId = Guid.NewGuid(),
                TradingStrategyVersionId = Guid.NewGuid(),
                StrategySignalId = Guid.NewGuid(),
                SignalType = StrategySignalType.Entry,
                ExchangeAccountId = exchangeAccountId,
                Plane = ExchangeDataPlane.Spot,
                StrategyKey = "spot-lifecycle-core",
                Symbol = "BTCUSDT",
                Timeframe = "1m",
                BaseAsset = "BTC",
                QuoteAsset = "USDT",
                Side = ExecutionOrderSide.Buy,
                OrderType = ExecutionOrderType.Market,
                Quantity = 0.05m,
                Price = 64000m,
                ExecutionEnvironment = ExecutionEnvironment.Live,
                ExecutorKind = ExecutionOrderExecutorKind.Binance,
                State = ExecutionOrderState.Submitted,
                SubmittedToBroker = true,
                ExternalOrderId = "spot-order-lifecycle-1",
                IdempotencyKey = $"spot_lifecycle_{executionOrderId:N}",
                RootCorrelationId = "root-spot-lifecycle-correlation-1",
                SubmittedAtUtc = now.UtcDateTime,
                LastStateChangedAtUtc = now.UtcDateTime
            });
            await context.SaveChangesAsync();

            var lifecycleService = new ExecutionOrderLifecycleService(
                context,
                new AuditLogService(context, new CorrelationContextAccessor()),
                timeProvider,
                NullLogger<ExecutionOrderLifecycleService>.Instance);
            var partialSnapshot = new BinanceOrderStatusSnapshot(
                "BTCUSDT",
                "spot-order-lifecycle-1",
                ExecutionClientOrderId.Create(executionOrderId),
                "PARTIALLY_FILLED",
                0.05m,
                0.02m,
                1280m,
                64000m,
                0.02m,
                64000m,
                now.UtcDateTime,
                "Binance.SpotPrivateRest.OrderPlacement",
                TradeId: 77,
                FeeAsset: "BNB",
                FeeAmount: 0.0001m,
                Plane: ExchangeDataPlane.Spot);
            var filledSnapshot = partialSnapshot with
            {
                Status = "FILLED",
                ExecutedQuantity = 0.05m,
                CumulativeQuoteQuantity = 3200m,
                AveragePrice = 64000m,
                LastExecutedQuantity = 0.03m,
                TradeId = 78,
                FeeAmount = 0.0002m,
                EventTimeUtc = now.UtcDateTime.AddSeconds(5)
            };

            Assert.True(await lifecycleService.ApplyExchangeUpdateAsync(partialSnapshot, CancellationToken.None));
            Assert.True(await lifecycleService.ApplyExchangeUpdateAsync(partialSnapshot, CancellationToken.None));
            Assert.True(await lifecycleService.ApplyExchangeUpdateAsync(filledSnapshot, CancellationToken.None));
            context.ChangeTracker.Clear();

            var persistedOrder = await context.ExecutionOrders.SingleAsync(entity => entity.Id == executionOrderId);
            var transitions = await context.ExecutionOrderTransitions
                .Where(entity => entity.ExecutionOrderId == executionOrderId && !entity.IsDeleted)
                .OrderBy(entity => entity.SequenceNumber)
                .ToListAsync();

            Assert.Equal(ExecutionOrderState.Filled, persistedOrder.State);
            Assert.Equal(0.05m, persistedOrder.FilledQuantity);
            Assert.Equal(64000m, persistedOrder.AverageFillPrice);
            Assert.Equal(2, transitions.Count);
            Assert.Equal(
                [ExecutionOrderState.PartiallyFilled, ExecutionOrderState.Filled],
                transitions.Select(entity => entity.State).ToArray());
            Assert.Contains("TradeId=77", transitions[0].Detail, StringComparison.Ordinal);
            Assert.Contains("TradeId=78", transitions[1].Detail, StringComparison.Ordinal);
        }
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }

    private static ApplicationDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private static CoinBot.Infrastructure.Identity.ApplicationUser CreateUser(string userId)
    {
        return new CoinBot.Infrastructure.Identity.ApplicationUser
        {
            Id = userId,
            UserName = userId,
            NormalizedUserName = userId.ToUpperInvariant(),
            Email = $"{userId}@coinbot.test",
            NormalizedEmail = $"{userId.ToUpperInvariant()}@COINBOT.TEST",
            FullName = userId,
            EmailConfirmed = true
        };
    }

    private static ExchangeAccount CreateExchangeAccount(Guid accountId, string ownerUserId)
    {
        return new ExchangeAccount
        {
            Id = accountId,
            OwnerUserId = ownerUserId,
            ExchangeName = "Binance",
            DisplayName = ownerUserId,
            CredentialStatus = ExchangeCredentialStatus.Active,
            ApiKeyCiphertext = "cipher-api-key",
            ApiSecretCiphertext = "cipher-api-secret"
        };
    }

    private static ApiCredential CreateCredential(Guid apiCredentialId, Guid exchangeAccountId, string ownerUserId)
    {
        return new ApiCredential
        {
            Id = apiCredentialId,
            ExchangeAccountId = exchangeAccountId,
            OwnerUserId = ownerUserId,
            ApiKeyCiphertext = "cipher-api-key",
            ApiSecretCiphertext = "cipher-api-secret",
            CredentialFingerprint = "fingerprint",
            KeyVersion = "credential-v1",
            EncryptedBlobVersion = 1,
            ValidationStatus = "Valid",
            PermissionSummary = "Integration",
            StoredAtUtc = new DateTime(2026, 4, 5, 10, 0, 0, DateTimeKind.Utc),
            LastValidatedAtUtc = new DateTime(2026, 4, 5, 10, 0, 0, DateTimeKind.Utc)
        };
    }

    private static ApiCredentialValidation CreateValidation(Guid apiCredentialId, Guid exchangeAccountId, string ownerUserId, bool supportsSpot, bool supportsFutures)
    {
        return new ApiCredentialValidation
        {
            Id = Guid.NewGuid(),
            ApiCredentialId = apiCredentialId,
            ExchangeAccountId = exchangeAccountId,
            OwnerUserId = ownerUserId,
            IsKeyValid = true,
            CanTrade = true,
            CanWithdraw = true,
            SupportsSpot = supportsSpot,
            SupportsFutures = supportsFutures,
            ValidationStatus = "Valid",
            PermissionSummary = "Integration",
            ValidatedAtUtc = new DateTime(2026, 4, 5, 10, 0, 0, DateTimeKind.Utc)
        };
    }

    private static ExchangeAccountSnapshot CreateSpotSnapshot(
        Guid exchangeAccountId,
        decimal walletBalance,
        decimal freeBalance,
        decimal lockedBalance,
        DateTime observedAtUtc,
        string ownerUserId)
    {
        return new ExchangeAccountSnapshot(
            exchangeAccountId,
            ownerUserId,
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

    private static SymbolMetadataSnapshot CreateSpotSymbolMetadata(DateTime observedAtUtc)
    {
        return new SymbolMetadataSnapshot(
            "BTCUSDT",
            "Binance",
            "BTC",
            "USDT",
            0.1m,
            0.001m,
            "TRADING",
            true,
            observedAtUtc)
        {
            MinQuantity = 0.001m,
            MinNotional = 100m,
            PricePrecision = 1,
            QuantityPrecision = 3
        };
    }

    private static ServiceProvider BuildProvider(
        string connectionString,
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
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(connectionString));
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
        using var bypass = scope.ServiceProvider.GetRequiredService<IDataScopeContextAccessor>().BeginScope(hasIsolationBypass: true);
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        dbContext.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = "user-spot-stream",
            ExchangeName = "Binance",
            DisplayName = "Stream",
            CredentialStatus = ExchangeCredentialStatus.Active,
            ApiKeyCiphertext = "cipher-api-key",
            ApiSecretCiphertext = "cipher-api-secret"
        });
        dbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = executionOrderId,
            OwnerUserId = "user-spot-stream",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            ExchangeAccountId = exchangeAccountId,
            Plane = ExchangeDataPlane.Spot,
            StrategyKey = "spot-integration",
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
            IdempotencyKey = $"spot_integration_{executionOrderId:N}",
            RootCorrelationId = "root-spot-integration-correlation-1",
            ExternalOrderId = "spot-order-1",
            SubmittedToBroker = true,
            SubmittedAtUtc = new DateTime(2026, 4, 5, 11, 55, 0, DateTimeKind.Utc),
            LastStateChangedAtUtc = new DateTime(2026, 4, 5, 11, 55, 0, DateTimeKind.Utc)
        });

        await dbContext.SaveChangesAsync();
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeSpotTimeSyncService : IBinanceSpotTimeSyncService
    {
        public Task<BinanceTimeSyncSnapshot> GetSnapshotAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            var synchronizedAtUtc = DateTime.UnixEpoch.AddMilliseconds(1710000000123L);

            return Task.FromResult(new BinanceTimeSyncSnapshot(
                synchronizedAtUtc,
                synchronizedAtUtc,
                0L,
                0,
                synchronizedAtUtc,
                "Synchronized",
                null));
        }

        public Task<long> GetCurrentTimestampMillisecondsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(1710000000123L);
        }
    }

    private sealed class FakeMarketDataService(SymbolMetadataSnapshot metadata) : IMarketDataService
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
            return ValueTask.FromResult<MarketPriceSnapshot?>(new MarketPriceSnapshot(symbol, 64000m, DateTime.UtcNow, DateTime.UtcNow, "SpotExecutionIntegration"));
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(metadata);
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }
    }

    private sealed class FakeExchangeInfoClient(SymbolMetadataSnapshot metadata) : IBinanceExchangeInfoClient
    {
        public Task<IReadOnlyCollection<SymbolMetadataSnapshot>> GetSymbolMetadataAsync(
            IReadOnlyCollection<string> symbols,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyCollection<SymbolMetadataSnapshot>>([metadata]);
        }

        public Task<DateTime?> GetServerTimeUtcAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<DateTime?>(DateTime.UtcNow);
        }
    }

    private sealed class FakeExchangeCredentialService(Guid exchangeAccountId) : IExchangeCredentialService
    {
        public Task<ExchangeCredentialAccessResult> GetAsync(
            ExchangeCredentialAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(exchangeAccountId, request.ExchangeAccountId);

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
            return Task.FromResult(accountSnapshots.Dequeue());
        }

        public Task<BinanceOrderStatusSnapshot> GetOrderAsync(BinanceOrderQueryRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyCollection<BinanceSpotTradeFillSnapshot>> GetTradeFillsAsync(BinanceOrderQueryRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string> StartListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            return Task.FromResult("spot-listen-key");
        }

        public Task KeepAliveListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task CloseListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
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

    private sealed class RecordingMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(responder(request));
        }
    }
}
