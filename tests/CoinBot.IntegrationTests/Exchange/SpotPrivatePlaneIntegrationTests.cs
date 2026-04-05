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

    [Fact]
    public async Task SpotExecutionLifecycle_ProjectsPortfolioHistoryAndAuditParity_EndToEnd()
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString($"CoinBotSpotPortfolioParityInt_{Guid.NewGuid():N}");
        var exchangeAccountId = Guid.NewGuid();
        var buyOrderId = Guid.NewGuid();
        var sellOrderId = Guid.NewGuid();
        var strategyId = Guid.NewGuid();
        var strategyVersionId = Guid.NewGuid();
        var strategySignalId = Guid.NewGuid();
        var scanCycleId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        await using var context = CreateContext(connectionString);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        try
        {
            context.Users.Add(CreateUser("user-spot-portfolio-e2e"));
            context.ExchangeAccounts.Add(CreateExchangeAccount(exchangeAccountId, "user-spot-portfolio-e2e"));
            context.ExchangeBalances.Add(new ExchangeBalance
            {
                OwnerUserId = "user-spot-portfolio-e2e",
                ExchangeAccountId = exchangeAccountId,
                Plane = ExchangeDataPlane.Spot,
                Asset = "BTC",
                WalletBalance = 1m,
                CrossWalletBalance = 1m,
                AvailableBalance = 0.75m,
                MaxWithdrawAmount = 0.75m,
                LockedBalance = 0.25m,
                ExchangeUpdatedAtUtc = now.UtcDateTime.AddSeconds(5),
                SyncedAtUtc = now.UtcDateTime.AddSeconds(5)
            });
            context.ExchangeAccountSyncStates.Add(new ExchangeAccountSyncState
            {
                OwnerUserId = "user-spot-portfolio-e2e",
                ExchangeAccountId = exchangeAccountId,
                Plane = ExchangeDataPlane.Spot,
                PrivateStreamConnectionState = ExchangePrivateStreamConnectionState.Connected,
                DriftStatus = ExchangeStateDriftStatus.InSync,
                LastPrivateStreamEventAtUtc = now.UtcDateTime.AddSeconds(5),
                LastBalanceSyncedAtUtc = now.UtcDateTime.AddSeconds(5),
                LastStateReconciledAtUtc = now.UtcDateTime.AddSeconds(5)
            });
            context.ExecutionOrders.AddRange(
                new ExecutionOrder
                {
                    Id = buyOrderId,
                    OwnerUserId = "user-spot-portfolio-e2e",
                    TradingStrategyId = strategyId,
                    TradingStrategyVersionId = strategyVersionId,
                    StrategySignalId = Guid.NewGuid(),
                    SignalType = StrategySignalType.Entry,
                    ExchangeAccountId = exchangeAccountId,
                    Plane = ExchangeDataPlane.Spot,
                    StrategyKey = "spot-portfolio-history",
                    Symbol = "BTCUSDT",
                    Timeframe = "1m",
                    BaseAsset = "BTC",
                    QuoteAsset = "USDT",
                    Side = ExecutionOrderSide.Buy,
                    OrderType = ExecutionOrderType.Market,
                    Quantity = 2m,
                    Price = 150m,
                    FilledQuantity = 2m,
                    AverageFillPrice = 150m,
                    ExecutionEnvironment = ExecutionEnvironment.Live,
                    ExecutorKind = ExecutionOrderExecutorKind.Binance,
                    State = ExecutionOrderState.Filled,
                    IdempotencyKey = "spot_buy_portfolio_e2e",
                    RootCorrelationId = "root-spot-buy-portfolio-e2e",
                    ExternalOrderId = "spot-buy-order-e2e",
                    SubmittedToBroker = true,
                    SubmittedAtUtc = now.UtcDateTime.AddMinutes(-5),
                    LastFilledAtUtc = now.UtcDateTime.AddMinutes(-5),
                    LastStateChangedAtUtc = now.UtcDateTime.AddMinutes(-5)
                },
                new ExecutionOrder
                {
                    Id = sellOrderId,
                    OwnerUserId = "user-spot-portfolio-e2e",
                    TradingStrategyId = strategyId,
                    TradingStrategyVersionId = strategyVersionId,
                    StrategySignalId = strategySignalId,
                    SignalType = StrategySignalType.Entry,
                    ExchangeAccountId = exchangeAccountId,
                    Plane = ExchangeDataPlane.Spot,
                    StrategyKey = "spot-portfolio-history",
                    Symbol = "BTCUSDT",
                    Timeframe = "1m",
                    BaseAsset = "BTC",
                    QuoteAsset = "USDT",
                    Side = ExecutionOrderSide.Sell,
                    OrderType = ExecutionOrderType.Market,
                    Quantity = 1m,
                    Price = 280m,
                    ExecutionEnvironment = ExecutionEnvironment.Live,
                    ExecutorKind = ExecutionOrderExecutorKind.Binance,
                    State = ExecutionOrderState.Submitted,
                    IdempotencyKey = "spot_sell_portfolio_e2e",
                    RootCorrelationId = "root-spot-sell-portfolio-e2e",
                    ExternalOrderId = "spot-sell-order-e2e",
                    SubmittedToBroker = true,
                    SubmittedAtUtc = now.UtcDateTime,
                    LastStateChangedAtUtc = now.UtcDateTime
                });
            context.SpotPortfolioFills.AddRange(
                new SpotPortfolioFill
                {
                    OwnerUserId = "user-spot-portfolio-e2e",
                    ExchangeAccountId = exchangeAccountId,
                    ExecutionOrderId = buyOrderId,
                    Plane = ExchangeDataPlane.Spot,
                    Symbol = "BTCUSDT",
                    BaseAsset = "BTC",
                    QuoteAsset = "USDT",
                    Side = ExecutionOrderSide.Buy,
                    ExchangeOrderId = "spot-buy-order-e2e",
                    ClientOrderId = "cb_spot_buy_portfolio_e2e",
                    TradeId = 1,
                    Quantity = 1m,
                    QuoteQuantity = 100m,
                    Price = 100m,
                    FeeAmountInQuote = 0m,
                    RealizedPnlDelta = 0m,
                    HoldingQuantityAfter = 1m,
                    HoldingCostBasisAfter = 100m,
                    HoldingAverageCostAfter = 100m,
                    CumulativeRealizedPnlAfter = 0m,
                    CumulativeFeesInQuoteAfter = 0m,
                    Source = "Binance.SpotPrivateRest.MyTrades",
                    RootCorrelationId = "root-spot-buy-portfolio-e2e",
                    OccurredAtUtc = now.UtcDateTime.AddMinutes(-5)
                },
                new SpotPortfolioFill
                {
                    OwnerUserId = "user-spot-portfolio-e2e",
                    ExchangeAccountId = exchangeAccountId,
                    ExecutionOrderId = buyOrderId,
                    Plane = ExchangeDataPlane.Spot,
                    Symbol = "BTCUSDT",
                    BaseAsset = "BTC",
                    QuoteAsset = "USDT",
                    Side = ExecutionOrderSide.Buy,
                    ExchangeOrderId = "spot-buy-order-e2e",
                    ClientOrderId = "cb_spot_buy_portfolio_e2e",
                    TradeId = 2,
                    Quantity = 1m,
                    QuoteQuantity = 200m,
                    Price = 200m,
                    FeeAmountInQuote = 0m,
                    RealizedPnlDelta = 0m,
                    HoldingQuantityAfter = 2m,
                    HoldingCostBasisAfter = 300m,
                    HoldingAverageCostAfter = 150m,
                    CumulativeRealizedPnlAfter = 0m,
                    CumulativeFeesInQuoteAfter = 0m,
                    Source = "Binance.SpotPrivateRest.MyTrades",
                    RootCorrelationId = "root-spot-buy-portfolio-e2e",
                    OccurredAtUtc = now.UtcDateTime.AddMinutes(-5).AddSeconds(1)
                });
            context.MarketScannerCycles.Add(new MarketScannerCycle
            {
                Id = scanCycleId,
                StartedAtUtc = now.UtcDateTime.AddSeconds(-10),
                CompletedAtUtc = now.UtcDateTime.AddSeconds(-5),
                UniverseSource = "sql-test",
                ScannedSymbolCount = 1,
                EligibleCandidateCount = 1,
                TopCandidateCount = 1,
                BestCandidateSymbol = "BTCUSDT",
                BestCandidateScore = 180m,
                Summary = "sql-test"
            });
            context.MarketScannerCandidates.Add(new MarketScannerCandidate
            {
                Id = candidateId,
                ScanCycleId = scanCycleId,
                Symbol = "BTCUSDT",
                UniverseSource = "sql-test",
                ObservedAtUtc = now.UtcDateTime.AddSeconds(-5),
                IsEligible = true,
                Rank = 1,
                MarketScore = 120m,
                StrategyScore = 60,
                Score = 180m,
                ScoringSummary = "MarketScore=120; StrategyScore=60; CompositeScore=180"
            });
            context.MarketScannerHandoffAttempts.Add(new MarketScannerHandoffAttempt
            {
                Id = Guid.NewGuid(),
                ScanCycleId = scanCycleId,
                SelectedCandidateId = candidateId,
                SelectedSymbol = "BTCUSDT",
                SelectedTimeframe = "1m",
                SelectedAtUtc = now.UtcDateTime.AddSeconds(-5),
                CandidateRank = 1,
                CandidateMarketScore = 120m,
                CandidateScore = 180m,
                SelectionReason = "Top-ranked eligible candidate selected. Symbol=BTCUSDT; Rank=1.",
                OwnerUserId = "user-spot-portfolio-e2e",
                StrategyKey = "spot-portfolio-history",
                TradingStrategyId = strategyId,
                TradingStrategyVersionId = strategyVersionId,
                StrategySignalId = strategySignalId,
                StrategyDecisionOutcome = "Persisted",
                StrategyScore = 80,
                RiskOutcome = "Allowed",
                RiskVetoReasonCode = "None",
                RiskSummary = "Reason=None; Scope=User:user-spot-portfolio-e2e/Bot:n/a/Symbol:BTCUSDT/Coin:BTC/Timeframe:1m.",
                ExecutionRequestStatus = "Prepared",
                ExecutionSide = ExecutionOrderSide.Sell,
                ExecutionOrderType = ExecutionOrderType.Market,
                ExecutionEnvironment = ExecutionEnvironment.Live,
                ExecutionQuantity = 1m,
                ExecutionPrice = 280m,
                GuardSummary = "ExecutionGate=Allowed; UserExecutionOverride=Allowed;",
                CorrelationId = "root-spot-sell-portfolio-e2e",
                CompletedAtUtc = now.UtcDateTime
            });
            context.DecisionTraces.Add(new DecisionTrace
            {
                Id = Guid.NewGuid(),
                StrategySignalId = strategySignalId,
                CorrelationId = "root-spot-sell-portfolio-e2e",
                DecisionId = "decision-spot-portfolio-e2e",
                UserId = "user-spot-portfolio-e2e",
                Symbol = "BTCUSDT",
                Timeframe = "1m",
                StrategyVersion = "1",
                SignalType = "Entry",
                RiskScore = 80,
                DecisionOutcome = "Allowed",
                LatencyMs = 11,
                SnapshotJson = "{}",
                CreatedAtUtc = now.UtcDateTime
            });
            context.ExecutionTraces.Add(new ExecutionTrace
            {
                Id = Guid.NewGuid(),
                ExecutionOrderId = sellOrderId,
                CorrelationId = "root-spot-sell-portfolio-e2e",
                ExecutionAttemptId = "exec-attempt-spot-portfolio-e2e",
                CommandId = "cmd-spot-portfolio-e2e",
                UserId = "user-spot-portfolio-e2e",
                Provider = "Binance.SpotPrivateRest",
                Endpoint = "/api/v3/order",
                ResponseMasked = "Accepted",
                CreatedAtUtc = now.UtcDateTime
            });
            await context.SaveChangesAsync();

            var restClient = new FakeSpotPrivateRestClient(
                [],
                [
                    [
                        new BinanceSpotTradeFillSnapshot(
                            "BTCUSDT",
                            "spot-sell-order-e2e",
                            ExecutionClientOrderId.Create(sellOrderId),
                            3,
                            0.4m,
                            100m,
                            250m,
                            "USDT",
                            5m,
                            now.UtcDateTime,
                            "Binance.SpotPrivateRest.MyTrades")
                    ],
                    [
                        new BinanceSpotTradeFillSnapshot(
                            "BTCUSDT",
                            "spot-sell-order-e2e",
                            ExecutionClientOrderId.Create(sellOrderId),
                            3,
                            0.4m,
                            100m,
                            250m,
                            "USDT",
                            5m,
                            now.UtcDateTime,
                            "Binance.SpotPrivateRest.MyTrades")
                    ],
                    [
                        new BinanceSpotTradeFillSnapshot(
                            "BTCUSDT",
                            "spot-sell-order-e2e",
                            ExecutionClientOrderId.Create(sellOrderId),
                            3,
                            0.4m,
                            100m,
                            250m,
                            "USDT",
                            5m,
                            now.UtcDateTime,
                            "Binance.SpotPrivateRest.MyTrades"),
                        new BinanceSpotTradeFillSnapshot(
                            "BTCUSDT",
                            "spot-sell-order-e2e",
                            ExecutionClientOrderId.Create(sellOrderId),
                            4,
                            0.6m,
                            180m,
                            300m,
                            "USDT",
                            5m,
                            now.UtcDateTime.AddSeconds(5),
                            "Binance.SpotPrivateRest.MyTrades")
                    ]
                ]);
            var marketDataService = new FixedPriceMarketDataService(
                new Dictionary<string, decimal>(StringComparer.Ordinal)
                {
                    ["BTCUSDT"] = 300m
                });
            var lifecycleService = new ExecutionOrderLifecycleService(
                context,
                new AuditLogService(context, new CorrelationContextAccessor()),
                timeProvider,
                NullLogger<ExecutionOrderLifecycleService>.Instance,
                spotPortfolioAccountingService: new SpotPortfolioAccountingService(
                    context,
                    new FakeExchangeCredentialService(exchangeAccountId),
                    restClient,
                    marketDataService,
                    NullLogger<SpotPortfolioAccountingService>.Instance));

            var partialSnapshot = new BinanceOrderStatusSnapshot(
                "BTCUSDT",
                "spot-sell-order-e2e",
                ExecutionClientOrderId.Create(sellOrderId),
                "PARTIALLY_FILLED",
                1m,
                0.4m,
                100m,
                250m,
                0.4m,
                250m,
                now.UtcDateTime,
                "Binance.SpotPrivateRest.OrderPlacement",
                TradeId: 3,
                FeeAsset: "USDT",
                FeeAmount: 5m,
                Plane: ExchangeDataPlane.Spot);
            var filledSnapshot = partialSnapshot with
            {
                Status = "FILLED",
                ExecutedQuantity = 1m,
                CumulativeQuoteQuantity = 280m,
                AveragePrice = 280m,
                LastExecutedQuantity = 0.6m,
                LastExecutedPrice = 300m,
                TradeId = 4,
                EventTimeUtc = now.UtcDateTime.AddSeconds(5)
            };

            Assert.True(await lifecycleService.ApplyExchangeUpdateAsync(partialSnapshot, CancellationToken.None));
            Assert.True(await lifecycleService.ApplyExchangeUpdateAsync(partialSnapshot, CancellationToken.None));
            Assert.True(await lifecycleService.ApplyExchangeUpdateAsync(filledSnapshot, CancellationToken.None));
            context.ChangeTracker.Clear();

            var persistedOrder = await context.ExecutionOrders.SingleAsync(entity => entity.Id == sellOrderId);
            var sellRows = await context.SpotPortfolioFills
                .Where(entity => entity.ExecutionOrderId == sellOrderId && !entity.IsDeleted)
                .OrderBy(entity => entity.TradeId)
                .ToListAsync();
            var portfolioAuditLogs = await context.AuditLogs
                .Where(entity =>
                    entity.CorrelationId == "root-spot-sell-portfolio-e2e" &&
                    (entity.Action == "SpotPortfolio.FillApplied" || entity.Action == "ExecutionOrder.ExchangeUpdate") &&
                    !entity.IsDeleted)
                .OrderBy(entity => entity.Action)
                .ThenBy(entity => entity.CreatedDate)
                .ToListAsync();
            var readModel = new UserDashboardPortfolioReadModelService(context, marketDataService);
            var portfolio = await readModel.GetSnapshotAsync("user-spot-portfolio-e2e");
            var holding = Assert.Single(portfolio.SpotHoldings!);
            var position = Assert.Single(portfolio.Positions, entity => entity.Plane == ExchangeDataPlane.Spot);
            var historyRow = Assert.Single(portfolio.TradeHistory, entity => entity.OrderId == sellOrderId);

            Assert.Equal(ExecutionOrderState.Filled, persistedOrder.State);
            Assert.Equal(1m, persistedOrder.FilledQuantity);
            Assert.Equal(280m, persistedOrder.AverageFillPrice);
            Assert.Equal([3L, 4L], sellRows.Select(entity => entity.TradeId).ToArray());
            Assert.Equal(120m, portfolio.RealizedPnl);
            Assert.Equal(150m, portfolio.UnrealizedPnl);
            Assert.Equal(270m, portfolio.TotalPnl);
            Assert.Equal(1m, holding.Quantity);
            Assert.Equal(0.75m, holding.AvailableQuantity);
            Assert.Equal(0.25m, holding.LockedQuantity);
            Assert.Equal(150m, holding.AverageCost);
            Assert.Equal(150m, holding.CostBasis);
            Assert.Equal(120m, holding.RealizedPnl);
            Assert.Equal(150m, holding.UnrealizedPnl);
            Assert.Equal(10m, holding.TotalFeesInQuote);
            Assert.Equal(1m, position.Quantity);
            Assert.Equal(150m, position.EntryPrice);
            Assert.Equal(150m, position.UnrealizedProfit);
            Assert.Equal(0.75m, position.AvailableQuantity);
            Assert.Equal(0.25m, position.LockedQuantity);
            Assert.Equal(120m, historyRow.RealizedPnl);
            Assert.Equal(150m, historyRow.UnrealizedPnlContribution);
            Assert.Equal(10m, historyRow.FeeAmountInQuote);
            Assert.Equal(2, historyRow.FillCount);
            Assert.Equal("3,4", historyRow.TradeIdsSummary);
            Assert.Contains("Plane=Spot", historyRow.ReasonChainSummary, StringComparison.Ordinal);
            Assert.Contains("AuditTrail=DecisionTrace:1; ExecutionTrace:1; AuditLog:6; Bot=n/a", historyRow.ReasonChainSummary, StringComparison.Ordinal);
            Assert.Contains("FillCount=2", historyRow.ExecutionResultSummary, StringComparison.Ordinal);
            Assert.Contains("FeeInQuote=10", historyRow.ExecutionResultSummary, StringComparison.Ordinal);
            Assert.Contains(portfolioAuditLogs, entity => entity.Action == "SpotPortfolio.FillApplied" && entity.Outcome == "Applied");
            Assert.Contains(portfolioAuditLogs, entity => entity.Action == "SpotPortfolio.FillApplied" && entity.Outcome == "DuplicateIgnored");
            Assert.Equal(6, portfolioAuditLogs.Count);
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

    private sealed class FakeSpotPrivateRestClient(
        IEnumerable<ExchangeAccountSnapshot> snapshots,
        IEnumerable<IReadOnlyCollection<BinanceSpotTradeFillSnapshot>>? tradeFillResponses = null) : IBinanceSpotPrivateRestClient
    {
        private readonly Queue<ExchangeAccountSnapshot> accountSnapshots = new(snapshots);
        private readonly Queue<IReadOnlyCollection<BinanceSpotTradeFillSnapshot>> tradeFillQueue = new(tradeFillResponses ?? []);

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
            return Task.FromResult<IReadOnlyCollection<BinanceSpotTradeFillSnapshot>>(
                tradeFillQueue.Count == 0
                    ? []
                    : tradeFillQueue.Dequeue());
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

    private sealed class FixedPriceMarketDataService(IReadOnlyDictionary<string, decimal> prices) : IMarketDataService
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
            return ValueTask.FromResult<MarketPriceSnapshot?>(
                prices.TryGetValue(symbol, out var price)
                    ? new MarketPriceSnapshot(symbol, price, DateTime.UtcNow, DateTime.UtcNow, "SpotPortfolioParityIntegration")
                    : null);
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(null);
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
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
