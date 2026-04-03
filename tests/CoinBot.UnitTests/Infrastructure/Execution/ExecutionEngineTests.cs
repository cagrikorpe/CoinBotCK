using CoinBot.Application.Abstractions.Alerts;
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
using CoinBot.Infrastructure.Alerts;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.DemoPortfolio;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
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
    public async Task DispatchAsync_RoutesToBinanceExecutor_WhenDevelopmentPilotOverridesDemoMode()
    {
        await using var harness = CreateHarness(environmentName: Environments.Development);
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        await SeedExchangeAccountAsync(harness.DbContext, "user-pilot-live", exchangeAccountId);
        await SeedBotAsync(harness.DbContext, "user-pilot-live", botId, "pilot-core");
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

        Assert.Equal(ExecutionEnvironment.Live, result.Order.ExecutionEnvironment);
        Assert.Equal(ExecutionOrderExecutorKind.Binance, result.Order.ExecutorKind);
        Assert.Equal(ExecutionOrderState.Submitted, result.Order.State);
        Assert.Equal(1, harness.PrivateRestClient.EnsureMarginTypeCalls);
        Assert.Equal(1, harness.PrivateRestClient.EnsureLeverageCalls);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenDevelopmentPilotViolatesFuturesMinNotional()
    {
        await using var harness = CreateHarness(environmentName: Environments.Development);
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        await SeedExchangeAccountAsync(harness.DbContext, "user-pilot-invalid", exchangeAccountId);
        await SeedBotAsync(harness.DbContext, "user-pilot-invalid", botId, "pilot-core");
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
                  isDemo: false,
                  botId: botId,
                exchangeAccountId: exchangeAccountId) with
              {
                  Actor = "system:bot-worker",
                  Context = "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                  Quantity = 0.001m,
                  Price = 65000m
              },
              CancellationToken.None);

        Assert.Equal(ExecutionOrderState.Rejected, result.Order.State);
        Assert.Equal(nameof(ExecutionValidationException), result.Order.FailureCode);
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
            environmentName: Environments.Development,
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
    public async Task DispatchAsync_FailsPostSubmit_WithRetryEligibleAndCooldownApplied_WhenBrokerRequestFails()
    {
        await using var harness = CreateHarness(environmentName: Environments.Development);
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

        Assert.Equal(ExecutionOrderState.Failed, result.Order.State);
        Assert.Equal(nameof(InvalidOperationException), result.Order.FailureCode);
        Assert.Equal(ExecutionRejectionStage.PostSubmit, result.Order.RejectionStage);
        Assert.True(result.Order.SubmittedToBroker);
        Assert.True(result.Order.RetryEligible);
        Assert.True(result.Order.CooldownApplied);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.StartsWith("cbp0_", result.Order.ClientOrderId, StringComparison.Ordinal);
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
        RecordingAlertDispatchCoordinator? alertDispatchCoordinator = null)
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
        var circuitBreaker = new DataLatencyCircuitBreaker(
            dbContext,
            new FakeAlertService(),
            Options.Create(new DataLatencyGuardOptions()),
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
        var executionGate = new ExecutionGate(
            demoSessionService,
            globalSystemStateService,
            switchService,
            circuitBreaker,
            tradingModeService,
            auditLogService,
            NullLogger<ExecutionGate>.Instance,
            runtimeEnvironment);
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
        var traceService = new TraceService(
            dbContext,
            correlationContextAccessor,
            timeProvider);
        var userExecutionOverrideGuard = new UserExecutionOverrideGuard(
            dbContext,
            tradingModeService,
            hostEnvironment: runtimeEnvironment);
        var credentialService = new FakeExchangeCredentialService();
        var privateRestClient = new FakePrivateRestClient(timeProvider);
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
                marketDataService: marketDataService),
            timeProvider,
            NullLogger<ExecutionEngine>.Instance,
            alertDispatchCoordinator,
            runtimeEnvironment);

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
            alertDispatchCoordinator);
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

            return Task.FromResult(
                new ExchangeCredentialAccessResult(
                    "api-key",
                    "api-secret",
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
        RecordingAlertDispatchCoordinator alertCoordinator) : IAsyncDisposable
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

        public RecordingAlertDispatchCoordinator AlertCoordinator { get; } = alertCoordinator;

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
