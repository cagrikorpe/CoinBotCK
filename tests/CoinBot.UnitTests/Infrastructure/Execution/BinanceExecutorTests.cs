using CoinBot.Application.Abstractions.Autonomy;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Execution;

public sealed class BinanceExecutorTests
{
    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenQuantityIsBelowMinQuantity()
    {
        await using var harness = await CreateHarnessAsync();

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                Quantity = 0.0005m,
                Price = 65000m
            },
            CancellationToken.None));

        Assert.Equal("OrderQuantityBelowMinimum", exception.ReasonCode);
        Assert.Contains("minimum quantity", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenQuantityViolatesStepSize()
    {
        await using var harness = await CreateHarnessAsync();

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                Quantity = 0.0015m,
                Price = 65000m
            },
            CancellationToken.None));

        Assert.Equal("OrderQuantityStepSizeMismatch", exception.ReasonCode);
        Assert.Contains("step size", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenQuantityViolatesQuantityPrecision()
    {
        await using var harness = await CreateHarnessAsync(
            metadata: CreateMetadata(stepSize: 0.00001m, quantityPrecision: 3));

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                Quantity = 0.00155m,
                Price = 65000m
            },
            CancellationToken.None));

        Assert.Equal("OrderQuantityPrecisionExceeded", exception.ReasonCode);
        Assert.Contains("quantity precision", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_AllowsQuantity_WhenOnlyTrailingZerosExceedPrecision()
    {
        await using var harness = await CreateHarnessAsync(
            metadata: CreateMetadata(stepSize: 0.01m, minQuantity: 0.01m, quantityPrecision: 2));

        var result = await harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                SignalType = StrategySignalType.Exit,
                Side = ExecutionOrderSide.Sell,
                ReduceOnly = true,
                Quantity = 0.660000000000000000m,
                Price = 200m
            },
            CancellationToken.None);

        Assert.Equal("binance-order-1", result.ExternalOrderId);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.NotNull(harness.PrivateRestClient.LastPlacementRequest);
        Assert.Equal(0.660000000000000000m, harness.PrivateRestClient.LastPlacementRequest.Quantity);
        Assert.Equal(harness.Order.Id.ToString("N"), harness.PrivateRestClient.LastPlacementRequest.ExecutionAttemptId);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenNotionalIsBelowMinimum()
    {
        await using var harness = await CreateHarnessAsync();

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                Quantity = 0.001m,
                Price = 65000m
            },
            CancellationToken.None));

        Assert.Equal("OrderNotionalBelowMinimum", exception.ReasonCode);
        Assert.Contains("minimum notional", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }


    [Fact]
    public async Task DispatchAsync_AllowsReduceOnly_WhenNotionalIsBelowMinimum()
    {
        await using var harness = await CreateHarnessAsync();

        var result = await harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                SignalType = StrategySignalType.Exit,
                Side = ExecutionOrderSide.Sell,
                ReduceOnly = true,
                Quantity = 0.001m,
                Price = 65000m
            },
            CancellationToken.None);

        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal("binance-order-1", result.ExternalOrderId);
    }

    [Fact]
    public async Task DispatchAsync_DoesNotTripBreaker_WhenValidationFailsClosed()
    {
        var breakerManager = new RecordingBreakerStateManager();
        await using var harness = await CreateHarnessAsync(dependencyCircuitBreakerStateManager: breakerManager);

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                Quantity = 0.001m,
                Price = 65000m
            },
            CancellationToken.None));

        Assert.Equal("OrderNotionalBelowMinimum", exception.ReasonCode);
        Assert.Equal(0, breakerManager.RecordFailureCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenBinanceTestnetUsesLiveEndpoint()
    {
        await using var harness = await CreateHarnessAsync(
            privateDataOptions: new BinancePrivateDataOptions
            {
                RestBaseUrl = "https://fapi.binance.com",
                WebSocketBaseUrl = "wss://fstream.binance.com",
                SpotRestBaseUrl = "https://api.binance.com",
                SpotWebSocketBaseUrl = "wss://stream.binance.com:9443"
            });
        harness.Order.ExecutionEnvironment = ExecutionEnvironment.BinanceTestnet;
        harness.Order.ExecutorKind = ExecutionOrderExecutorKind.BinanceTestnet;

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                RequestedEnvironment = ExecutionEnvironment.BinanceTestnet
            },
            CancellationToken.None));

        Assert.Equal("TestnetExecutionEndpointMisconfigured", exception.ReasonCode);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenBinanceTestnetEndpointConfigIsMissing()
    {
        await using var harness = await CreateHarnessAsync(
            privateDataOptions: new BinancePrivateDataOptions
            {
                RestBaseUrl = "https://demo-fapi.binance.com",
                WebSocketBaseUrl = "wss://fstream.binancefuture.com",
                SpotRestBaseUrl = "https://api.binance.com",
                SpotWebSocketBaseUrl = "wss://stream.binance.com:9443"
            },
            testnetOptions: new BinanceFuturesTestnetOptions
            {
                ApiKey = "testnet-api-key",
                ApiSecret = "testnet-api-secret"
            });
        harness.Order.ExecutionEnvironment = ExecutionEnvironment.BinanceTestnet;
        harness.Order.ExecutorKind = ExecutionOrderExecutorKind.BinanceTestnet;

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                RequestedEnvironment = ExecutionEnvironment.BinanceTestnet
            },
            CancellationToken.None));

        Assert.Equal("BinanceTestnetEndpointMissing", exception.ReasonCode);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(0, harness.CredentialService.AccessCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenBinanceTestnetApiKeyIsMissing()
    {
        await using var harness = await CreateHarnessAsync(
            privateDataOptions: new BinancePrivateDataOptions
            {
                RestBaseUrl = "https://demo-fapi.binance.com",
                WebSocketBaseUrl = "wss://fstream.binancefuture.com",
                SpotRestBaseUrl = "https://api.binance.com",
                SpotWebSocketBaseUrl = "wss://stream.binance.com:9443"
            },
            credentialService: new FakeExchangeCredentialService
            {
                AccessException = new InvalidOperationException("User-scoped credential missing.")
            },
            testnetOptions: new BinanceFuturesTestnetOptions
            {
                BaseUrl = "https://demo-fapi.binance.com",
                ApiSecret = "plain-secret-testnet-key-missing",
                AllowConfiguredCredentialFallback = true
            });
        harness.Order.ExecutionEnvironment = ExecutionEnvironment.BinanceTestnet;
        harness.Order.ExecutorKind = ExecutionOrderExecutorKind.BinanceTestnet;

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                RequestedEnvironment = ExecutionEnvironment.BinanceTestnet
            },
            CancellationToken.None));

        Assert.Equal("BinanceTestnetCredentialsMissing", exception.ReasonCode);
        Assert.DoesNotContain("plain-secret-testnet-key-missing", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(1, harness.CredentialService.AccessCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenBinanceTestnetApiSecretIsMissing()
    {
        await using var harness = await CreateHarnessAsync(
            privateDataOptions: new BinancePrivateDataOptions
            {
                RestBaseUrl = "https://demo-fapi.binance.com",
                WebSocketBaseUrl = "wss://fstream.binancefuture.com",
                SpotRestBaseUrl = "https://api.binance.com",
                SpotWebSocketBaseUrl = "wss://stream.binance.com:9443"
            },
            credentialService: new FakeExchangeCredentialService
            {
                AccessException = new InvalidOperationException("User-scoped credential missing.")
            },
            testnetOptions: new BinanceFuturesTestnetOptions
            {
                BaseUrl = "https://demo-fapi.binance.com",
                ApiKey = "plain-key-testnet-secret-missing",
                AllowConfiguredCredentialFallback = true
            });
        harness.Order.ExecutionEnvironment = ExecutionEnvironment.BinanceTestnet;
        harness.Order.ExecutorKind = ExecutionOrderExecutorKind.BinanceTestnet;

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                RequestedEnvironment = ExecutionEnvironment.BinanceTestnet
            },
            CancellationToken.None));

        Assert.Equal("BinanceTestnetCredentialsMissing", exception.ReasonCode);
        Assert.DoesNotContain("plain-key-testnet-secret-missing", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(1, harness.CredentialService.AccessCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenLiveExecutionUsesTestnetEndpoint()
    {
        await using var harness = await CreateHarnessAsync(
            privateDataOptions: new BinancePrivateDataOptions
            {
                RestBaseUrl = "https://demo-fapi.binance.com",
                WebSocketBaseUrl = "wss://testnet.binance.example/futures-private",
                SpotRestBaseUrl = "https://testnet.binance.example/spot-rest",
                SpotWebSocketBaseUrl = "wss://testnet.binance.example/spot-private"
            });

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                RequestedEnvironment = ExecutionEnvironment.Live,
                Context = "NonPilotExecution=True"
            },
            CancellationToken.None));

        Assert.Equal("LiveExecutionEndpointMisconfigured", exception.ReasonCode);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_UsesUserExchangeCredentials_ForBinanceTestnetWhenAvailable()
    {
        await using var harness = await CreateHarnessAsync(
            privateDataOptions: new BinancePrivateDataOptions
            {
                RestBaseUrl = "https://demo-fapi.binance.com",
                WebSocketBaseUrl = "wss://fstream.binancefuture.com",
                SpotRestBaseUrl = "https://api.binance.com",
                SpotWebSocketBaseUrl = "wss://stream.binance.com:9443"
            },
            testnetOptions: new BinanceFuturesTestnetOptions
            {
                BaseUrl = "https://demo-fapi.binance.com",
                ApiKey = "testnet-api-key",
                ApiSecret = "testnet-api-secret"
            });
        harness.Order.ExecutionEnvironment = ExecutionEnvironment.BinanceTestnet;
        harness.Order.ExecutorKind = ExecutionOrderExecutorKind.BinanceTestnet;

        var result = await harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                RequestedEnvironment = ExecutionEnvironment.BinanceTestnet
            },
            CancellationToken.None);

        Assert.Equal("binance-order-1", result.ExternalOrderId);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(1, harness.CredentialService.AccessCalls);
        Assert.Equal("api-key", harness.PrivateRestClient.LastPlacementRequest?.ApiKey);
        Assert.Equal("api-secret", harness.PrivateRestClient.LastPlacementRequest?.ApiSecret);
    }

    [Fact]
    public async Task DispatchAsync_UsesConfiguredFallback_ForBinanceTestnetOnlyWhenExplicitlyEnabled()
    {
        await using var harness = await CreateHarnessAsync(
            privateDataOptions: new BinancePrivateDataOptions
            {
                RestBaseUrl = "https://demo-fapi.binance.com",
                WebSocketBaseUrl = "wss://fstream.binancefuture.com",
                SpotRestBaseUrl = "https://api.binance.com",
                SpotWebSocketBaseUrl = "wss://stream.binance.com:9443"
            },
            credentialService: new FakeExchangeCredentialService
            {
                AccessException = new InvalidOperationException("User-scoped credential missing.")
            },
            testnetOptions: new BinanceFuturesTestnetOptions
            {
                BaseUrl = "https://demo-fapi.binance.com",
                ApiKey = "testnet-api-key",
                ApiSecret = "testnet-api-secret",
                AllowConfiguredCredentialFallback = true
            });
        harness.Order.ExecutionEnvironment = ExecutionEnvironment.BinanceTestnet;
        harness.Order.ExecutorKind = ExecutionOrderExecutorKind.BinanceTestnet;

        var result = await harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                RequestedEnvironment = ExecutionEnvironment.BinanceTestnet
            },
            CancellationToken.None);

        Assert.Equal("binance-order-1", result.ExternalOrderId);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(1, harness.CredentialService.AccessCalls);
        Assert.Equal("testnet-api-key", harness.PrivateRestClient.LastPlacementRequest?.ApiKey);
        Assert.Equal("testnet-api-secret", harness.PrivateRestClient.LastPlacementRequest?.ApiSecret);
    }

    [Fact]
    public async Task DispatchAsync_AllowsBinanceTestnet_WhenMarginTypeAlreadyMatchesExchangeState()
    {
        await using var harness = await CreateHarnessAsync(
            privateDataOptions: new BinancePrivateDataOptions
            {
                RestBaseUrl = "https://demo-fapi.binance.com",
                WebSocketBaseUrl = "wss://fstream.binancefuture.com",
                SpotRestBaseUrl = "https://api.binance.com",
                SpotWebSocketBaseUrl = "wss://stream.binance.com:9443"
            },
            testnetOptions: new BinanceFuturesTestnetOptions
            {
                BaseUrl = "https://demo-fapi.binance.com",
                ApiKey = "testnet-api-key",
                ApiSecret = "testnet-api-secret"
            });
        harness.Order.ExecutionEnvironment = ExecutionEnvironment.BinanceTestnet;
        harness.Order.ExecutorKind = ExecutionOrderExecutorKind.BinanceTestnet;
        harness.DbContext.ExchangePositions.Add(new ExchangePosition
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-exec",
            ExchangeAccountId = harness.ExchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            Symbol = "BTCUSDT",
            PositionSide = "SHORT",
            Quantity = -0.01m,
            EntryPrice = 65000m,
            BreakEvenPrice = 65000m,
            UnrealizedProfit = 0m,
            MarginType = "isolated",
            IsolatedWallet = 20m,
            ExchangeUpdatedAtUtc = new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc),
            SyncedAtUtc = new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc)
        });
        await harness.DbContext.SaveChangesAsync();

        var result = await harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                RequestedEnvironment = ExecutionEnvironment.BinanceTestnet
            },
            CancellationToken.None);

        Assert.Equal("binance-order-1", result.ExternalOrderId);
        Assert.Equal(0, harness.PrivateRestClient.EnsureMarginTypeCalls);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_DoesNotFail_WhenMarginTypeAlreadySetResponseReturns400()
    {
        await using var harness = await CreateHarnessAsync(
            privateDataOptions: new BinancePrivateDataOptions
            {
                RestBaseUrl = "https://demo-fapi.binance.com",
                WebSocketBaseUrl = "wss://fstream.binancefuture.com",
                SpotRestBaseUrl = "https://api.binance.com",
                SpotWebSocketBaseUrl = "wss://stream.binance.com:9443"
            },
            testnetOptions: new BinanceFuturesTestnetOptions
            {
                BaseUrl = "https://demo-fapi.binance.com",
                ApiKey = "testnet-api-key",
                ApiSecret = "testnet-api-secret"
            });
        harness.Order.ExecutionEnvironment = ExecutionEnvironment.BinanceTestnet;
        harness.Order.ExecutorKind = ExecutionOrderExecutorKind.BinanceTestnet;
        harness.PrivateRestClient.EnsureMarginTypeException = new BinanceExchangeRejectedException(
            "BinanceMarginTypeConfigurationFailed",
            "Binance futures margin type request failed for BTCUSDT with requested margin type ISOLATED and HTTP status 400 (exchange code -4046: No need to change margin type.).",
            "-4046",
            400);

        var result = await harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                RequestedEnvironment = ExecutionEnvironment.BinanceTestnet
            },
            CancellationToken.None);

        Assert.Equal("binance-order-1", result.ExternalOrderId);
        Assert.Equal(1, harness.PrivateRestClient.EnsureMarginTypeCalls);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenOpenPositionMarginTypeDiffersFromRequestedMarginType()
    {
        await using var harness = await CreateHarnessAsync(
            privateDataOptions: new BinancePrivateDataOptions
            {
                RestBaseUrl = "https://demo-fapi.binance.com",
                WebSocketBaseUrl = "wss://fstream.binancefuture.com",
                SpotRestBaseUrl = "https://api.binance.com",
                SpotWebSocketBaseUrl = "wss://stream.binance.com:9443"
            },
            testnetOptions: new BinanceFuturesTestnetOptions
            {
                BaseUrl = "https://demo-fapi.binance.com",
                ApiKey = "testnet-api-key",
                ApiSecret = "testnet-api-secret"
            });
        harness.Order.ExecutionEnvironment = ExecutionEnvironment.BinanceTestnet;
        harness.Order.ExecutorKind = ExecutionOrderExecutorKind.BinanceTestnet;
        harness.DbContext.ExchangePositions.Add(new ExchangePosition
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-exec",
            ExchangeAccountId = harness.ExchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            Symbol = "BTCUSDT",
            PositionSide = "LONG",
            Quantity = 0.01m,
            EntryPrice = 65000m,
            BreakEvenPrice = 65000m,
            UnrealizedProfit = 0m,
            MarginType = "cross",
            IsolatedWallet = 0m,
            ExchangeUpdatedAtUtc = new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc),
            SyncedAtUtc = new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc)
        });
        await harness.DbContext.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                RequestedEnvironment = ExecutionEnvironment.BinanceTestnet
            },
            CancellationToken.None));

        Assert.Equal("BinanceMarginTypeChangeBlockedOpenPosition", exception.ReasonCode);
        Assert.Contains("margin type CROSS", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requested margin type is ISOLATED", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.PrivateRestClient.EnsureMarginTypeCalls);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_SkipsMarginTypeAndLeverageAlignment_ForReduceOnlyClose_WhenOpenPositionMarginTypeDiffers()
    {
        await using var harness = await CreateHarnessAsync(
            privateDataOptions: new BinancePrivateDataOptions
            {
                RestBaseUrl = "https://demo-fapi.binance.com",
                WebSocketBaseUrl = "wss://fstream.binancefuture.com",
                SpotRestBaseUrl = "https://api.binance.com",
                SpotWebSocketBaseUrl = "wss://stream.binance.com:9443"
            },
            testnetOptions: new BinanceFuturesTestnetOptions
            {
                BaseUrl = "https://demo-fapi.binance.com",
                ApiKey = "testnet-api-key",
                ApiSecret = "testnet-api-secret"
            });
        harness.Order.ExecutionEnvironment = ExecutionEnvironment.BinanceTestnet;
        harness.Order.ExecutorKind = ExecutionOrderExecutorKind.BinanceTestnet;
        harness.Order.ReduceOnly = true;
        harness.Order.SignalType = StrategySignalType.Exit;
        harness.DbContext.ExchangePositions.Add(new ExchangePosition
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-exec",
            ExchangeAccountId = harness.ExchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            Symbol = "BTCUSDT",
            PositionSide = "SHORT",
            Quantity = -0.01m,
            EntryPrice = 65000m,
            BreakEvenPrice = 65000m,
            UnrealizedProfit = 0m,
            MarginType = "cross",
            IsolatedWallet = 0m,
            ExchangeUpdatedAtUtc = new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc),
            SyncedAtUtc = new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc)
        });
        await harness.DbContext.SaveChangesAsync();

        var result = await harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                RequestedEnvironment = ExecutionEnvironment.BinanceTestnet,
                SignalType = StrategySignalType.Exit,
                Side = ExecutionOrderSide.Buy,
                ReduceOnly = true,
                Quantity = 0.002m,
                Context = "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1 | ExecutionIntent=ExitCloseOnly | ReduceOnly=True | AutoReverse=False"
            },
            CancellationToken.None);

        Assert.Equal("binance-order-1", result.ExternalOrderId);
        Assert.Equal(0, harness.PrivateRestClient.EnsureMarginTypeCalls);
        Assert.Equal(0, harness.PrivateRestClient.EnsureLeverageCalls);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.True(harness.PrivateRestClient.LastPlacementRequest?.ReduceOnly);
        Assert.Equal(ExecutionOrderSide.Buy, harness.PrivateRestClient.LastPlacementRequest?.Side);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenPilotLeverageExceedsConfiguredMax()
    {
        await using var harness = await CreateHarnessAsync(
            privateDataOptions: new BinancePrivateDataOptions
            {
                RestBaseUrl = "https://demo-fapi.binance.com",
                WebSocketBaseUrl = "wss://fstream.binancefuture.com",
                SpotRestBaseUrl = "https://api.binance.com",
                SpotWebSocketBaseUrl = "wss://stream.binance.com:9443"
            },
            testnetOptions: new BinanceFuturesTestnetOptions
            {
                BaseUrl = "https://demo-fapi.binance.com",
                ApiKey = "testnet-api-key",
                ApiSecret = "testnet-api-secret"
            },
            pilotOptions: new BotExecutionPilotOptions
            {
                MaxAllowedLeverage = 1m
            });
        harness.Order.ExecutionEnvironment = ExecutionEnvironment.BinanceTestnet;
        harness.Order.ExecutorKind = ExecutionOrderExecutorKind.BinanceTestnet;

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                RequestedEnvironment = ExecutionEnvironment.BinanceTestnet,
                Context = "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=2 | RequestedLeverage=2 | EffectiveLeverage=2 | MaxAllowedLeverage=1 | LeverageSource=Bot | LeveragePolicyDecision=Blocked | LeveragePolicyReason=LeveragePolicyExceeded | LeverageAlignmentSkippedForReduceOnly=False"
            },
            CancellationToken.None));

        Assert.Equal("LeveragePolicyExceeded", exception.ReasonCode);
        Assert.Equal(0, harness.PrivateRestClient.EnsureMarginTypeCalls);
        Assert.Equal(0, harness.PrivateRestClient.EnsureLeverageCalls);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WithStableReason_WhenLeverageAlignmentFails()
    {
        await using var harness = await CreateHarnessAsync(
            privateDataOptions: new BinancePrivateDataOptions
            {
                RestBaseUrl = "https://demo-fapi.binance.com",
                WebSocketBaseUrl = "wss://fstream.binancefuture.com",
                SpotRestBaseUrl = "https://api.binance.com",
                SpotWebSocketBaseUrl = "wss://stream.binance.com:9443"
            },
            testnetOptions: new BinanceFuturesTestnetOptions
            {
                BaseUrl = "https://demo-fapi.binance.com",
                ApiKey = "testnet-api-key",
                ApiSecret = "testnet-api-secret"
            });
        harness.Order.ExecutionEnvironment = ExecutionEnvironment.BinanceTestnet;
        harness.Order.ExecutorKind = ExecutionOrderExecutorKind.BinanceTestnet;
        harness.PrivateRestClient.EnsureLeverageException = new BinanceExchangeRejectedException(
            "BinanceLeverageConfigurationFailed",
            "Binance futures leverage request failed for BTCUSDT with requested leverage 1 and HTTP status 400.",
            "-4003",
            400);

        var exception = await Assert.ThrowsAsync<BinanceExchangeRejectedException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                RequestedEnvironment = ExecutionEnvironment.BinanceTestnet
            },
            CancellationToken.None));

        Assert.Equal("BinanceLeverageConfigurationFailed", exception.FailureCode);
        Assert.Equal(1, harness.PrivateRestClient.EnsureMarginTypeCalls);
        Assert.Equal(1, harness.PrivateRestClient.EnsureLeverageCalls);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_LiveMode_UsesDbBackedCredentialStore()
    {
        await using var harness = await CreateHarnessAsync();

        var result = await harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                RequestedEnvironment = ExecutionEnvironment.Live
            },
            CancellationToken.None);

        Assert.Equal("binance-order-1", result.ExternalOrderId);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal(1, harness.CredentialService.AccessCalls);
        Assert.Equal("api-key", harness.PrivateRestClient.LastPlacementRequest?.ApiKey);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenExchangeAccountOwnerDoesNotMatchCommandOwner()
    {
        await using var harness = await CreateHarnessAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                OwnerUserId = "user-other"
            },
            CancellationToken.None));

        Assert.Contains("owner does not match", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenLimitPriceViolatesTickSize()
    {
        await using var harness = await CreateHarnessAsync();

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                OrderType = ExecutionOrderType.Limit,
                Quantity = 0.002m,
                Price = 65000.15m
            },
            CancellationToken.None));

        Assert.Equal("LimitPriceTickSizeMismatch", exception.ReasonCode);
        Assert.Contains("tick size", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenLimitPriceViolatesPricePrecision()
    {
        await using var harness = await CreateHarnessAsync(
            metadata: CreateMetadata(tickSize: 0.001m, pricePrecision: 2));

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                OrderType = ExecutionOrderType.Limit,
                Quantity = 0.002m,
                Price = 65000.123m
            },
            CancellationToken.None));

        Assert.Equal("LimitPricePrecisionExceeded", exception.ReasonCode);
        Assert.Contains("price precision", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenTradingIsDisabled()
    {
        await using var harness = await CreateHarnessAsync(
            metadata: CreateMetadata(isTradingEnabled: false, tradingStatus: "BREAK"));

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                Quantity = 0.002m,
                Price = 65000m
            },
            CancellationToken.None));

        Assert.Equal("SymbolTradingDisabled", exception.ReasonCode);
        Assert.Contains("not trading-enabled", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenMetadataIsUnavailable()
    {
        await using var harness = await CreateHarnessAsync(
            marketDataService: new NullMarketDataService(),
            exchangeInfoClient: new FakeExchangeInfoClient(null));

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                Quantity = 0.002m,
                Price = 65000m
            },
            CancellationToken.None));

        Assert.Equal("SymbolMetadataUnavailable", exception.ReasonCode);
        Assert.Contains("metadata", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, harness.ExchangeInfoClient.CallCount);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }

    [Fact]
    public async Task DispatchAsync_PlacesOrder_WhenMetadataIsValid()
    {
        await using var harness = await CreateHarnessAsync(
            marketDataService: new NullMarketDataService(),
            exchangeInfoClient: new FakeExchangeInfoClient(CreateMetadata()));

        var result = await harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                Quantity = 0.002m,
                Price = 65000m
            },
            CancellationToken.None);

        Assert.Equal(1, harness.ExchangeInfoClient.CallCount);
        Assert.Equal(1, harness.PrivateRestClient.PlaceOrderCalls);
        Assert.Equal("binance-order-1", result.ExternalOrderId);
    }

    [Fact]
    public async Task DispatchAsync_FailsClosed_WhenDevelopmentPilotFuturesMarginIsInsufficient()
    {
        await using var harness = await CreateHarnessAsync(futuresQuoteAvailableBalance: 98.71811317m);

        var exception = await Assert.ThrowsAsync<ExecutionValidationException>(() => harness.Executor.DispatchAsync(
            harness.Order,
            CreateCommand(harness.ExchangeAccountId) with
            {
                Quantity = 0.002m,
                Price = 51135m
            },
            CancellationToken.None));

        Assert.Equal("FuturesMarginInsufficient", exception.ReasonCode);
        Assert.Contains("available USDT futures margin 98.71811317", exception.Message, StringComparison.Ordinal);
        Assert.Contains("required initial margin 102.27", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.PrivateRestClient.PlaceOrderCalls);
    }
    private static async Task<TestHarness> CreateHarnessAsync(
        SymbolMetadataSnapshot? metadata = null,
        IMarketDataService? marketDataService = null,
        FakeExchangeInfoClient? exchangeInfoClient = null,
        decimal? futuresQuoteAvailableBalance = null,
        IDependencyCircuitBreakerStateManager? dependencyCircuitBreakerStateManager = null,
        BinancePrivateDataOptions? privateDataOptions = null,
        BinanceFuturesTestnetOptions? testnetOptions = null,
        BotExecutionPilotOptions? pilotOptions = null,
        FakeExchangeCredentialService? credentialService = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var exchangeAccountId = Guid.NewGuid();

        dbContext.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = "user-exec",
            ExchangeName = "Binance",
            DisplayName = "Binance Futures",
            IsReadOnly = false,
            CredentialStatus = ExchangeCredentialStatus.Active
        });

        if (futuresQuoteAvailableBalance.HasValue)
        {
            dbContext.ExchangeBalances.Add(new ExchangeBalance
            {
                OwnerUserId = "user-exec",
                ExchangeAccountId = exchangeAccountId,
                Plane = ExchangeDataPlane.Futures,
                Asset = "USDT",
                WalletBalance = futuresQuoteAvailableBalance.Value + 5m,
                CrossWalletBalance = futuresQuoteAvailableBalance.Value,
                AvailableBalance = futuresQuoteAvailableBalance.Value,
                MaxWithdrawAmount = futuresQuoteAvailableBalance.Value,
                ExchangeUpdatedAtUtc = new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc),
                SyncedAtUtc = new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc)
            });
        }
        await dbContext.SaveChangesAsync();

        var order = new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-exec",
            ExchangeAccountId = exchangeAccountId,
            StrategyKey = "pilot-core",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.002m,
            Price = 65000m,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            IdempotencyKey = "exec-test",
            RootCorrelationId = "corr-exec-test",
            LastStateChangedAtUtc = new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc)
        };

        var resolvedMetadata = metadata ?? CreateMetadata();
        var resolvedMarketDataService = marketDataService ?? new FakeMarketDataService(resolvedMetadata);
        var resolvedExchangeInfoClient = exchangeInfoClient ?? new FakeExchangeInfoClient(resolvedMetadata);
        var resolvedPrivateDataOptions = privateDataOptions ?? new BinancePrivateDataOptions
        {
            RestBaseUrl = "https://fapi.binance.com",
            WebSocketBaseUrl = "wss://fstream.binance.com",
            SpotRestBaseUrl = "https://api.binance.com",
            SpotWebSocketBaseUrl = "wss://stream.binance.com:9443"
        };
        var privateRestClient = new FakePrivateRestClient();
        var resolvedCredentialService = credentialService ?? new FakeExchangeCredentialService();
        var resolvedTestnetOptions = testnetOptions ?? new BinanceFuturesTestnetOptions
        {
            BaseUrl = resolvedPrivateDataOptions.RestBaseUrl,
            ApiKey = "testnet-api-key",
            ApiSecret = "testnet-api-secret"
        };
        var executor = new BinanceExecutor(
            dbContext,
            resolvedCredentialService,
            privateRestClient,
            NullLogger<BinanceExecutor>.Instance,
            dependencyCircuitBreakerStateManager,
            resolvedMarketDataService,
            resolvedExchangeInfoClient,
            Options.Create(resolvedPrivateDataOptions),
            Options.Create(resolvedTestnetOptions),
            Options.Create(pilotOptions ?? new BotExecutionPilotOptions()));

        return new TestHarness(
            dbContext,
            executor,
            privateRestClient,
            resolvedExchangeInfoClient,
            exchangeAccountId,
            order,
            resolvedCredentialService);
    }

    private static SymbolMetadataSnapshot CreateMetadata(
        decimal tickSize = 0.1m,
        decimal stepSize = 0.001m,
        decimal? minQuantity = 0.001m,
        decimal? minNotional = 100m,
        int? pricePrecision = 1,
        int? quantityPrecision = 3,
        bool isTradingEnabled = true,
        string tradingStatus = "TRADING")
    {
        return new SymbolMetadataSnapshot(
            "BTCUSDT",
            "Binance",
            "BTC",
            "USDT",
            tickSize,
            stepSize,
            tradingStatus,
            isTradingEnabled,
            new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc))
        {
            MinQuantity = minQuantity,
            MinNotional = minNotional,
            PricePrecision = pricePrecision,
            QuantityPrecision = quantityPrecision
        };
    }

    private static ExecutionCommand CreateCommand(Guid exchangeAccountId)
    {
        return new ExecutionCommand(
            Actor: "system:bot-worker",
            OwnerUserId: "user-exec",
            TradingStrategyId: Guid.NewGuid(),
            TradingStrategyVersionId: Guid.NewGuid(),
            StrategySignalId: Guid.NewGuid(),
            SignalType: StrategySignalType.Entry,
            StrategyKey: "pilot-core",
            Symbol: "BTCUSDT",
            Timeframe: "1m",
            BaseAsset: "BTC",
            QuoteAsset: "USDT",
            Side: ExecutionOrderSide.Buy,
            OrderType: ExecutionOrderType.Market,
            Quantity: 0.002m,
            Price: 65000m,
            ExchangeAccountId: exchangeAccountId,
            IsDemo: false,
            CorrelationId: "corr-exec-test",
            Context: "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1");
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
                        StoredAtUtc: new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
                        LastValidatedAtUtc: new DateTime(2026, 4, 2, 11, 0, 0, DateTimeKind.Utc),
                        LastAccessedAtUtc: new DateTime(2026, 4, 2, 11, 5, 0, DateTimeKind.Utc),
                        LastRotatedAtUtc: new DateTime(2026, 4, 2, 9, 0, 0, DateTimeKind.Utc),
                        RevalidateAfterUtc: new DateTime(2026, 5, 2, 11, 0, 0, DateTimeKind.Utc),
                        RotateAfterUtc: new DateTime(2026, 7, 2, 11, 0, 0, DateTimeKind.Utc))));
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

    private sealed class FakePrivateRestClient : IBinancePrivateRestClient
    {
        public int PlaceOrderCalls { get; private set; }

        public int EnsureMarginTypeCalls { get; private set; }

        public int EnsureLeverageCalls { get; private set; }

        public BinanceOrderPlacementRequest? LastPlacementRequest { get; private set; }

        public Exception? EnsureMarginTypeException { get; set; }

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

            if (EnsureMarginTypeException is not null)
            {
                throw EnsureMarginTypeException;
            }

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
            return Task.FromResult(new BinanceOrderPlacementResult($"binance-order-{PlaceOrderCalls}", request.ClientOrderId, DateTime.UtcNow));
        }

        public Task<BinanceOrderStatusSnapshot> CancelOrderAsync(BinanceOrderCancelRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<BinanceOrderStatusSnapshot> GetOrderAsync(BinanceOrderQueryRequest request, CancellationToken cancellationToken = default)
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
            return ValueTask.FromResult<MarketPriceSnapshot?>(new MarketPriceSnapshot(
                "BTCUSDT",
                65000m,
                DateTime.UtcNow,
                DateTime.UtcNow,
                "UnitTest"));
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

    private sealed class NullMarketDataService : IMarketDataService
    {
        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<MarketPriceSnapshot?>(null);

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<SymbolMetadataSnapshot?>(null);

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }
    }

    private sealed class FakeExchangeInfoClient(SymbolMetadataSnapshot? metadata) : IBinanceExchangeInfoClient
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyCollection<SymbolMetadataSnapshot>> GetSymbolMetadataAsync(
            IReadOnlyCollection<string> symbols,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyCollection<SymbolMetadataSnapshot>>(
                metadata is null ? [] : [metadata]);
        }

        public Task<DateTime?> GetServerTimeUtcAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<DateTime?>(DateTime.UtcNow);
        }
    }

    private sealed class TestDataScopeContext : CoinBot.Application.Abstractions.DataScope.IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class TestHarness(
        ApplicationDbContext dbContext,
        BinanceExecutor executor,
        FakePrivateRestClient privateRestClient,
        FakeExchangeInfoClient exchangeInfoClient,
        Guid exchangeAccountId,
        ExecutionOrder order,
        FakeExchangeCredentialService credentialService) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public BinanceExecutor Executor { get; } = executor;

        public FakePrivateRestClient PrivateRestClient { get; } = privateRestClient;

        public FakeExchangeInfoClient ExchangeInfoClient { get; } = exchangeInfoClient;

        public Guid ExchangeAccountId { get; } = exchangeAccountId;

        public ExecutionOrder Order { get; } = order;

        public FakeExchangeCredentialService CredentialService { get; } = credentialService;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }
    }


    private sealed class RecordingBreakerStateManager : IDependencyCircuitBreakerStateManager
    {
        public int RecordFailureCalls { get; private set; }

        public Task<DependencyCircuitBreakerSnapshot> GetSnapshotAsync(DependencyCircuitBreakerKind breakerKind, CancellationToken cancellationToken = default)
            => Task.FromResult(CreateSnapshot(breakerKind, CircuitBreakerStateCode.Closed));

        public Task<IReadOnlyCollection<DependencyCircuitBreakerSnapshot>> ListSnapshotsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<DependencyCircuitBreakerSnapshot>>(Array.Empty<DependencyCircuitBreakerSnapshot>());

        public Task<DependencyCircuitBreakerSnapshot> RecordFailureAsync(DependencyCircuitBreakerFailureRequest request, CancellationToken cancellationToken = default)
        {
            RecordFailureCalls++;
            return Task.FromResult(CreateSnapshot(request.BreakerKind, CircuitBreakerStateCode.Cooldown));
        }

        public Task<DependencyCircuitBreakerSnapshot> RecordSuccessAsync(DependencyCircuitBreakerSuccessRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CreateSnapshot(request.BreakerKind, CircuitBreakerStateCode.Closed));

        public Task<DependencyCircuitBreakerSnapshot?> TryBeginHalfOpenAsync(DependencyCircuitBreakerHalfOpenRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<DependencyCircuitBreakerSnapshot?>(null);

        private static DependencyCircuitBreakerSnapshot CreateSnapshot(DependencyCircuitBreakerKind breakerKind, CircuitBreakerStateCode stateCode)
            => new(
                breakerKind,
                stateCode,
                ConsecutiveFailureCount: 0,
                LastFailureAtUtc: null,
                LastSuccessAtUtc: null,
                CooldownUntilUtc: null,
                HalfOpenStartedAtUtc: null,
                LastProbeAtUtc: null,
                LastErrorCode: null,
                LastErrorMessage: null,
                CorrelationId: null,
                IsPersisted: false);
    }

}
