using CoinBot.Application.Abstractions.Alerts;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.DemoPortfolio;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Risk;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Alerts;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.DemoPortfolio;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Risk;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Execution;

public sealed class ExecutionEngineTests
{
    [Fact]
    public async Task DispatchAsync_RoutesToVirtualExecutor_WhenCommandRequestsDemo()
    {
        await using var harness = CreateHarness();
        var botId = Guid.NewGuid();
        await SeedBotAsync(harness.DbContext, "user-demo", botId, "demo-core");
        await SeedDemoWalletAsync(harness.DbContext, "user-demo", "AAVE", 0m);
        await SeedDemoWalletAsync(harness.DbContext, "user-demo", "USDT", 1000m);
        harness.MarketDataService.SetLatestPrice("AAVEUSDT", 100m, harness.TimeProvider.GetUtcNow().UtcDateTime, "unit-test");
        harness.MarketDataService.SetSymbolMetadata("AAVEUSDT", "AAVE", "USDT", 0.01m, 0.001m);
        await PrimeFreshMarketDataAsync(harness, "corr-demo-1", "AAVEUSDT", "1m");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-demo",
            context: "Open demo execution",
            correlationId: "corr-demo-2");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-demo",
                strategyKey: "demo-core",
                botId: botId,
                isDemo: true) with
            {
                Symbol = "AAVEUSDT",
                BaseAsset = "AAVE",
                QuoteAsset = "USDT",
                Quantity = 1m,
                Price = 100m
            },
            CancellationToken.None);

        var bot = await harness.DbContext.TradingBots
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.Id == botId);
        var usdtWallet = await harness.DbContext.DemoWallets.SingleAsync(entity => entity.OwnerUserId == "user-demo" && entity.Asset == "USDT");
        var aaveWallet = await harness.DbContext.DemoWallets.SingleAsync(entity => entity.OwnerUserId == "user-demo" && entity.Asset == "AAVE");
        var position = await harness.DbContext.DemoPositions.SingleAsync(entity => entity.OwnerUserId == "user-demo" && entity.Symbol == "AAVEUSDT");

        Assert.False(result.IsDuplicate);
        Assert.Equal(ExecutionEnvironment.Demo, result.Order.ExecutionEnvironment);
        Assert.Equal(ExecutionOrderExecutorKind.Virtual, result.Order.ExecutorKind);
        Assert.Equal(ExecutionOrderState.Filled, result.Order.State);
        Assert.Equal(1m, result.Order.FilledQuantity);
        Assert.Equal(100.06m, result.Order.AverageFillPrice);
        Assert.Equal(0, bot.OpenOrderCount);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(899.83994m, usdtWallet.AvailableBalance);
        Assert.Equal(0m, usdtWallet.ReservedBalance);
        Assert.Equal(1m, aaveWallet.AvailableBalance);
        Assert.Equal(1m, position.Quantity);
        Assert.Equal(
            [
                ExecutionOrderState.Received,
                ExecutionOrderState.GatePassed,
                ExecutionOrderState.Dispatching,
                ExecutionOrderState.Submitted,
                ExecutionOrderState.Filled
            ],
            result.Order.Transitions.Select(transition => transition.State).ToArray());
        var traces = await harness.DbContext.ExecutionTraces
            .Where(entity => entity.ExecutionOrderId == result.Order.ExecutionOrderId)
            .OrderBy(entity => entity.CreatedAtUtc)
            .ToListAsync();
        Assert.Collection(
            traces,
            dispatchTrace =>
            {
                Assert.Equal("VirtualExecutor", dispatchTrace.Provider);
                Assert.Equal("internal://virtual-executor/dispatch", dispatchTrace.Endpoint);
                Assert.Contains("OutboundBrokerRequested=False", dispatchTrace.RequestMasked, StringComparison.Ordinal);
                Assert.Contains("SimulatedFillPathUsed=True", dispatchTrace.ResponseMasked, StringComparison.Ordinal);
            },
            fillTrace =>
            {
                Assert.Equal("DemoFillSimulator", fillTrace.Provider);
                Assert.Equal("internal://demo-fill-simulator/submission", fillTrace.Endpoint);
                Assert.Contains("Phase=SubmissionFill", fillTrace.RequestMasked, StringComparison.Ordinal);
                Assert.Contains("State=Filled", fillTrace.ResponseMasked, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task DispatchAsync_RoutesDemoRuntimeToBinanceExecutor_WhenInternalDemoExecutionIsDisabled()
    {
        await using var harness = CreateHarness(
            environmentName: Environments.Development,
            allowInternalDemoExecution: false);
        var exchangeAccountId = Guid.NewGuid();
        await SeedExchangeAccountAsync(harness.DbContext, "user-demo-runtime", exchangeAccountId);
        await PrimeFreshMarketDataAsync(harness, "corr-demo-runtime-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-demo-runtime",
            context: "Open runtime demo execution",
            correlationId: "corr-demo-runtime-2");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-demo-runtime",
                strategyKey: "demo-runtime",
                exchangeAccountId: exchangeAccountId,
                isDemo: true),
            CancellationToken.None);

        var traces = await harness.DbContext.ExecutionTraces
            .Where(entity => entity.ExecutionOrderId == result.Order.ExecutionOrderId)
            .OrderBy(entity => entity.CreatedAtUtc)
            .ToListAsync();

        Assert.False(result.IsDuplicate);
        Assert.Equal(ExecutionEnvironment.Demo, result.Order.ExecutionEnvironment);
        Assert.Equal(ExecutionOrderExecutorKind.Binance, result.Order.ExecutorKind);
        Assert.Equal(exchangeAccountId, result.Order.ExchangeAccountId);
        Assert.Equal(ExecutionOrderState.Submitted, result.Order.State);
        Assert.Null(result.Order.FailureCode);
        Assert.Null(result.Order.FailureDetail);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Empty(traces);
    }

    [Fact]
    public async Task DispatchAsync_RoutesExplicitBinanceTestnetEnvironment_ToBinanceExecutor()
    {
        await using var harness = CreateHarness(
            environmentName: Environments.Development,
            enableRiskPolicyEvaluator: true,
            testnetExecutionOptions: new BinanceFuturesTestnetOptions
            {
                BaseUrl = "https://testnet.binance.example/futures-rest",
                ApiKey = "testnet-api-key",
                ApiSecret = "testnet-api-secret"
            });
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        await SeedExchangeAccountAsync(harness.DbContext, "user-testnet", exchangeAccountId);
        await SeedBotAsync(harness.DbContext, "user-testnet", botId, "pilot-testnet");
        await SeedPilotSafetyPrerequisitesAsync(
            harness.DbContext,
            "user-testnet",
            exchangeAccountId,
            harness.TimeProvider.GetUtcNow().UtcDateTime);
        harness.PilotOptions.PilotActivationEnabled = true;
        harness.PilotOptions.AllowedUserIds = ["user-testnet"];
        harness.PilotOptions.AllowedBotIds = [botId.ToString("N")];
        harness.PilotOptions.AllowedSymbols = ["BTCUSDT"];
        await PrimeFreshMarketDataAsync(harness, "corr-testnet-engine-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-testnet",
            context: "Open testnet execution",
            correlationId: "corr-testnet-engine-2");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-testnet",
                strategyKey: "pilot-testnet",
                isDemo: null,
                botId: botId,
                exchangeAccountId: exchangeAccountId) with
            {
                Actor = "system:bot-worker",
                RequestedEnvironment = ExecutionEnvironment.BinanceTestnet,
                Context = "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                Quantity = 0.002m,
                Price = 65000m
            },
            CancellationToken.None);

        Assert.Equal(ExecutionEnvironment.BinanceTestnet, result.Order.ExecutionEnvironment);
        Assert.Equal(ExecutionOrderExecutorKind.BinanceTestnet, result.Order.ExecutorKind);
        Assert.Equal(ExecutionOrderState.Submitted, result.Order.State);
        Assert.True(result.Order.SubmittedToBroker);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(0, harness.SpotPrivateRestClient.PlaceOrderCalls);
        Assert.Equal(1, harness.CredentialService.AccessCalls);
        Assert.Equal("api-key", harness.PrivateRestClient.LastPlacementRequest?.ApiKey);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenBinanceTestnetCredentialsAreMissing()
    {
        await using var harness = CreateHarness(
            environmentName: Environments.Development,
            enableRiskPolicyEvaluator: true,
            credentialService: new FakeExchangeCredentialService
            {
                AccessException = new InvalidOperationException("User-scoped credential missing.")
            },
            testnetExecutionOptions: new BinanceFuturesTestnetOptions
            {
                BaseUrl = "https://testnet.binance.example/futures-rest",
                ApiKey = "present-only-key"
            });
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        await SeedExchangeAccountAsync(harness.DbContext, "user-testnet-missing-creds", exchangeAccountId);
        await SeedBotAsync(harness.DbContext, "user-testnet-missing-creds", botId, "pilot-testnet-missing-creds");
        await SeedPilotSafetyPrerequisitesAsync(
            harness.DbContext,
            "user-testnet-missing-creds",
            exchangeAccountId,
            harness.TimeProvider.GetUtcNow().UtcDateTime);
        harness.PilotOptions.PilotActivationEnabled = true;
        harness.PilotOptions.AllowedUserIds = ["user-testnet-missing-creds"];
        harness.PilotOptions.AllowedBotIds = [botId.ToString("N")];
        harness.PilotOptions.AllowedSymbols = ["BTCUSDT"];
        await PrimeFreshMarketDataAsync(harness, "corr-testnet-engine-missing-creds-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-testnet-missing-creds",
            context: "Open testnet execution",
            correlationId: "corr-testnet-engine-missing-creds-2");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-testnet-missing-creds",
                strategyKey: "pilot-testnet-missing-creds",
                isDemo: null,
                botId: botId,
                exchangeAccountId: exchangeAccountId) with
            {
                Actor = "system:bot-worker",
                RequestedEnvironment = ExecutionEnvironment.BinanceTestnet,
                Context = "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                Quantity = 0.002m,
                Price = 65000m
            },
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Rejected, result.Order.State);
        Assert.Equal("BinanceTestnetUserCredentialUnavailable", result.Order.FailureCode);
        Assert.False(result.Order.SubmittedToBroker);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(1, harness.CredentialService.AccessCalls);
        Assert.DoesNotContain("present-only-key", result.Order.FailureDetail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosedWithoutFillOrPosition_WhenDemoReserveBalanceIsUnavailable()
    {
        await using var harness = CreateHarness();
        var botId = Guid.NewGuid();
        await SeedBotAsync(harness.DbContext, "user-demo-reserve-fail", botId, "demo-reserve-fail");
        await SeedDemoWalletAsync(harness.DbContext, "user-demo-reserve-fail", "USDT", 50m);
        harness.MarketDataService.SetLatestPrice("AAVEUSDT", 100m, harness.TimeProvider.GetUtcNow().UtcDateTime, "unit-test");
        harness.MarketDataService.SetSymbolMetadata("AAVEUSDT", "AAVE", "USDT", 0.01m, 0.001m);
        await PrimeFreshMarketDataAsync(harness, "corr-demo-reserve-fail", "AAVEUSDT", "1m");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-demo-reserve-fail",
            context: "Open demo execution",
            correlationId: "corr-demo-reserve-fail-switch");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-demo-reserve-fail",
                strategyKey: "demo-reserve-fail",
                botId: botId,
                isDemo: true) with
            {
                Symbol = "AAVEUSDT",
                BaseAsset = "AAVE",
                QuoteAsset = "USDT",
                Quantity = 1m,
                Price = 100m
            },
            CancellationToken.None);

        var order = await harness.DbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.Id == result.Order.ExecutionOrderId);
        var bot = await harness.DbContext.TradingBots
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.Id == botId);

        Assert.Equal(ExecutionOrderState.Failed, order.State);
        Assert.Equal(0m, order.FilledQuantity);
        Assert.Null(order.LastFilledAtUtc);
        Assert.Equal(0, bot.OpenPositionCount);
        Assert.Equal(0, bot.OpenOrderCount);
        Assert.Empty(await harness.DbContext.DemoPositions.Where(entity => entity.BotId == botId && entity.Quantity != 0m).ToListAsync());
    }

    [Fact]
    public async Task DispatchAsync_ReservesBalanceAndKeepsDemoLimitOrderSubmitted_WhenPriceHasNotCrossedLimit()
    {
        await using var harness = CreateHarness();
        await SeedDemoWalletAsync(harness.DbContext, "user-limit", "USDT", 1000m);
        harness.MarketDataService.SetLatestPrice("AAVEUSDT", 105m, harness.TimeProvider.GetUtcNow().UtcDateTime, "unit-test");
        harness.MarketDataService.SetSymbolMetadata("AAVEUSDT", "AAVE", "USDT", 0.01m, 0.001m);
        await PrimeFreshMarketDataAsync(harness, "corr-limit-1", "AAVEUSDT", "1m");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-limit",
            context: "Open demo execution",
            correlationId: "corr-limit-2");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-limit",
                strategyKey: "demo-limit",
                isDemo: true) with
            {
                Symbol = "AAVEUSDT",
                BaseAsset = "AAVE",
                QuoteAsset = "USDT",
                Quantity = 1m,
                Price = 100m,
                OrderType = ExecutionOrderType.Limit
            },
            CancellationToken.None);

        var usdtWallet = await harness.DbContext.DemoWallets.SingleAsync(entity => entity.OwnerUserId == "user-limit" && entity.Asset == "USDT");
        var transaction = await harness.DbContext.DemoLedgerTransactions.SingleAsync(
            entity => entity.OwnerUserId == "user-limit" &&
                      entity.TransactionType == DemoLedgerTransactionType.FundsReserved);

        Assert.Equal(ExecutionOrderState.Submitted, result.Order.State);
        Assert.Equal(0m, result.Order.FilledQuantity);
        Assert.Equal(899.92m, usdtWallet.AvailableBalance);
        Assert.Equal(100.08m, usdtWallet.ReservedBalance);
        Assert.Equal(DemoLedgerTransactionType.FundsReserved, transaction.TransactionType);
        Assert.Equal(
            [
                ExecutionOrderState.Received,
                ExecutionOrderState.GatePassed,
                ExecutionOrderState.Dispatching,
                ExecutionOrderState.Submitted
            ],
            result.Order.Transitions.Select(transition => transition.State).ToArray());
    }

    [Fact]
    public async Task DispatchAsync_RoutesToBinanceExecutor_WhenResolvedModeIsLive()
    {
        await using var harness = CreateHarness();
        var strategyId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        await SeedUserAsync(harness.DbContext, "user-live");
        await SeedLiveStrategyAsync(harness.DbContext, "user-live", strategyId, "live-core");
        await SeedExchangeAccountAsync(harness.DbContext, "user-live", exchangeAccountId);
        await PrimeFreshMarketDataAsync(harness, "corr-live-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-live",
            context: "Open live execution",
            correlationId: "corr-live-2");
        await harness.SwitchService.SetDemoModeAsync(
            isEnabled: false,
            actor: "admin-live",
            liveApproval: new TradingModeLiveApproval("live-approval-1"),
            context: "Switch to live",
            correlationId: "corr-live-3");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-live",
                strategyId: strategyId,
                strategyKey: "live-core",
                exchangeAccountId: exchangeAccountId,
                isDemo: null),
            CancellationToken.None);

        Assert.False(result.IsDuplicate);
        Assert.Equal(ExecutionEnvironment.Live, result.Order.ExecutionEnvironment);
        Assert.Equal(ExecutionOrderExecutorKind.Binance, result.Order.ExecutorKind);
        Assert.Equal(ExecutionOrderState.Submitted, result.Order.State);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(1, harness.CredentialService.AccessCalls);
        Assert.Equal("binance-order-1", result.Order.ExternalOrderId);
    }

    [Fact]
    public async Task DispatchAsync_RoutesToSpotExecutor_WhenSpotPlaneIsRequested()
    {
        await using var harness = CreateHarness();
        var strategyId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        await SeedUserAsync(harness.DbContext, "user-live-spot");
        await SeedLiveStrategyAsync(harness.DbContext, "user-live-spot", strategyId, "live-spot-core");
        await SeedExchangeAccountAsync(harness.DbContext, "user-live-spot", exchangeAccountId);
        await SeedSpotValidationAsync(harness.DbContext, "user-live-spot", exchangeAccountId);
        await SeedSpotBalanceAsync(harness.DbContext, "user-live-spot", exchangeAccountId, "USDT", 1000m, 50m);
        await SeedSpotBalanceAsync(harness.DbContext, "user-live-spot", exchangeAccountId, "BTC", 1m, 0.1m);
        await PrimeFreshMarketDataAsync(harness, "corr-live-spot-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-live-spot",
            context: "Open live execution",
            correlationId: "corr-live-spot-2");
        await harness.SwitchService.SetDemoModeAsync(
            isEnabled: false,
            actor: "admin-live-spot",
            liveApproval: new TradingModeLiveApproval("live-approval-spot-1"),
            context: "Switch to live",
            correlationId: "corr-live-spot-3");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-live-spot",
                strategyId: strategyId,
                strategyKey: "live-spot-core",
                exchangeAccountId: exchangeAccountId,
                isDemo: null) with
            {
                Plane = ExchangeDataPlane.Spot,
                Quantity = 0.01m,
                Price = 65000m
            },
            CancellationToken.None);

        Assert.False(result.IsDuplicate);
        Assert.Equal(ExecutionEnvironment.Live, result.Order.ExecutionEnvironment);
        Assert.Equal(ExecutionOrderExecutorKind.Binance, result.Order.ExecutorKind);
        Assert.Equal(ExecutionOrderState.Submitted, result.Order.State);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(1, harness.SpotPrivateRestClient.PlaceOrderCalls);
        Assert.Equal(ExchangeDataPlane.Spot, await harness.DbContext.ExecutionOrders
            .Where(entity => entity.Id == result.Order.ExecutionOrderId)
            .Select(entity => entity.Plane)
            .SingleAsync());
    }

    [Fact]
    public async Task DispatchAsync_RoutesToBinanceExecutor_WhenDevelopmentPilotOverridesDemoMode()
    {
        await using var harness = CreateHarness(environmentName: Environments.Development, enableRiskPolicyEvaluator: true);
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        await SeedExchangeAccountAsync(harness.DbContext, "user-pilot-live", exchangeAccountId);
        await SeedBotAsync(harness.DbContext, "user-pilot-live", botId, "pilot-core");
        await SeedPilotSafetyPrerequisitesAsync(
            harness.DbContext,
            "user-pilot-live",
            exchangeAccountId,
            harness.TimeProvider.GetUtcNow().UtcDateTime);
        harness.PilotOptions.AllowedUserIds = ["user-pilot-live"];
        harness.PilotOptions.AllowedBotIds = [botId.ToString("N")];
        harness.PilotOptions.AllowedSymbols = ["BTCUSDT"];
        harness.MarketDataService.SetLatestPrice("BTCUSDT", 65000m, harness.TimeProvider.GetUtcNow().UtcDateTime, "unit-test");
        harness.MarketDataService.SetSymbolMetadata(
            "BTCUSDT",
            "BTC",
            "USDT",
            0.1m,
            0.001m,
            minQuantity: 0.001m,
            minNotional: 100m,
            pricePrecision: 1,
            quantityPrecision: 3);
        await PrimeFreshMarketDataAsync(harness, "corr-pilot-engine-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-pilot-engine",
            context: "Execution open",
            correlationId: "corr-pilot-engine-2");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-pilot-live",
                strategyKey: "pilot-core",
                isDemo: null,
                botId: botId,
                exchangeAccountId: exchangeAccountId) with
            {
                Actor = "system:bot-worker",
                RequestedEnvironment = ExecutionEnvironment.BinanceTestnet,
                Context = "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                Quantity = 0.002m,
                Price = 65000m
            },
            CancellationToken.None);

        Assert.Equal(ExecutionEnvironment.BinanceTestnet, result.Order.ExecutionEnvironment);
        Assert.Equal(ExecutionOrderExecutorKind.BinanceTestnet, result.Order.ExecutorKind);
        Assert.Equal(ExecutionOrderState.Submitted, result.Order.State);
        var persistedOrder = await harness.DbContext.ExecutionOrders.SingleAsync(entity => entity.Id == result.Order.ExecutionOrderId);

        Assert.Equal(ExchangeDataPlane.Futures, persistedOrder.Plane);
        Assert.Equal(1, harness.PrivateRestClient.EnsureMarginTypeCalls);
        Assert.Equal(1, harness.PrivateRestClient.EnsureLeverageCalls);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(0, harness.SpotPrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenPilotUserAndBotScopeAreMissing()
    {
        await using var harness = CreateHarness(environmentName: Environments.Development, enableRiskPolicyEvaluator: true);
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        await SeedExchangeAccountAsync(harness.DbContext, "user-pilot-open-scope", exchangeAccountId);
        await SeedBotAsync(harness.DbContext, "user-pilot-open-scope", botId, "pilot-core");
        await SeedPilotSafetyPrerequisitesAsync(
            harness.DbContext,
            "user-pilot-open-scope",
            exchangeAccountId,
            harness.TimeProvider.GetUtcNow().UtcDateTime);
        harness.PilotOptions.AllowedUserIds = [];
        harness.PilotOptions.AllowedBotIds = [];
        harness.PilotOptions.AllowedSymbols = ["BTCUSDT"];
        harness.MarketDataService.SetLatestPrice("BTCUSDT", 65000m, harness.TimeProvider.GetUtcNow().UtcDateTime, "unit-test");
        harness.MarketDataService.SetSymbolMetadata(
            "BTCUSDT",
            "BTC",
            "USDT",
            0.1m,
            0.001m,
            minQuantity: 0.001m,
            minNotional: 100m,
            pricePrecision: 1,
            quantityPrecision: 3);
        await PrimeFreshMarketDataAsync(harness, "corr-pilot-open-scope-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-pilot-open-scope",
            context: "Execution open",
            correlationId: "corr-pilot-open-scope-2");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-pilot-open-scope",
                strategyKey: "pilot-core",
                isDemo: false,
                botId: botId,
                exchangeAccountId: exchangeAccountId) with
            {
                Actor = "system:bot-worker",
                Context = "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                Quantity = 0.002m,
                Price = 65000m
            },
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Rejected, result.Order.State);
        Assert.Equal("UserExecutionPilotUserScopeMissing", result.Order.FailureCode);
        Assert.Contains("pilot scope constraints", result.Order.FailureDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ExecutionRejectionStage.PreSubmit, result.Order.RejectionStage);
        Assert.False(result.Order.SubmittedToBroker);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(0, harness.SpotPrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_AllowsPilotShortEntry_WhenBotAllowListUsesDFormatAndCooldownIsZero()
    {
        await using var harness = CreateHarness(environmentName: Environments.Development, enableRiskPolicyEvaluator: true);
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        var strategyId = Guid.NewGuid();
        await SeedExchangeAccountAsync(harness.DbContext, "user-pilot-short-scope", exchangeAccountId);
        await SeedBotAsync(harness.DbContext, "user-pilot-short-scope", botId, "pilot-short-scope");
        var bot = await harness.DbContext.TradingBots.SingleAsync(entity => entity.Id == botId);
        bot.Symbol = "SOLUSDT";
        bot.DirectionMode = TradingBotDirectionMode.ShortOnly;
        await SeedLiveStrategyAsync(harness.DbContext, "user-pilot-short-scope", strategyId, "pilot-short-scope");
        await SeedPilotSafetyPrerequisitesAsync(
            harness.DbContext,
            "user-pilot-short-scope",
            exchangeAccountId,
            harness.TimeProvider.GetUtcNow().UtcDateTime);
        harness.PilotOptions.AllowedUserIds = ["user-pilot-short-scope"];
        harness.PilotOptions.AllowedBotIds = [botId.ToString("D").ToUpperInvariant()];
        harness.PilotOptions.AllowedSymbols = ["SOLUSDT"];
        harness.PilotOptions.PerBotCooldownSeconds = 0;
        harness.PilotOptions.PerSymbolCooldownSeconds = 0;
        harness.MarketDataService.SetLatestPrice("SOLUSDT", 85m, harness.TimeProvider.GetUtcNow().UtcDateTime, "unit-test");
        harness.MarketDataService.SetSymbolMetadata(
            "SOLUSDT",
            "SOL",
            "USDT",
            0.01m,
            0.01m,
            minQuantity: 0.01m,
            minNotional: 5m,
            pricePrecision: 2,
            quantityPrecision: 2);
        await PrimeFreshMarketDataAsync(harness, "corr-pilot-short-scope-1", "SOLUSDT");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-pilot-short-scope",
            context: "Execution open",
            correlationId: "corr-pilot-short-scope-2");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-pilot-short-scope",
                strategyKey: "pilot-short-scope",
                isDemo: null,
                strategyId: strategyId,
                botId: botId,
                exchangeAccountId: exchangeAccountId) with
            {
                Actor = "system:bot-worker",
                RequestedEnvironment = ExecutionEnvironment.BinanceTestnet,
                Context = "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                Symbol = "SOLUSDT",
                BaseAsset = "SOL",
                QuoteAsset = "USDT",
                Side = ExecutionOrderSide.Sell,
                Quantity = 0.06m,
                Price = 85m
            },
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Submitted, result.Order.State);
        Assert.Equal(ExecutionOrderSide.Sell, result.Order.Side);
        Assert.Null(result.Order.FailureCode);
        Assert.True(result.Order.SubmittedToBroker);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(ExecutionOrderSide.Sell, harness.PrivateRestClient.LastPlacementRequest?.Side);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenDevelopmentPilotViolatesFuturesMinNotional()
    {
        await using var harness = CreateHarness(environmentName: Environments.Development, enableRiskPolicyEvaluator: true);
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        await SeedExchangeAccountAsync(harness.DbContext, "user-pilot-invalid", exchangeAccountId);
        await SeedBotAsync(harness.DbContext, "user-pilot-invalid", botId, "pilot-core");
        await SeedPilotSafetyPrerequisitesAsync(
            harness.DbContext,
            "user-pilot-invalid",
            exchangeAccountId,
            harness.TimeProvider.GetUtcNow().UtcDateTime);
        harness.PilotOptions.AllowedUserIds = ["user-pilot-invalid"];
        harness.PilotOptions.AllowedBotIds = [botId.ToString("N")];
        harness.PilotOptions.AllowedSymbols = ["BTCUSDT"];
        harness.MarketDataService.SetLatestPrice("BTCUSDT", 65000m, harness.TimeProvider.GetUtcNow().UtcDateTime, "unit-test");
        harness.MarketDataService.SetSymbolMetadata(
            "BTCUSDT",
            "BTC",
            "USDT",
            0.1m,
            0.001m,
            minQuantity: 0.001m,
            minNotional: 100m,
            pricePrecision: 1,
            quantityPrecision: 4);
        await PrimeFreshMarketDataAsync(harness, "corr-pilot-engine-3");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-pilot-engine",
            context: "Execution open",
            correlationId: "corr-pilot-engine-4");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-pilot-invalid",
                strategyKey: "pilot-core",
                isDemo: null,
                botId: botId,
                exchangeAccountId: exchangeAccountId) with
            {
                Actor = "system:bot-worker",
                RequestedEnvironment = ExecutionEnvironment.BinanceTestnet,
                Context = "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                Quantity = 0.001m,
                Price = 65000m
            },
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Rejected, result.Order.State);
        Assert.Equal("OrderNotionalBelowMinimum", result.Order.FailureCode);
        Assert.Contains("minimum notional", result.Order.FailureDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ExecutionRejectionStage.PreSubmit, result.Order.RejectionStage);
        Assert.False(result.Order.SubmittedToBroker);
        Assert.False(result.Order.RetryEligible);
        Assert.False(result.Order.CooldownApplied);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(
            [
                ExecutionOrderState.Received,
                ExecutionOrderState.GatePassed,
                ExecutionOrderState.Dispatching,
                ExecutionOrderState.Rejected
            ],
            result.Order.Transitions.Select(transition => transition.State).ToArray());
        Assert.Contains(
            "minimum notional",
            result.Order.Transitions.Last().Detail,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DispatchAsync_SendsSubmittedAlert_WhenLiveOrderIsAccepted()
    {
        await using var harness = CreateHarness(
            environmentName: Environments.Production,
            alertDispatchCoordinator: new RecordingAlertDispatchCoordinator());
        var strategyId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        await SeedUserAsync(harness.DbContext, "user-live-alert");
        await SeedLiveStrategyAsync(harness.DbContext, "user-live-alert", strategyId, "live-alert");
        await SeedExchangeAccountAsync(harness.DbContext, "user-live-alert", exchangeAccountId);
        await PrimeFreshMarketDataAsync(harness, "corr-live-alert-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-live-alert",
            context: "Open live execution",
            correlationId: "corr-live-alert-2");
        await harness.SwitchService.SetDemoModeAsync(
            isEnabled: false,
            actor: "admin-live-alert",
            liveApproval: new TradingModeLiveApproval("live-approval-alert"),
            context: "Switch to live",
            correlationId: "corr-live-alert-3");
        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-live-alert",
                strategyId: strategyId,
                strategyKey: "live-alert",
                exchangeAccountId: exchangeAccountId,
                isDemo: null),
            CancellationToken.None);

        var alert = Assert.Single(harness.AlertCoordinator.Notifications, notification => notification.Code == "ORDER_SUBMITTED");
        Assert.Equal(ExecutionOrderState.Submitted, result.Order.State);
        Assert.Equal("ORDER_SUBMITTED", alert.Code);
        Assert.Contains("EventType=OrderSubmitted", alert.Message, StringComparison.Ordinal);
        Assert.Contains("Symbol=BTCUSDT", alert.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("api-secret", alert.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DispatchAsync_SendsRejectedAlert_WhenGateBlocksOrder()
    {
        await using var harness = CreateHarness(alertDispatchCoordinator: new RecordingAlertDispatchCoordinator());
        await PrimeFreshMarketDataAsync(harness, "corr-reject-alert-1");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-reject-alert",
                strategyKey: "reject-alert-core",
                isDemo: true),
            CancellationToken.None);

        var alert = Assert.Single(harness.AlertCoordinator.Notifications, notification => notification.Code == "ORDER_REJECTED");
        Assert.Equal(ExecutionOrderState.Rejected, result.Order.State);
        Assert.Equal("ORDER_REJECTED", alert.Code);
        Assert.Contains("EventType=OrderRejected", alert.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ExecutionGateBlockedReason.SwitchConfigurationMissing), alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenDemoModeKeepsLivePathShutEvenIfScopedModeResolvesLive()
    {
        await using var harness = CreateHarness();
        var strategyId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        await SeedUserAsync(harness.DbContext, "user-live-blocked");
        await SeedLiveStrategyAsync(harness.DbContext, "user-live-blocked", strategyId, "live-blocked");
        await SeedExchangeAccountAsync(harness.DbContext, "user-live-blocked", exchangeAccountId);
        await harness.TradingModeService.SetUserTradingModeOverrideAsync(
            "user-live-blocked",
            ExecutionEnvironment.Live,
            actor: "admin-live-blocked",
            liveApproval: new TradingModeLiveApproval("user-override-live-1"),
            context: "User forced to live",
            correlationId: "corr-live-blocked-1");
        await PrimeFreshMarketDataAsync(harness, "corr-live-blocked-2");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-live-blocked",
            context: "Execution open",
            correlationId: "corr-live-blocked-3");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-live-blocked",
                strategyId: strategyId,
                strategyKey: "live-blocked",
                exchangeAccountId: exchangeAccountId,
                isDemo: null),
            CancellationToken.None);

        Assert.False(result.IsDuplicate);
        Assert.Equal(ExecutionEnvironment.Live, result.Order.ExecutionEnvironment);
        Assert.Equal(ExecutionOrderState.Rejected, result.Order.State);
        Assert.Equal(nameof(ExecutionGateBlockedReason.LiveExecutionBlockedByDemoMode), result.Order.FailureCode);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(
            [
                ExecutionOrderState.Received,
                ExecutionOrderState.Rejected
            ],
            result.Order.Transitions.Select(transition => transition.State).ToArray());
    }

    [Fact]
    public async Task DispatchAsync_SuppressesDuplicateCommand_ByIdempotencyKey()
    {
        await using var harness = CreateHarness();
        var strategyId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        await SeedUserAsync(harness.DbContext, "user-dup");
        await SeedLiveStrategyAsync(harness.DbContext, "user-dup", strategyId, "dup-core");
        await SeedExchangeAccountAsync(harness.DbContext, "user-dup", exchangeAccountId);
        await PrimeFreshMarketDataAsync(harness, "corr-dup-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-dup",
            context: "Open live execution",
            correlationId: "corr-dup-2");
        await harness.SwitchService.SetDemoModeAsync(
            isEnabled: false,
            actor: "admin-dup",
            liveApproval: new TradingModeLiveApproval("live-approval-dup"),
            context: "Switch to live",
            correlationId: "corr-dup-3");

        var command = CreateCommand(
            ownerUserId: "user-dup",
            strategyId: strategyId,
            strategyKey: "dup-core",
            exchangeAccountId: exchangeAccountId,
            isDemo: null) with
        {
            IdempotencyKey = "dup-key-1"
        };

        var first = await harness.Engine.DispatchAsync(command, CancellationToken.None);
        var second = await harness.Engine.DispatchAsync(command, CancellationToken.None);

        Assert.False(first.IsDuplicate);
        Assert.True(second.IsDuplicate);
        Assert.True(second.Order.DuplicateSuppressed);
        Assert.Equal(first.Order.ExecutionOrderId, second.Order.ExecutionOrderId);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(1, await harness.DbContext.ExecutionOrders.CountAsync());
    }

    [Fact]
    public async Task DispatchAsync_SuppressesDuplicateCommand_WhenSharedScannerSignalKeyIsReusedAcrossSources()
    {
        await using var harness = CreateHarness(
            environmentName: Environments.Development,
            enableRiskPolicyEvaluator: true,
            testnetExecutionOptions: new BinanceFuturesTestnetOptions
            {
                BaseUrl = "https://testnet.binance.example/futures-rest",
                ApiKey = "testnet-api-key",
                ApiSecret = "testnet-api-secret"
            });
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        var strategySignalId = Guid.NewGuid();
        await SeedExchangeAccountAsync(harness.DbContext, "user-shared-dup", exchangeAccountId);
        await SeedBotAsync(harness.DbContext, "user-shared-dup", botId, "pilot-shared-dup");
        await SeedPilotSafetyPrerequisitesAsync(
            harness.DbContext,
            "user-shared-dup",
            exchangeAccountId,
            harness.TimeProvider.GetUtcNow().UtcDateTime);
        harness.PilotOptions.PilotActivationEnabled = true;
        harness.PilotOptions.AllowedUserIds = ["user-shared-dup"];
        harness.PilotOptions.AllowedBotIds = [botId.ToString("N")];
        harness.PilotOptions.AllowedSymbols = ["BTCUSDT"];
        await PrimeFreshMarketDataAsync(harness, "corr-shared-dup-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-shared-dup",
            context: "Open testnet execution",
            correlationId: "corr-shared-dup-2");

        var idempotencyKey = $"scanner-handoff:{strategySignalId:N}:{ExecutionEnvironment.BinanceTestnet}:{StrategySignalType.Entry}:{ExecutionOrderSide.Buy}:{false}";
        var scannerCommand = CreateCommand(
            ownerUserId: "user-shared-dup",
            strategyKey: "pilot-shared-dup",
            isDemo: null,
            botId: botId,
            exchangeAccountId: exchangeAccountId) with
        {
            Actor = "system:market-scanner",
            RequestedEnvironment = ExecutionEnvironment.BinanceTestnet,
            StrategySignalId = strategySignalId,
            Context = "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
            IdempotencyKey = idempotencyKey,
            CorrelationId = "corr-shared-root",
            Quantity = 0.002m,
            Price = 65000m
        };

        var botWorkerCommand = scannerCommand with
        {
            Actor = "system:bot-worker"
        };

        var first = await harness.Engine.DispatchAsync(scannerCommand, CancellationToken.None);
        var second = await harness.Engine.DispatchAsync(botWorkerCommand, CancellationToken.None);

        Assert.False(first.IsDuplicate);
        Assert.True(second.IsDuplicate);
        Assert.True(second.Order.DuplicateSuppressed);
        Assert.Equal(first.Order.ExecutionOrderId, second.Order.ExecutionOrderId);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(1, await harness.DbContext.ExecutionOrders.CountAsync());
    }

    [Fact]
    public async Task DispatchAsync_SuppressesDuplicateCommand_ByStrategySignalIntent_WhenPreSubmitRejectedOrderAlreadyExists()
    {
        await using var harness = CreateHarness();
        var strategyId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        var strategySignalId = Guid.NewGuid();
        await SeedUserAsync(harness.DbContext, "user-dup-intent-rejected");
        await SeedLiveStrategyAsync(harness.DbContext, "user-dup-intent-rejected", strategyId, "dup-intent-rejected");
        await SeedExchangeAccountAsync(harness.DbContext, "user-dup-intent-rejected", exchangeAccountId);
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-dup-intent-rejected",
            context: "Open live execution",
            correlationId: "corr-dup-intent-rejected-1");
        await harness.SwitchService.SetDemoModeAsync(
            isEnabled: false,
            actor: "admin-dup-intent-rejected",
            liveApproval: new TradingModeLiveApproval("live-approval-dup-intent-rejected"),
            context: "Switch to live",
            correlationId: "corr-dup-intent-rejected-2");
        await SeedExistingExecutionIntentOrderAsync(
            harness.DbContext,
            ownerUserId: "user-dup-intent-rejected",
            strategyId: strategyId,
            strategySignalId: strategySignalId,
            exchangeAccountId: exchangeAccountId,
            side: ExecutionOrderSide.Sell,
            state: ExecutionOrderState.Rejected,
            submittedToBroker: false,
            failureCode: "UserExecutionPilotBotNotAllowed",
            rejectionStage: ExecutionRejectionStage.PreSubmit);

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-dup-intent-rejected",
                strategyId: strategyId,
                strategyKey: "dup-intent-rejected",
                exchangeAccountId: exchangeAccountId,
                isDemo: null) with
            {
                StrategySignalId = strategySignalId,
                Side = ExecutionOrderSide.Sell,
                IdempotencyKey = "fresh-dup-intent-rejected"
            },
            CancellationToken.None);

        Assert.True(result.IsDuplicate);
        Assert.True(result.Order.DuplicateSuppressed);
        Assert.Equal(ExecutionOrderState.Rejected, result.Order.State);
        Assert.Equal("UserExecutionPilotBotNotAllowed", result.Order.FailureCode);
        Assert.Equal(ExecutionRejectionStage.PreSubmit, result.Order.RejectionStage);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(1, await harness.DbContext.ExecutionOrders.CountAsync());
    }

    [Fact]
    public async Task DispatchAsync_SuppressesDuplicateCommand_ByStrategySignalIntent_WhenSubmittedOrderAlreadyExists()
    {
        await using var harness = CreateHarness();
        var strategyId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        var strategySignalId = Guid.NewGuid();
        await SeedUserAsync(harness.DbContext, "user-dup-intent-submitted");
        await SeedLiveStrategyAsync(harness.DbContext, "user-dup-intent-submitted", strategyId, "dup-intent-submitted");
        await SeedExchangeAccountAsync(harness.DbContext, "user-dup-intent-submitted", exchangeAccountId);
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-dup-intent-submitted",
            context: "Open live execution",
            correlationId: "corr-dup-intent-submitted-1");
        await harness.SwitchService.SetDemoModeAsync(
            isEnabled: false,
            actor: "admin-dup-intent-submitted",
            liveApproval: new TradingModeLiveApproval("live-approval-dup-intent-submitted"),
            context: "Switch to live",
            correlationId: "corr-dup-intent-submitted-2");
        await SeedExistingExecutionIntentOrderAsync(
            harness.DbContext,
            ownerUserId: "user-dup-intent-submitted",
            strategyId: strategyId,
            strategySignalId: strategySignalId,
            exchangeAccountId: exchangeAccountId,
            side: ExecutionOrderSide.Sell,
            state: ExecutionOrderState.Submitted,
            submittedToBroker: true,
            failureCode: null,
            rejectionStage: ExecutionRejectionStage.None);

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-dup-intent-submitted",
                strategyId: strategyId,
                strategyKey: "dup-intent-submitted",
                exchangeAccountId: exchangeAccountId,
                isDemo: null) with
            {
                StrategySignalId = strategySignalId,
                Side = ExecutionOrderSide.Sell,
                IdempotencyKey = "fresh-dup-intent-submitted"
            },
            CancellationToken.None);

        Assert.True(result.IsDuplicate);
        Assert.True(result.Order.DuplicateSuppressed);
        Assert.Equal(ExecutionOrderState.Submitted, result.Order.State);
        Assert.True(result.Order.SubmittedToBroker);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(1, await harness.DbContext.ExecutionOrders.CountAsync());
    }

    [Fact]
    public async Task DispatchAsync_PersistsRejectedLifecycle_WhenGateBlocksOrder()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-reject-1");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-reject",
                strategyKey: "reject-core",
                isDemo: true),
            CancellationToken.None);

        Assert.False(result.IsDuplicate);
        Assert.Equal(ExecutionOrderState.Rejected, result.Order.State);
        Assert.Equal(nameof(ExecutionGateBlockedReason.SwitchConfigurationMissing), result.Order.FailureCode);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(
            [
                ExecutionOrderState.Received,
                ExecutionOrderState.Rejected
            ],
            result.Order.Transitions.Select(transition => transition.State).ToArray());
    }

    [Fact]
    public async Task DispatchAsync_PersistsProtectiveTargets_AndReplacementLink_WhenProvided()
    {
        await using var harness = CreateHarness();
        var replacementOrderId = Guid.NewGuid();
        await PrimeFreshMarketDataAsync(harness, "corr-protect-1");
        await SeedDemoWalletAsync(harness.DbContext, "user-protect", "USDT", 10000m);
        harness.MarketDataService.SetLatestPrice("BTCUSDT", 65000m, harness.TimeProvider.GetUtcNow().UtcDateTime, "unit-test");
        harness.MarketDataService.SetSymbolMetadata("BTCUSDT", "BTC", "USDT", 0.01m, 0.0001m);
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-protect",
            context: "Open demo execution",
            correlationId: "corr-protect-2");
        await SeedExecutionOrderAsync(
            harness.DbContext,
            "user-protect",
            replacementOrderId,
            ExecutionEnvironment.Live);
        await harness.DemoSessionService.EnsureActiveSessionAsync("user-protect");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-protect",
                strategyKey: "protect-core",
                isDemo: true) with
            {
                StopLossPrice = 64000m,
                TakeProfitPrice = 68000m,
                ReplacesExecutionOrderId = replacementOrderId
            },
            CancellationToken.None);

        Assert.Equal(64000m, result.Order.StopLossPrice);
        Assert.Equal(68000m, result.Order.TakeProfitPrice);
        Assert.True(result.Order.StopLossAttached);
        Assert.True(result.Order.TakeProfitAttached);
        Assert.Equal(replacementOrderId, result.Order.ReplacesExecutionOrderId);
        var terminalTransition = result.Order.Transitions.Last();

        Assert.True(
            result.Order.State is ExecutionOrderState.Filled or ExecutionOrderState.PartiallyFilled,
            $"Expected terminal demo state to be Filled or PartiallyFilled but was {result.Order.State}.");
        Assert.Equal(result.Order.State, terminalTransition.State);
        Assert.Contains(
            "ProtectiveRule=Stop:64000|Take:68000",
            terminalTransition.Detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenProtectiveTargetsAreInvalid()
    {
        await using var harness = CreateHarness();

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-invalid-protect",
                strategyKey: "protect-core",
                isDemo: true) with
            {
                StopLossPrice = 66000m
            },
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Rejected, result.Order.State);
        Assert.Equal("InvalidStopLossConfiguration", result.Order.FailureCode);
        Assert.Equal(ExecutionRejectionStage.PreSubmit, result.Order.RejectionStage);
        Assert.False(result.Order.SubmittedToBroker);
        Assert.False(result.Order.RetryEligible);
        Assert.False(result.Order.CooldownApplied);
        Assert.Equal(1, await harness.DbContext.ExecutionOrders.CountAsync());
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenTakeProfitConfigurationIsInvalid()
    {
        await using var harness = CreateHarness();

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-invalid-tp",
                strategyKey: "protect-core",
                isDemo: true) with
            {
                TakeProfitPrice = 64000m
            },
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Rejected, result.Order.State);
        Assert.Equal("InvalidTakeProfitConfiguration", result.Order.FailureCode);
        Assert.Equal(ExecutionRejectionStage.PreSubmit, result.Order.RejectionStage);
        Assert.False(result.Order.SubmittedToBroker);
        Assert.False(result.Order.RetryEligible);
        Assert.False(result.Order.CooldownApplied);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenProtectiveBracketIsSideMismatched()
    {
        await using var harness = CreateHarness();

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-invalid-bracket",
                strategyKey: "protect-core",
                isDemo: true) with
            {
                StopLossPrice = 68000m,
                TakeProfitPrice = 67000m
            },
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Rejected, result.Order.State);
        Assert.Equal("ProtectiveOrderSideMismatch", result.Order.FailureCode);
        Assert.Equal(ExecutionRejectionStage.PreSubmit, result.Order.RejectionStage);
        Assert.False(result.Order.SubmittedToBroker);
        Assert.False(result.Order.RetryEligible);
        Assert.False(result.Order.CooldownApplied);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenReduceOnlyHasNoOpenPosition()
    {
        await using var harness = CreateHarness();

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-reduce-missing",
                strategyKey: "reduce-core",
                isDemo: true) with
            {
                ReduceOnly = true,
                Side = ExecutionOrderSide.Sell
            },
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Rejected, result.Order.State);
        Assert.True(result.Order.ReduceOnly);
        Assert.Equal("ReduceOnlyWithoutOpenPosition", result.Order.FailureCode);
        Assert.Equal(ExecutionRejectionStage.PreSubmit, result.Order.RejectionStage);
        Assert.False(result.Order.SubmittedToBroker);
        Assert.False(result.Order.RetryEligible);
        Assert.False(result.Order.CooldownApplied);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenReduceOnlyWouldIncreaseExposure()
    {
        await using var harness = CreateHarness();
        await SeedDemoPositionAsync(harness.DbContext, "user-reduce-side", "BTCUSDT", "BTC", "USDT", 0.05m, 65000m);

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-reduce-side",
                strategyKey: "reduce-core",
                isDemo: true) with
            {
                ReduceOnly = true,
                Side = ExecutionOrderSide.Buy,
                Quantity = 0.01m,
                Price = 65000m
            },
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Rejected, result.Order.State);
        Assert.Equal("ReduceOnlyWouldIncreaseExposure", result.Order.FailureCode);
        Assert.Equal(ExecutionRejectionStage.PreSubmit, result.Order.RejectionStage);
        Assert.False(result.Order.SubmittedToBroker);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenReduceOnlyQuantityExceedsOpenPosition()
    {
        await using var harness = CreateHarness();
        await SeedDemoPositionAsync(harness.DbContext, "user-reduce-qty", "BTCUSDT", "BTC", "USDT", 0.05m, 65000m);

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-reduce-qty",
                strategyKey: "reduce-core",
                isDemo: true) with
            {
                ReduceOnly = true,
                Side = ExecutionOrderSide.Sell,
                Quantity = 0.06m,
                Price = 65000m
            },
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Rejected, result.Order.State);
        Assert.Equal("ReduceOnlyQuantityExceedsOpenPosition", result.Order.FailureCode);
        Assert.Equal(ExecutionRejectionStage.PreSubmit, result.Order.RejectionStage);
        Assert.False(result.Order.SubmittedToBroker);
    }

    [Fact]
    public async Task DispatchAsync_AllowsReduceOnlyPartialClose_AndPassesReduceOnlyToBroker()
    {
        await using var harness = CreateHarness();
        var strategyId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        await SeedUserAsync(harness.DbContext, "user-reduce-live");
        await SeedLiveStrategyAsync(harness.DbContext, "user-reduce-live", strategyId, "reduce-live");
        await SeedExchangeAccountAsync(harness.DbContext, "user-reduce-live", exchangeAccountId);
        await SeedExchangePositionAsync(harness.DbContext, "user-reduce-live", exchangeAccountId, "BTCUSDT", "LONG", 0.05m, 65000m);
        await PrimeFreshMarketDataAsync(harness, "corr-reduce-live-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-reduce-live",
            context: "Open live reduce-only execution",
            correlationId: "corr-reduce-live-2");
        await harness.SwitchService.SetDemoModeAsync(
            isEnabled: false,
            actor: "admin-reduce-live",
            liveApproval: new TradingModeLiveApproval("reduce-live-approval"),
            context: "Switch to live reduce-only",
            correlationId: "corr-reduce-live-3");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-reduce-live",
                strategyId: strategyId,
                strategyKey: "reduce-live",
                exchangeAccountId: exchangeAccountId,
                isDemo: null) with
            {
                ReduceOnly = true,
                Side = ExecutionOrderSide.Sell,
                Quantity = 0.02m,
                Price = 65000m
            },
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Submitted, result.Order.State);
        Assert.True(result.Order.ReduceOnly);
        Assert.True(result.Order.SubmittedToBroker);
        Assert.Equal(ExecutionRejectionStage.None, result.Order.RejectionStage);
        Assert.True(result.Order.CooldownApplied);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.NotNull(harness.PrivateRestClient.LastPlacementRequest);
        Assert.True(harness.PrivateRestClient.LastPlacementRequest.ReduceOnly);
    }

    [Fact]
    public async Task DispatchAsync_AllowsPilotReduceOnlyClose_WhenCooldownConfigurationIsZero()
    {
        await using var harness = CreateHarness(environmentName: Environments.Development);
        var strategyId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        harness.PilotOptions.PerBotCooldownSeconds = 0;
        harness.PilotOptions.PerSymbolCooldownSeconds = 0;
        harness.PilotOptions.AllowedUserIds = ["user-reduce-live-pilot-zero-cooldown"];
        harness.PilotOptions.AllowedBotIds = [botId.ToString("N")];
        await SeedUserAsync(harness.DbContext, "user-reduce-live-pilot-zero-cooldown");
        await SeedBotAsync(harness.DbContext, "user-reduce-live-pilot-zero-cooldown", botId, "reduce-live-pilot-zero-cooldown");
        await SeedLiveStrategyAsync(harness.DbContext, "user-reduce-live-pilot-zero-cooldown", strategyId, "reduce-live-pilot-zero-cooldown");
        await SeedExchangeAccountAsync(harness.DbContext, "user-reduce-live-pilot-zero-cooldown", exchangeAccountId);
        await SeedPilotSafetyPrerequisitesAsync(
            harness.DbContext,
            "user-reduce-live-pilot-zero-cooldown",
            exchangeAccountId,
            harness.TimeProvider.GetUtcNow().UtcDateTime);
        await SeedExchangePositionAsync(
            harness.DbContext,
            "user-reduce-live-pilot-zero-cooldown",
            exchangeAccountId,
            "BTCUSDT",
            "LONG",
            0.05m,
            65000m);
        await PrimeFreshMarketDataAsync(harness, "corr-reduce-live-pilot-zero-cooldown-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-reduce-live-pilot-zero-cooldown",
            context: "Open live reduce-only pilot execution",
            correlationId: "corr-reduce-live-pilot-zero-cooldown-2");
        await harness.SwitchService.SetDemoModeAsync(
            isEnabled: false,
            actor: "admin-reduce-live-pilot-zero-cooldown",
            liveApproval: new TradingModeLiveApproval("reduce-live-pilot-zero-cooldown-approval"),
            context: "Switch to live reduce-only pilot",
            correlationId: "corr-reduce-live-pilot-zero-cooldown-3");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-reduce-live-pilot-zero-cooldown",
                strategyId: strategyId,
                strategyKey: "reduce-live-pilot-zero-cooldown",
                botId: botId,
                exchangeAccountId: exchangeAccountId,
                isDemo: null) with
            {
                Actor = "system:bot-worker",
                RequestedEnvironment = ExecutionEnvironment.BinanceTestnet,
                Context = "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                ReduceOnly = true,
                Side = ExecutionOrderSide.Sell,
                Quantity = 0.02m,
                Price = 65000m
            },
            CancellationToken.None);

        Assert.True(
            result.Order.State == ExecutionOrderState.Submitted,
            $"Expected Submitted but was {result.Order.State}. FailureCode={result.Order.FailureCode}; FailureDetail={result.Order.FailureDetail}");
        Assert.True(result.Order.ReduceOnly);
        Assert.True(result.Order.SubmittedToBroker);
        Assert.Equal(ExecutionRejectionStage.None, result.Order.RejectionStage);
        Assert.NotEqual("UserExecutionPilotCooldownConfigurationInvalid", result.Order.FailureCode);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.NotNull(harness.PrivateRestClient.LastPlacementRequest);
        Assert.True(harness.PrivateRestClient.LastPlacementRequest.ReduceOnly);
    }

    [Fact]
    public async Task DispatchAsync_RejectsReduceOnlyClose_WhenOnlyUnfilledSubmittedMarketOrderExists()
    {
        await using var harness = CreateHarness();
        var strategyId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        await SeedUserAsync(harness.DbContext, "user-reduce-live-fallback");
        await SeedLiveStrategyAsync(harness.DbContext, "user-reduce-live-fallback", strategyId, "reduce-live-fallback");
        await SeedExchangeAccountAsync(harness.DbContext, "user-reduce-live-fallback", exchangeAccountId);
        await SeedSubmittedLiveExecutionOrderAsync(harness.DbContext, "user-reduce-live-fallback", exchangeAccountId, "BTCUSDT", 0.05m, ExecutionOrderSide.Buy, reduceOnly: false);
        await PrimeFreshMarketDataAsync(harness, "corr-reduce-live-fallback-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-reduce-live-fallback",
            context: "Open live reduce-only execution",
            correlationId: "corr-reduce-live-fallback-2");
        await harness.SwitchService.SetDemoModeAsync(
            isEnabled: false,
            actor: "admin-reduce-live-fallback",
            liveApproval: new TradingModeLiveApproval("reduce-live-fallback-approval"),
            context: "Switch to live reduce-only",
            correlationId: "corr-reduce-live-fallback-3");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-reduce-live-fallback",
                strategyId: strategyId,
                strategyKey: "reduce-live-fallback",
                exchangeAccountId: exchangeAccountId,
                isDemo: null) with
            {
                ReduceOnly = true,
                Side = ExecutionOrderSide.Sell,
                Quantity = 0.02m,
                Price = 65000m
            },
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Rejected, result.Order.State);
        Assert.Equal("ReduceOnlyWithoutOpenPosition", result.Order.FailureCode);
        Assert.True(result.Order.ReduceOnly);
        Assert.False(result.Order.SubmittedToBroker);
        Assert.Equal(ExecutionRejectionStage.PreSubmit, result.Order.RejectionStage);
        Assert.False(result.Order.CooldownApplied);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Null(harness.PrivateRestClient.LastPlacementRequest);
    }

    [Fact]
    public async Task DispatchAsync_AllowsReduceOnlyPartialClose_WhenAnotherAccountOffsetsOwnerLevelNetQuantity()
    {
        await using var harness = CreateHarness();
        var strategyId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        await SeedUserAsync(harness.DbContext, "user-reduce-live-account-scope");
        await SeedLiveStrategyAsync(harness.DbContext, "user-reduce-live-account-scope", strategyId, "reduce-live-account-scope");
        await SeedExchangeAccountAsync(harness.DbContext, "user-reduce-live-account-scope", exchangeAccountId);
        await SeedExchangePositionAsync(harness.DbContext, "user-reduce-live-account-scope", exchangeAccountId, "BTCUSDT", "LONG", 0.05m, 65000m);
        await SeedExchangePositionAsync(harness.DbContext, "user-reduce-live-account-scope", Guid.NewGuid(), "BTCUSDT", "SHORT", 0.05m, 65100m);
        await PrimeFreshMarketDataAsync(harness, "corr-reduce-live-account-scope-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-reduce-live-account-scope",
            context: "Open live reduce-only execution",
            correlationId: "corr-reduce-live-account-scope-2");
        await harness.SwitchService.SetDemoModeAsync(
            isEnabled: false,
            actor: "admin-reduce-live-account-scope",
            liveApproval: new TradingModeLiveApproval("reduce-live-account-scope-approval"),
            context: "Switch to live reduce-only",
            correlationId: "corr-reduce-live-account-scope-3");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-reduce-live-account-scope",
                strategyId: strategyId,
                strategyKey: "reduce-live-account-scope",
                exchangeAccountId: exchangeAccountId,
                isDemo: null) with
            {
                ReduceOnly = true,
                Side = ExecutionOrderSide.Sell,
                Quantity = 0.02m,
                Price = 65000m
            },
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Submitted, result.Order.State);
        Assert.True(result.Order.ReduceOnly);
        Assert.True(result.Order.SubmittedToBroker);
        Assert.Equal(ExecutionRejectionStage.None, result.Order.RejectionStage);
        Assert.Equal(exchangeAccountId, result.Order.ExchangeAccountId);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.NotNull(harness.PrivateRestClient.LastPlacementRequest);
        Assert.True(harness.PrivateRestClient.LastPlacementRequest.ReduceOnly);
    }

    [Fact]
    public async Task DispatchAsync_FailsPostSubmit_WithRetryEligibleAndCooldownApplied_WhenBrokerRequestFails()
    {
        await using var harness = CreateHarness(environmentName: Environments.Production);
        var strategyId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        await SeedUserAsync(harness.DbContext, "user-post-submit");
        await SeedLiveStrategyAsync(harness.DbContext, "user-post-submit", strategyId, "post-submit");
        await SeedExchangeAccountAsync(harness.DbContext, "user-post-submit", exchangeAccountId);
        await PrimeFreshMarketDataAsync(harness, "corr-post-submit-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-post-submit",
            context: "Open live execution",
            correlationId: "corr-post-submit-2");
        await harness.SwitchService.SetDemoModeAsync(
            isEnabled: false,
            actor: "admin-post-submit",
            liveApproval: new TradingModeLiveApproval("post-submit-approval"),
            context: "Switch to live",
            correlationId: "corr-post-submit-3");
        harness.PrivateRestClient.PlaceOrderException = new InvalidOperationException("Broker request timed out.");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-post-submit",
                strategyId: strategyId,
                strategyKey: "post-submit",
                exchangeAccountId: exchangeAccountId,
                isDemo: null),
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Rejected, result.Order.State);
        Assert.Equal("PreSubmitFailed", result.Order.FailureCode);
        Assert.Equal(ExecutionRejectionStage.PreSubmit, result.Order.RejectionStage);
        Assert.False(result.Order.SubmittedToBroker);
        Assert.False(result.Order.RetryEligible);
        Assert.False(result.Order.CooldownApplied);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Null(result.Order.ClientOrderId);
    }
    [Fact]
    public async Task DispatchAsync_PersistsStableFailureCode_WhenBrokerRejectsWithExchangeMarginReason()
    {
        await using var harness = CreateHarness(environmentName: Environments.Production);
        var strategyId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        await SeedUserAsync(harness.DbContext, "user-margin-reject");
        await SeedLiveStrategyAsync(harness.DbContext, "user-margin-reject", strategyId, "margin-reject");
        await SeedExchangeAccountAsync(harness.DbContext, "user-margin-reject", exchangeAccountId);
        await PrimeFreshMarketDataAsync(harness, "corr-margin-reject-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-margin-reject",
            context: "Open live execution",
            correlationId: "corr-margin-reject-2");
        await harness.SwitchService.SetDemoModeAsync(
            isEnabled: false,
            actor: "admin-margin-reject",
            liveApproval: new TradingModeLiveApproval("margin-reject-approval"),
            context: "Switch to live",
            correlationId: "corr-margin-reject-3");
        harness.PrivateRestClient.PlaceOrderException = new BinanceExchangeRejectedException(
            "FuturesMarginInsufficient",
            "Binance futures order rejected with exchange code -2019 (Margin is insufficient.).",
            "-2019",
            400);

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-margin-reject",
                strategyId: strategyId,
                strategyKey: "margin-reject",
                exchangeAccountId: exchangeAccountId,
                isDemo: null),
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Rejected, result.Order.State);
        Assert.Equal("FuturesMarginInsufficient", result.Order.FailureCode);
        Assert.Contains("-2019", result.Order.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(ExecutionRejectionStage.PreSubmit, result.Order.RejectionStage);
        Assert.False(result.Order.SubmittedToBroker);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_PersistsStableFailureCode_WhenLeverageConfigurationFails()
    {
        await using var harness = CreateHarness(
            environmentName: Environments.Development,
            enableRiskPolicyEvaluator: true,
            testnetExecutionOptions: new BinanceFuturesTestnetOptions
            {
                BaseUrl = "https://testnet.binance.example/futures-rest",
                ApiKey = "testnet-api-key",
                ApiSecret = "testnet-api-secret"
            });
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        await SeedExchangeAccountAsync(harness.DbContext, "user-lev-fail", exchangeAccountId);
        await SeedBotAsync(harness.DbContext, "user-lev-fail", botId, "pilot-lev-fail");
        await SeedPilotSafetyPrerequisitesAsync(
            harness.DbContext,
            "user-lev-fail",
            exchangeAccountId,
            harness.TimeProvider.GetUtcNow().UtcDateTime);
        harness.PilotOptions.PilotActivationEnabled = true;
        harness.PilotOptions.AllowedUserIds = ["user-lev-fail"];
        harness.PilotOptions.AllowedBotIds = [botId.ToString("N")];
        harness.PilotOptions.AllowedSymbols = ["BTCUSDT"];
        harness.PrivateRestClient.EnsureLeverageException = new BinanceExchangeRejectedException(
            "BinanceLeverageConfigurationFailed",
            "Binance futures leverage request failed for BTCUSDT with requested leverage 1 and HTTP status 400.",
            "-4003",
            400);
        await PrimeFreshMarketDataAsync(harness, "corr-lev-fail-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-lev-fail",
            context: "Open leverage failure execution",
            correlationId: "corr-lev-fail-2");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-lev-fail",
                strategyKey: "pilot-lev-fail",
                isDemo: null,
                botId: botId,
                exchangeAccountId: exchangeAccountId) with
            {
                Actor = "system:bot-worker",
                RequestedEnvironment = ExecutionEnvironment.BinanceTestnet,
                Context = "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1 | RequestedLeverage=1 | EffectiveLeverage=1 | MaxAllowedLeverage=1 | LeverageSource=Default | LeveragePolicyDecision=Allowed | LeveragePolicyReason=LeveragePolicyAllowed | LeverageAlignmentSkippedForReduceOnly=False",
                Quantity = 0.002m,
                Price = 65000m
            },
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Rejected, result.Order.State);
        Assert.Equal("BinanceLeverageConfigurationFailed", result.Order.FailureCode);
        Assert.Equal(ExecutionRejectionStage.PreSubmit, result.Order.RejectionStage);
        Assert.False(result.Order.SubmittedToBroker);
        Assert.Equal(1, harness.PrivateRestClient.EnsureLeverageCalls);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_PersistsStableFailureCode_WhenPilotExecutionSymbolIsOutsideAllowlist()
    {
        await using var harness = CreateHarness(
            environmentName: Environments.Development,
            enableRiskPolicyEvaluator: true,
            testnetExecutionOptions: new BinanceFuturesTestnetOptions
            {
                BaseUrl = "https://testnet.binance.example/futures-rest",
                ApiKey = "testnet-api-key",
                ApiSecret = "testnet-api-secret"
            });
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        await SeedExchangeAccountAsync(harness.DbContext, "user-symbol-allowlist-block", exchangeAccountId);
        await SeedBotAsync(harness.DbContext, "user-symbol-allowlist-block", botId, "pilot-symbol-allowlist-block");
        await SeedPilotSafetyPrerequisitesAsync(
            harness.DbContext,
            "user-symbol-allowlist-block",
            exchangeAccountId,
            harness.TimeProvider.GetUtcNow().UtcDateTime);
        harness.PilotOptions.PilotActivationEnabled = true;
        harness.PilotOptions.AllowedUserIds = ["user-symbol-allowlist-block"];
        harness.PilotOptions.AllowedBotIds = [botId.ToString("N")];
        harness.PilotOptions.AllowedSymbols = ["BTCUSDT"];
        harness.PilotOptions.AllowedExecutionSymbols = ["SOLUSDT"];
        await PrimeFreshMarketDataAsync(harness, "corr-symbol-allowlist-block-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-symbol-allowlist-block",
            context: "Open symbol allowlist blocked execution",
            correlationId: "corr-symbol-allowlist-block-2");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-symbol-allowlist-block",
                strategyKey: "pilot-symbol-allowlist-block",
                isDemo: null,
                botId: botId,
                exchangeAccountId: exchangeAccountId) with
            {
                Actor = "system:bot-worker",
                RequestedEnvironment = ExecutionEnvironment.BinanceTestnet,
                Context = "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                Quantity = 0.002m,
                Price = 65000m
            },
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Rejected, result.Order.State);
        Assert.Equal("SymbolExecutionNotAllowed", result.Order.FailureCode);
        Assert.Equal(ExecutionRejectionStage.PreSubmit, result.Order.RejectionStage);
        Assert.False(result.Order.SubmittedToBroker);
        Assert.Equal(0, harness.PrivateRestClient.EnsureLeverageCalls);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_PersistsStableFailureCode_WhenLeverageChangeIsBlockedByOpenPosition()
    {
        await using var harness = CreateHarness(
            environmentName: Environments.Development,
            enableRiskPolicyEvaluator: true,
            testnetExecutionOptions: new BinanceFuturesTestnetOptions
            {
                BaseUrl = "https://testnet.binance.example/futures-rest",
                ApiKey = "testnet-api-key",
                ApiSecret = "testnet-api-secret"
            });
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        await SeedExchangeAccountAsync(harness.DbContext, "user-lev-open-pos", exchangeAccountId);
        await SeedBotAsync(harness.DbContext, "user-lev-open-pos", botId, "pilot-lev-open-pos");
        await SeedPilotSafetyPrerequisitesAsync(
            harness.DbContext,
            "user-lev-open-pos",
            exchangeAccountId,
            harness.TimeProvider.GetUtcNow().UtcDateTime);
        harness.PilotOptions.PilotActivationEnabled = true;
        harness.PilotOptions.AllowedUserIds = ["user-lev-open-pos"];
        harness.PilotOptions.AllowedBotIds = [botId.ToString("N")];
        harness.PilotOptions.AllowedSymbols = ["BTCUSDT"];
        harness.PrivateRestClient.EnsureLeverageException = new BinanceExchangeRejectedException(
            "BinanceLeverageChangeBlockedOpenPosition",
            "Binance futures leverage request failed for BTCUSDT with requested leverage 1 and HTTP status 400 (exchange code -4161: leverage change blocked by open position).",
            "-4161",
            400);
        await PrimeFreshMarketDataAsync(harness, "corr-lev-open-pos-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-lev-open-pos",
            context: "Open leverage failure execution",
            correlationId: "corr-lev-open-pos-2");

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-lev-open-pos",
                strategyKey: "pilot-lev-open-pos",
                isDemo: null,
                botId: botId,
                exchangeAccountId: exchangeAccountId) with
            {
                Actor = "system:bot-worker",
                RequestedEnvironment = ExecutionEnvironment.BinanceTestnet,
                Context = "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1 | RequestedLeverage=1 | EffectiveLeverage=1 | MaxAllowedLeverage=1 | LeverageSource=Default | LeveragePolicyDecision=Allowed | LeveragePolicyReason=LeveragePolicyAllowed | LeverageAlignmentSkippedForReduceOnly=False",
                Quantity = 0.002m,
                Price = 65000m
            },
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Rejected, result.Order.State);
        Assert.Equal("BinanceLeverageChangeBlockedOpenPosition", result.Order.FailureCode);
        Assert.Equal(ExecutionRejectionStage.PreSubmit, result.Order.RejectionStage);
        Assert.False(result.Order.SubmittedToBroker);
        Assert.Equal(1, harness.PrivateRestClient.EnsureLeverageCalls);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_UsesScopedCorrelationId_WhenCommandOmitsExplicitCorrelation()
    {
        await using var harness = CreateHarness();
        await SeedDemoWalletAsync(harness.DbContext, "user-corr", "USDT", 1000m);
        harness.MarketDataService.SetLatestPrice("BTCUSDT", 65000m, harness.TimeProvider.GetUtcNow().UtcDateTime, "unit-test");
        harness.MarketDataService.SetSymbolMetadata("BTCUSDT", "BTC", "USDT", 0.01m, 0.0001m);
        await PrimeFreshMarketDataAsync(harness, "corr-scope-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-scope",
            context: "Open demo execution",
            correlationId: "corr-scope-2");
        using var _ = harness.CorrelationContextAccessor.BeginScope(
            new CoinBot.Infrastructure.Observability.CorrelationContext(
                "corr-scoped-request-1",
                "req-scoped-request-1",
                "trace-scoped-request-1",
                "span-scoped-request-1"));

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-corr",
                strategyKey: "corr-core",
                isDemo: true) with
            {
                CorrelationId = null,
                ParentCorrelationId = null
            },
            CancellationToken.None);

        Assert.Equal("corr-scoped-request-1", result.Order.RootCorrelationId);
    }

    [Fact]
    public async Task DispatchAsync_UsesDecisionTraceCorrelation_WhenCommandOmitsExplicitCorrelation()
    {
        await using var harness = CreateHarness();
        await SeedDemoWalletAsync(harness.DbContext, "user-decision-corr", "USDT", 1000m);
        harness.MarketDataService.SetLatestPrice("BTCUSDT", 65000m, harness.TimeProvider.GetUtcNow().UtcDateTime, "unit-test");
        harness.MarketDataService.SetSymbolMetadata("BTCUSDT", "BTC", "USDT", 0.01m, 0.0001m);
        await PrimeFreshMarketDataAsync(harness, "corr-decision-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-decision",
            context: "Open demo execution",
            correlationId: "corr-decision-2");

        var strategySignalId = Guid.NewGuid();
        await harness.TraceService.WriteDecisionTraceAsync(
            new DecisionTraceWriteRequest(
                "user-decision-corr",
                "BTCUSDT",
                "1m",
                "StrategyVersion:test",
                "Entry",
                "Persisted",
                "{}",
                12,
                CorrelationId: "corr-from-decision-trace",
                StrategySignalId: strategySignalId));

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-decision-corr",
                strategyKey: "decision-core",
                isDemo: true) with
            {
                StrategySignalId = strategySignalId,
                CorrelationId = null,
                ParentCorrelationId = null
            },
            CancellationToken.None);

        Assert.Equal("corr-from-decision-trace", result.Order.RootCorrelationId);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenUserExecutionOverrideDisablesSession()
    {
        await using var harness = CreateHarness();
        await SeedDemoWalletAsync(harness.DbContext, "user-override", "USDT", 1000m);
        harness.MarketDataService.SetLatestPrice("BTCUSDT", 65000m, harness.TimeProvider.GetUtcNow().UtcDateTime, "unit-test");
        harness.MarketDataService.SetSymbolMetadata("BTCUSDT", "BTC", "USDT", 0.01m, 0.0001m);
        await PrimeFreshMarketDataAsync(harness, "corr-override-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-override",
            context: "Open demo execution",
            correlationId: "corr-override-2");

        harness.DbContext.UserExecutionOverrides.Add(new UserExecutionOverride
        {
            Id = Guid.NewGuid(),
            UserId = "user-override",
            SessionDisabled = true
        });
        await harness.DbContext.SaveChangesAsync();

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-override",
                strategyKey: "override-core",
                isDemo: true),
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Rejected, result.Order.State);
        Assert.Equal("UserExecutionSessionDisabled", result.Order.FailureCode);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_AllowsAdministrativeOverride_ToBypassUserExecutionOverrideGuard()
    {
        await using var harness = CreateHarness();
        await SeedDemoWalletAsync(harness.DbContext, "user-crisis", "BTC", 0m);
        await SeedDemoWalletAsync(harness.DbContext, "user-crisis", "USDT", 5000m);
        harness.MarketDataService.SetLatestPrice("BTCUSDT", 65000m, harness.TimeProvider.GetUtcNow().UtcDateTime, "unit-test");
        harness.MarketDataService.SetSymbolMetadata("BTCUSDT", "BTC", "USDT", 0.01m, 0.0001m);
        await PrimeFreshMarketDataAsync(harness, "corr-admin-override-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-override",
            context: "Open demo execution",
            correlationId: "corr-admin-override-2");

        harness.DbContext.UserExecutionOverrides.Add(new UserExecutionOverride
        {
            Id = Guid.NewGuid(),
            UserId = "user-crisis",
            SessionDisabled = true
        });
        await harness.DbContext.SaveChangesAsync();

        var result = await harness.Engine.DispatchAsync(
            CreateCommand(
                ownerUserId: "user-crisis",
                strategyKey: "crisis-override",
                isDemo: true) with
            {
                Actor = "admin:super-admin",
                AdministrativeOverride = true,
                AdministrativeOverrideReason = "CrisisEmergencyFlatten|PositionHash=test-hash"
            },
            CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Filled, result.Order.State);
        Assert.Null(result.Order.FailureCode);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    private static TestHarness CreateHarness(
        string environmentName = "Production",
        RecordingAlertDispatchCoordinator? alertDispatchCoordinator = null,
        bool enableRiskPolicyEvaluator = false,
        bool allowInternalDemoExecution = true,
        BinanceFuturesTestnetOptions? testnetExecutionOptions = null,
        FakeExchangeCredentialService? credentialService = null)
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var correlationContextAccessor = new CorrelationContextAccessor();
        var auditLogService = new AuditLogService(dbContext, correlationContextAccessor);
        var runtimeEnvironment = new TestHostEnvironment(environmentName);
        alertDispatchCoordinator ??= new RecordingAlertDispatchCoordinator();
        var switchService = new GlobalExecutionSwitchService(
            dbContext,
            auditLogService,
            alertDispatchCoordinator,
            runtimeEnvironment);
        var globalSystemStateService = new GlobalSystemStateService(dbContext, auditLogService, timeProvider);
        var marketDataService = new FakeMarketDataService();
        marketDataService.SetSymbolMetadata(
            "BTCUSDT",
            "BTC",
            "USDT",
            0.1m,
            0.001m,
            minQuantity: 0.001m,
            minNotional: 100m,
            pricePrecision: 1,
            quantityPrecision: 3);
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
        var traceService = new TraceService(
            dbContext,
            correlationContextAccessor,
            timeProvider);
        var executionRuntimeOptions = Options.Create(new ExecutionRuntimeOptions
        {
            AllowInternalDemoExecution = allowInternalDemoExecution
        });
        var lifecycleService = new ExecutionOrderLifecycleService(
            dbContext,
            auditLogService,
            timeProvider,
            NullLogger<ExecutionOrderLifecycleService>.Instance,
            executionRuntimeOptions: executionRuntimeOptions);
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
            RestBaseUrl = runtimeEnvironment.IsDevelopment()
                ? "https://testnet.binance.example/futures-rest"
                : "https://fapi.binance.com",
            WebSocketBaseUrl = runtimeEnvironment.IsDevelopment()
                ? "wss://testnet.binance.example/futures-private"
                : "wss://fstream.binance.com"
        });
        var marketDataOptions = Options.Create(new BinanceMarketDataOptions
        {
            RestBaseUrl = runtimeEnvironment.IsDevelopment()
                ? "https://testnet.binance.example/futures-market-rest"
                : "https://fapi.binance.com",
            WebSocketBaseUrl = runtimeEnvironment.IsDevelopment()
                ? "wss://testnet.binance.example/futures-market-stream"
                : "wss://fstream.binance.com",
            KlineInterval = "1m"
        });
        IRiskPolicyEvaluator? riskPolicyEvaluator = enableRiskPolicyEvaluator
            ? new RiskPolicyEvaluator(
                dbContext,
                timeProvider,
                NullLogger<RiskPolicyEvaluator>.Instance)
            : null;
        var executionGate = new ExecutionGate(
            demoSessionService,
            globalSystemStateService,
            switchService,
            circuitBreaker,
            tradingModeService,
            auditLogService,
            NullLogger<ExecutionGate>.Instance,
            runtimeEnvironment,
            traceService,
            timeProvider,
            latencyOptions,
            dbContext,
            privateDataOptions,
            marketDataOptions,
            Options.Create(pilotOptions),
            executionRuntimeOptions);
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
        var userExecutionOverrideGuard = new UserExecutionOverrideGuard(
            dbContext,
            tradingModeService,
            logger: NullLogger<UserExecutionOverrideGuard>.Instance,
            hostEnvironment: runtimeEnvironment,
            riskPolicyEvaluator: riskPolicyEvaluator,
            botExecutionPilotOptions: Options.Create(pilotOptions),
            executionRuntimeOptions: executionRuntimeOptions);
        credentialService ??= new FakeExchangeCredentialService();
        var privateRestClient = new FakePrivateRestClient(timeProvider);
        var spotPrivateRestClient = new FakeSpotPrivateRestClient(timeProvider);
        var engine = new ExecutionEngine(
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
                marketDataService: marketDataService,
                privateDataOptions: privateDataOptions,
                binanceFuturesTestnetOptions: Options.Create(testnetExecutionOptions ?? new BinanceFuturesTestnetOptions
                {
                    BaseUrl = privateDataOptions.Value.RestBaseUrl,
                    ApiKey = "testnet-api-key",
                    ApiSecret = "testnet-api-secret"
                }),
                botExecutionPilotOptions: Options.Create(pilotOptions)),
            new BinanceSpotExecutor(
                dbContext,
                credentialService,
                spotPrivateRestClient,
                NullLogger<BinanceSpotExecutor>.Instance,
                marketDataService: marketDataService),
            lifecycleService,
            timeProvider,
            NullLogger<ExecutionEngine>.Instance,
            alertDispatchCoordinator,
            runtimeEnvironment,
            runtimeOptions: executionRuntimeOptions);

        return new TestHarness(
            dbContext,
            switchService,
            circuitBreaker,
            demoSessionService,
            tradingModeService,
            correlationContextAccessor,
            traceService,
            engine,
            timeProvider,
            marketDataService,
            credentialService,
            privateRestClient,
            spotPrivateRestClient,
            alertDispatchCoordinator,
            pilotOptions);
    }

    private static async Task PrimeFreshMarketDataAsync(
        TestHarness harness,
        string correlationId,
        string symbol = "BTCUSDT",
        string timeframe = "1m")
    {
        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                $"binance-{symbol.ToLowerInvariant()}",
                harness.TimeProvider.GetUtcNow().UtcDateTime,
                Symbol: symbol,
                Timeframe: timeframe,
                ExpectedOpenTimeUtc: harness.TimeProvider.GetUtcNow().UtcDateTime,
                ContinuityGapCount: 0),
            correlationId);
    }

    private static async Task SeedBotAsync(
        ApplicationDbContext dbContext,
        string ownerUserId,
        Guid botId,
        string strategyKey)
    {
        dbContext.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = ownerUserId,
            Name = $"{strategyKey}-bot",
            StrategyKey = strategyKey,
            IsEnabled = true
        });

        await dbContext.SaveChangesAsync();
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

    private static async Task SeedLiveStrategyAsync(
        ApplicationDbContext dbContext,
        string ownerUserId,
        Guid strategyId,
        string strategyKey)
    {
        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = strategyId,
            OwnerUserId = ownerUserId,
            StrategyKey = strategyKey,
            DisplayName = $"{strategyKey}-strategy",
            PromotionState = StrategyPromotionState.LivePublished,
            PublishedMode = ExecutionEnvironment.Live,
            PublishedAtUtc = new DateTime(2026, 3, 22, 11, 55, 0, DateTimeKind.Utc),
            LivePromotionApprovedAtUtc = new DateTime(2026, 3, 22, 11, 50, 0, DateTimeKind.Utc),
            LivePromotionApprovalReference = "approval-live-1"
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

    private static async Task SeedPilotSafetyPrerequisitesAsync(
        ApplicationDbContext dbContext,
        string ownerUserId,
        Guid exchangeAccountId,
        DateTime observedAtUtc)
    {
        dbContext.RiskProfiles.Add(new RiskProfile
        {
            OwnerUserId = ownerUserId,
            ProfileName = "Pilot",
            MaxDailyLossPercentage = 5m,
            MaxPositionSizePercentage = 100m,
            MaxLeverage = 2m,
            MaxConcurrentPositions = 1
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
            EnvironmentScope = "Testnet",
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

        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedSpotValidationAsync(
        ApplicationDbContext dbContext,
        string ownerUserId,
        Guid exchangeAccountId)
    {
        dbContext.ApiCredentialValidations.Add(new ApiCredentialValidation
        {
            Id = Guid.NewGuid(),
            ExchangeAccountId = exchangeAccountId,
            OwnerUserId = ownerUserId,
            IsKeyValid = true,
            CanTrade = true,
            SupportsSpot = true,
            SupportsFutures = false,
            ValidationStatus = "Valid",
            PermissionSummary = "Trade=Y; Spot=Y",
            ValidatedAtUtc = new DateTime(2026, 3, 22, 11, 50, 0, DateTimeKind.Utc)
        });

        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedSpotBalanceAsync(
        ApplicationDbContext dbContext,
        string ownerUserId,
        Guid exchangeAccountId,
        string asset,
        decimal availableBalance,
        decimal lockedBalance)
    {
        dbContext.ExchangeBalances.Add(new ExchangeBalance
        {
            OwnerUserId = ownerUserId,
            ExchangeAccountId = exchangeAccountId,
            Plane = ExchangeDataPlane.Spot,
            Asset = asset,
            WalletBalance = availableBalance + lockedBalance,
            CrossWalletBalance = availableBalance + lockedBalance,
            AvailableBalance = availableBalance,
            MaxWithdrawAmount = availableBalance,
            LockedBalance = lockedBalance,
            ExchangeUpdatedAtUtc = new DateTime(2026, 3, 22, 11, 55, 0, DateTimeKind.Utc),
            SyncedAtUtc = new DateTime(2026, 3, 22, 11, 55, 0, DateTimeKind.Utc)
        });

        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedExecutionOrderAsync(
        ApplicationDbContext dbContext,
        string ownerUserId,
        Guid executionOrderId,
        ExecutionEnvironment executionEnvironment = ExecutionEnvironment.Demo)
    {
        dbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = executionOrderId,
            OwnerUserId = ownerUserId,
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            StrategyKey = "protect-core",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.05m,
            Price = 65000m,
            ExecutionEnvironment = executionEnvironment,
            ExecutorKind = executionEnvironment == ExecutionEnvironment.Demo
                ? ExecutionOrderExecutorKind.Virtual
                : ExecutionOrderExecutorKind.Binance,
            State = ExecutionOrderState.Submitted,
            IdempotencyKey = $"seed_{executionOrderId:N}",
            RootCorrelationId = "seed-correlation-1",
            ExternalOrderId = $"virtual:{executionOrderId:N}",
            SubmittedAtUtc = new DateTime(2026, 3, 22, 11, 55, 0, DateTimeKind.Utc),
            LastStateChangedAtUtc = new DateTime(2026, 3, 22, 11, 55, 0, DateTimeKind.Utc)
        });

        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedExistingExecutionIntentOrderAsync(
        ApplicationDbContext dbContext,
        string ownerUserId,
        Guid strategyId,
        Guid strategySignalId,
        Guid exchangeAccountId,
        ExecutionOrderSide side,
        ExecutionOrderState state,
        bool submittedToBroker,
        string? failureCode,
        ExecutionRejectionStage rejectionStage)
    {
        var orderId = Guid.NewGuid();
        dbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = orderId,
            OwnerUserId = ownerUserId,
            TradingStrategyId = strategyId,
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = strategySignalId,
            SignalType = StrategySignalType.Entry,
            StrategyKey = "duplicate-intent-core",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = side,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.05m,
            Price = 65000m,
            ExchangeAccountId = exchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            SubmittedToBroker = submittedToBroker,
            State = state,
            FailureCode = failureCode,
            RejectionStage = rejectionStage,
            IdempotencyKey = $"seed-existing-intent-{orderId:N}",
            RootCorrelationId = "seed-existing-intent-correlation",
            ExternalOrderId = submittedToBroker ? $"binance:{orderId:N}" : null,
            LastStateChangedAtUtc = new DateTime(2026, 3, 22, 11, 55, 0, DateTimeKind.Utc)
        });

        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedSubmittedLiveExecutionOrderAsync(
        ApplicationDbContext dbContext,
        string ownerUserId,
        Guid exchangeAccountId,
        string symbol,
        decimal quantity,
        ExecutionOrderSide side,
        bool reduceOnly)
    {
        var orderId = Guid.NewGuid();
        dbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = orderId,
            OwnerUserId = ownerUserId,
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            StrategyKey = "reduce-live-fallback",
            Symbol = symbol,
            Timeframe = "1m",
            BaseAsset = symbol[..^4],
            QuoteAsset = "USDT",
            Side = side,
            OrderType = ExecutionOrderType.Market,
            Quantity = quantity,
            Price = 65000m,
            ExchangeAccountId = exchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            ReduceOnly = reduceOnly,
            SubmittedToBroker = true,
            State = ExecutionOrderState.Submitted,
            IdempotencyKey = $"seed-live-{orderId:N}",
            RootCorrelationId = "seed-live-correlation-1",
            ExternalOrderId = $"binance:{orderId:N}",
            SubmittedAtUtc = new DateTime(2026, 3, 22, 11, 55, 0, DateTimeKind.Utc),
            LastStateChangedAtUtc = new DateTime(2026, 3, 22, 11, 55, 0, DateTimeKind.Utc)
        });

        await dbContext.SaveChangesAsync();
    }


    private static async Task SeedDemoWalletAsync(
        ApplicationDbContext dbContext,
        string ownerUserId,
        string asset,
        decimal availableBalance)
    {
        dbContext.DemoWallets.Add(new DemoWallet
        {
            OwnerUserId = ownerUserId,
            Asset = asset,
            AvailableBalance = availableBalance,
            ReservedBalance = 0m,
            LastActivityAtUtc = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc)
        });

        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedDemoPositionAsync(
        ApplicationDbContext dbContext,
        string ownerUserId,
        string symbol,
        string baseAsset,
        string quoteAsset,
        decimal quantity,
        decimal averageEntryPrice)
    {
        dbContext.DemoPositions.Add(new DemoPosition
        {
            OwnerUserId = ownerUserId,
            PositionScopeKey = $"{ownerUserId}:{symbol}",
            Symbol = symbol,
            BaseAsset = baseAsset,
            QuoteAsset = quoteAsset,
            Quantity = quantity,
            CostBasis = quantity * averageEntryPrice,
            AverageEntryPrice = averageEntryPrice,
            LastPrice = averageEntryPrice,
            LastMarkPrice = averageEntryPrice,
            LastValuationAtUtc = new DateTime(2026, 3, 22, 11, 55, 0, DateTimeKind.Utc)
        });

        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedExchangePositionAsync(
        ApplicationDbContext dbContext,
        string ownerUserId,
        Guid exchangeAccountId,
        string symbol,
        string positionSide,
        decimal quantity,
        decimal entryPrice)
    {
        dbContext.ExchangePositions.Add(new ExchangePosition
        {
            OwnerUserId = ownerUserId,
            ExchangeAccountId = exchangeAccountId,
            Symbol = symbol,
            PositionSide = positionSide,
            Quantity = quantity,
            EntryPrice = entryPrice,
            BreakEvenPrice = entryPrice,
            MarginType = "ISOLATED",
            ExchangeUpdatedAtUtc = new DateTime(2026, 3, 22, 11, 55, 0, DateTimeKind.Utc),
            SyncedAtUtc = new DateTime(2026, 3, 22, 11, 55, 0, DateTimeKind.Utc)
        });

        await dbContext.SaveChangesAsync();
    }

    private static ExecutionCommand CreateCommand(
        string ownerUserId,
        string strategyKey,
        bool? isDemo,
        Guid strategyId = default,
        Guid? botId = null,
        Guid? exchangeAccountId = null)
    {
        return new ExecutionCommand(
            Actor: "worker-exec",
            OwnerUserId: ownerUserId,
            TradingStrategyId: strategyId == default ? Guid.NewGuid() : strategyId,
            TradingStrategyVersionId: Guid.NewGuid(),
            StrategySignalId: Guid.NewGuid(),
            SignalType: StrategySignalType.Entry,
            StrategyKey: strategyKey,
            Symbol: "BTCUSDT",
            Timeframe: "1m",
            BaseAsset: "BTC",
            QuoteAsset: "USDT",
            Side: ExecutionOrderSide.Buy,
            OrderType: ExecutionOrderType.Market,
            Quantity: 0.05m,
            Price: 65000m,
            BotId: botId,
            ExchangeAccountId: exchangeAccountId,
            IsDemo: isDemo,
            CorrelationId: "root-correlation-1",
            ParentCorrelationId: "signal-correlation-1",
            Context: "ExecutionEngineTests");
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

    private sealed class RecordingAlertDispatchCoordinator : IAlertDispatchCoordinator
    {
        public List<AlertNotification> Notifications { get; } = [];

        public Task SendAsync(
            AlertNotification notification,
            string dedupeKey,
            TimeSpan cooldown,
            CancellationToken cancellationToken = default)
        {
            Notifications.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeExchangeCredentialService : IExchangeCredentialService
    {
        public int AccessCalls { get; private set; }

        public string ApiKey { get; set; } = "api-key";

        public string ApiSecret { get; set; } = "api-secret";

        public Exception? AccessException { get; set; }

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
            AccessCalls++;

            if (AccessException is not null)
            {
                throw AccessException;
            }

            return Task.FromResult(
                new ExchangeCredentialAccessResult(
                    ApiKey,
                    ApiSecret,
                    new ExchangeCredentialStateSnapshot(
                        request.ExchangeAccountId,
                        ExchangeCredentialStatus.Active,
                        "fingerprint",
                        "v1",
                        StoredAtUtc: new DateTime(2026, 3, 22, 11, 0, 0, DateTimeKind.Utc),
                        LastValidatedAtUtc: new DateTime(2026, 3, 22, 11, 5, 0, DateTimeKind.Utc),
                        LastAccessedAtUtc: new DateTime(2026, 3, 22, 11, 5, 0, DateTimeKind.Utc),
                        LastRotatedAtUtc: new DateTime(2026, 3, 22, 10, 0, 0, DateTimeKind.Utc),
                        RevalidateAfterUtc: new DateTime(2026, 4, 21, 11, 5, 0, DateTimeKind.Utc),
                        RotateAfterUtc: new DateTime(2026, 6, 20, 11, 5, 0, DateTimeKind.Utc))));
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
        public int PlaceOrderCalls { get; private set; }

        public int EnsureMarginTypeCalls { get; private set; }

        public int EnsureLeverageCalls { get; private set; }

        public BinanceOrderPlacementRequest? LastPlacementRequest { get; private set; }

        public Exception? PlaceOrderException { get; set; }

        public Exception? EnsureLeverageException { get; set; }

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

            if (EnsureLeverageException is not null)
            {
                throw EnsureLeverageException;
            }

            return Task.CompletedTask;
        }

        public Task<BinanceOrderPlacementResult> PlaceOrderAsync(
            BinanceOrderPlacementRequest request,
            CancellationToken cancellationToken = default)
        {
            PlaceOrderCalls++;
            LastPlacementRequest = request;

            if (PlaceOrderException is not null)
            {
                throw PlaceOrderException;
            }

            return Task.FromResult(
                new BinanceOrderPlacementResult(
                    $"binance-order-{PlaceOrderCalls}",
                    request.ClientOrderId,
                    timeProvider.GetUtcNow().UtcDateTime));
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
        public int PlaceOrderCalls { get; private set; }

        public BinanceOrderPlacementRequest? LastPlacementRequest { get; private set; }

        public Exception? PlaceOrderException { get; set; }

        public BinanceOrderStatusSnapshot? PlacementSnapshot { get; set; }

        public Task<BinanceOrderPlacementResult> PlaceOrderAsync(
            BinanceOrderPlacementRequest request,
            CancellationToken cancellationToken = default)
        {
            PlaceOrderCalls++;
            LastPlacementRequest = request;

            if (PlaceOrderException is not null)
            {
                throw PlaceOrderException;
            }

            var snapshot = PlacementSnapshot ?? new BinanceOrderStatusSnapshot(
                request.Symbol,
                $"spot-order-{PlaceOrderCalls}",
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

            return Task.FromResult(
                new BinanceOrderPlacementResult(
                    snapshot.ExchangeOrderId,
                    snapshot.ClientOrderId,
                    timeProvider.GetUtcNow().UtcDateTime,
                    snapshot));
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

    private sealed class FakeMarketDataService : IMarketDataService
    {
        private readonly Dictionary<string, MarketPriceSnapshot> prices = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SymbolMetadataSnapshot> metadata = new(StringComparer.Ordinal);

        public void SetLatestPrice(string symbol, decimal price, DateTime observedAtUtc, string source)
        {
            prices[symbol] = new MarketPriceSnapshot(symbol, price, observedAtUtc, observedAtUtc, source);
        }

        public void SetSymbolMetadata(
            string symbol,
            string baseAsset,
            string quoteAsset,
            decimal tickSize,
            decimal stepSize,
            decimal? minQuantity = null,
            decimal? minNotional = null,
            int? pricePrecision = null,
            int? quantityPrecision = null)
        {
            metadata[symbol] = new SymbolMetadataSnapshot(
                symbol,
                "Binance",
                baseAsset,
                quoteAsset,
                tickSize,
                stepSize,
                "TRADING",
                true,
                DateTime.UtcNow)
            {
                MinQuantity = minQuantity,
                MinNotional = minNotional,
                PricePrecision = pricePrecision,
                QuantityPrecision = quantityPrecision
            };
        }

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
            prices.TryGetValue(symbol, out var snapshot);
            return ValueTask.FromResult<MarketPriceSnapshot?>(snapshot);
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            metadata.TryGetValue(symbol, out var snapshot);
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
    }

    private sealed class TestHarness(
        ApplicationDbContext dbContext,
        IGlobalExecutionSwitchService switchService,
        IDataLatencyCircuitBreaker circuitBreaker,
        IDemoSessionService demoSessionService,
        ITradingModeService tradingModeService,
        CorrelationContextAccessor correlationContextAccessor,
        ITraceService traceService,
        IExecutionEngine engine,
        AdjustableTimeProvider timeProvider,
        FakeMarketDataService marketDataService,
        FakeExchangeCredentialService credentialService,
        FakePrivateRestClient privateRestClient,
        FakeSpotPrivateRestClient spotPrivateRestClient,
        RecordingAlertDispatchCoordinator alertCoordinator,
        BotExecutionPilotOptions pilotOptions) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public IGlobalExecutionSwitchService SwitchService { get; } = switchService;

        public IDataLatencyCircuitBreaker CircuitBreaker { get; } = circuitBreaker;

        public IDemoSessionService DemoSessionService { get; } = demoSessionService;

        public ITradingModeService TradingModeService { get; } = tradingModeService;

        public CorrelationContextAccessor CorrelationContextAccessor { get; } = correlationContextAccessor;

        public ITraceService TraceService { get; } = traceService;

        public IExecutionEngine Engine { get; } = engine;

        public AdjustableTimeProvider TimeProvider { get; } = timeProvider;

        public FakeMarketDataService MarketDataService { get; } = marketDataService;

        public FakeExchangeCredentialService CredentialService { get; } = credentialService;

        public FakePrivateRestClient PrivateRestClient { get; } = privateRestClient;

        public FakeSpotPrivateRestClient SpotPrivateRestClient { get; } = spotPrivateRestClient;

        public RecordingAlertDispatchCoordinator AlertCoordinator { get; } = alertCoordinator;

        public BotExecutionPilotOptions PilotOptions { get; } = pilotOptions;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "CoinBot.UnitTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
