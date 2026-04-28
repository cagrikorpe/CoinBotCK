using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Administration;

public sealed class AdminManualCloseServiceTests
{
    [Fact]
    public async Task ManualClose_LongPosition_DispatchesSellReduceOnly_ToBinanceTestnet()
    {
        await using var harness = await ManualCloseHarness.CreateAsync(positionQuantity: 0.06m);

        var result = await harness.Service.CloseAsync(harness.CreateRequest());

        Assert.True(result.IsSuccess);
        Assert.NotNull(harness.ExecutionEngine.LastCommand);
        Assert.Equal(ExecutionOrderSide.Sell, harness.ExecutionEngine.LastCommand!.Side);
        Assert.True(harness.ExecutionEngine.LastCommand.ReduceOnly);
        Assert.Equal(ExecutionEnvironment.BinanceTestnet, harness.ExecutionEngine.LastCommand.RequestedEnvironment);
        Assert.Contains("SignalType=Exit", harness.ExecutionEngine.LastCommand.Context, StringComparison.Ordinal);
        Assert.Contains("ExecutionIntent=ManualExitCloseOnly", harness.ExecutionEngine.LastCommand.Context, StringComparison.Ordinal);
        Assert.Contains("ExitIntent=ManualExitCloseOnly", harness.ExecutionEngine.LastCommand.Context, StringComparison.Ordinal);
        Assert.Contains("ExitSource=Manual", harness.ExecutionEngine.LastCommand.Context, StringComparison.Ordinal);
        Assert.Contains("ReverseEntryConvertedToCloseOnly=False", harness.ExecutionEngine.LastCommand.Context, StringComparison.Ordinal);
        Assert.Contains("ManualClose=True", harness.ExecutionEngine.LastCommand.Context, StringComparison.Ordinal);
        Assert.Contains($"ExchangeAccountId={harness.ExchangeAccountId:D}", harness.ExecutionEngine.LastCommand.Context, StringComparison.Ordinal);
        Assert.Contains("ManualClose=True", result.Summary, StringComparison.Ordinal);
        Assert.Contains("ExitSource=Manual", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualClose_ShortPosition_DispatchesBuyReduceOnly_ToBinanceTestnet()
    {
        await using var harness = await ManualCloseHarness.CreateAsync(positionQuantity: -0.06m);

        var result = await harness.Service.CloseAsync(harness.CreateRequest());

        Assert.True(result.IsSuccess);
        Assert.Equal(ExecutionOrderSide.Buy, harness.ExecutionEngine.LastCommand!.Side);
        Assert.True(harness.ExecutionEngine.LastCommand.ReduceOnly);
        Assert.Contains("ExitSource=Manual", harness.ExecutionEngine.LastCommand.Context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualClose_NoOpenPosition_DoesNotDispatch()
    {
        await using var harness = await ManualCloseHarness.CreateAsync(positionQuantity: 0m, seedPosition: false);

        var result = await harness.Service.CloseAsync(harness.CreateRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal("ManualCloseNoOpenPosition", result.OutcomeCode);
        Assert.Contains("SignalType=Exit", result.Summary, StringComparison.Ordinal);
        Assert.Contains("ExitSource=Manual", result.Summary, StringComparison.Ordinal);
        Assert.Contains("ReverseEntryConvertedToCloseOnly=False", result.Summary, StringComparison.Ordinal);
        Assert.Null(harness.ExecutionEngine.LastCommand);
    }

    [Fact]
    public async Task ManualClose_MissingExchangeAccountScope_DoesNotDispatch()
    {
        await using var harness = await ManualCloseHarness.CreateAsync(positionQuantity: 0.06m);

        var result = await harness.Service.CloseAsync(harness.CreateRequest(omitExchangeAccountId: true));

        Assert.False(result.IsSuccess);
        Assert.Equal("ManualCloseAccountScopeMissing", result.OutcomeCode);
        Assert.Contains("ExchangeAccountId=missing", result.Summary, StringComparison.Ordinal);
        Assert.Null(harness.ExecutionEngine.LastCommand);
    }

    [Fact]
    public async Task ManualClose_AccountScopeMismatch_DoesNotDispatch()
    {
        await using var harness = await ManualCloseHarness.CreateAsync(positionQuantity: 0.06m);

        var result = await harness.Service.CloseAsync(harness.CreateRequest(exchangeAccountId: Guid.NewGuid()));

        Assert.False(result.IsSuccess);
        Assert.Equal("ManualCloseAccountScopeMismatch", result.OutcomeCode);
        Assert.Null(harness.ExecutionEngine.LastCommand);
    }

    [Fact]
    public async Task ManualClose_PrivatePlaneStale_DoesNotDispatch()
    {
        await using var harness = await ManualCloseHarness.CreateAsync(positionQuantity: 0.06m, stalePrivatePlane: true);

        var result = await harness.Service.CloseAsync(harness.CreateRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal("ManualCloseBlockedPrivatePlaneStale", result.OutcomeCode);
        Assert.Contains("SignalType=Exit", result.Summary, StringComparison.Ordinal);
        Assert.Contains("ExitSource=Manual", result.Summary, StringComparison.Ordinal);
        Assert.Contains("ReverseEntryConvertedToCloseOnly=False", result.Summary, StringComparison.Ordinal);
        Assert.Contains("PrivateStreamState=Disconnected", result.Summary, StringComparison.Ordinal);
        Assert.Null(harness.ExecutionEngine.LastCommand);
    }

    [Fact]
    public async Task ManualClose_ReadOnlyAccount_DoesNotDispatch()
    {
        await using var harness = await ManualCloseHarness.CreateAsync(positionQuantity: 0.06m, readOnlyAccount: true);

        var result = await harness.Service.CloseAsync(harness.CreateRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal("ManualCloseReadOnlyAccount", result.OutcomeCode);
        Assert.Null(harness.ExecutionEngine.LastCommand);
    }

    [Fact]
    public async Task ManualClose_OwnershipMismatch_DoesNotDispatch()
    {
        await using var harness = await ManualCloseHarness.CreateAsync(positionQuantity: 0.06m, mismatchedAccountOwner: true);

        var result = await harness.Service.CloseAsync(harness.CreateRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal("ManualCloseOwnershipMismatch", result.OutcomeCode);
        Assert.Null(harness.ExecutionEngine.LastCommand);
    }

    [Fact]
    public async Task ManualClose_InactiveCredential_DoesNotDispatch()
    {
        await using var harness = await ManualCloseHarness.CreateAsync(
            positionQuantity: 0.06m,
            credentialStatus: ExchangeCredentialStatus.Invalid);

        var result = await harness.Service.CloseAsync(harness.CreateRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal("ManualCloseCredentialUnavailable", result.OutcomeCode);
        Assert.Null(harness.ExecutionEngine.LastCommand);
    }

    [Fact]
    public async Task ManualClose_NonTestnetEnvironment_DoesNotDispatch()
    {
        await using var harness = await ManualCloseHarness.CreateAsync(
            positionQuantity: 0.06m,
            effectiveMode: ExecutionEnvironment.Demo);

        var result = await harness.Service.CloseAsync(harness.CreateRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal("ManualCloseEnvironmentInvalid", result.OutcomeCode);
        Assert.Null(harness.ExecutionEngine.LastCommand);
    }

    [Fact]
    public async Task ManualClose_LiveModeWithBinanceTestnetExecutionEvidence_DispatchesReduceOnlyClose()
    {
        await using var harness = await ManualCloseHarness.CreateAsync(
            positionQuantity: 0.14m,
            effectiveMode: ExecutionEnvironment.Live,
            seedBrokerBackedTestnetExecutionEvidence: true);

        var result = await harness.Service.CloseAsync(harness.CreateRequest());

        Assert.True(result.IsSuccess);
        Assert.NotNull(harness.ExecutionEngine.LastCommand);
        Assert.Equal(ExecutionOrderSide.Sell, harness.ExecutionEngine.LastCommand!.Side);
        Assert.True(harness.ExecutionEngine.LastCommand.ReduceOnly);
        Assert.Equal(ExecutionEnvironment.BinanceTestnet, harness.ExecutionEngine.LastCommand.RequestedEnvironment);
        Assert.Equal(1, harness.ExecutionEngine.DispatchCalls);
    }

    [Fact]
    public async Task ManualClose_ExplicitAccountScope_ClosesOnlySelectedAccount_WhenMirroredPositionExists()
    {
        await using var harness = await ManualCloseHarness.CreateAsync(
            positionQuantity: 0.14m,
            seedMirroredPosition: true);

        var result = await harness.Service.CloseAsync(harness.CreateRequest());

        Assert.True(result.IsSuccess);
        Assert.NotNull(harness.ExecutionEngine.LastCommand);
        Assert.Equal(harness.ExchangeAccountId, harness.ExecutionEngine.LastCommand!.ExchangeAccountId);
        Assert.Equal(ExecutionOrderSide.Sell, harness.ExecutionEngine.LastCommand.Side);
        Assert.Equal(1, harness.ExecutionEngine.DispatchCalls);
    }

    [Fact]
    public async Task ManualClose_DoubleRequest_SubmitsAtMostOnce()
    {
        await using var harness = await ManualCloseHarness.CreateAsync(positionQuantity: -0.14m);
        harness.ExecutionEngine.SuppressDuplicateRequests = true;

        var first = await harness.Service.CloseAsync(harness.CreateRequest());
        var second = await harness.Service.CloseAsync(harness.CreateRequest());

        Assert.True(first.IsSuccess);
        Assert.True(second.IsDuplicate);
        Assert.Equal("ManualCloseSubmitted", second.OutcomeCode);
        Assert.Equal(1, harness.ExecutionEngine.DispatchCalls);
    }

    private sealed class ManualCloseHarness : IAsyncDisposable
    {
        private ManualCloseHarness(
            ApplicationDbContext dbContext,
            AdminManualCloseService service,
            FakeExecutionEngine executionEngine,
            Guid botId,
            Guid exchangeAccountId)
        {
            DbContext = dbContext;
            Service = service;
            ExecutionEngine = executionEngine;
            BotId = botId;
            ExchangeAccountId = exchangeAccountId;
        }

        public ApplicationDbContext DbContext { get; }

        public AdminManualCloseService Service { get; }

        public FakeExecutionEngine ExecutionEngine { get; }

        public Guid BotId { get; }

        public Guid ExchangeAccountId { get; }

        public AdminManualCloseRequest CreateRequest(Guid? exchangeAccountId = null, string? symbol = "SOLUSDT", bool omitExchangeAccountId = false)
        {
            return new AdminManualCloseRequest(
                BotId,
                omitExchangeAccountId ? null : exchangeAccountId ?? ExchangeAccountId,
                symbol,
                "admin-01",
                "admin:admin-01",
                "corr-manual-close");
        }

        public ValueTask DisposeAsync() => DbContext.DisposeAsync();

        public static async Task<ManualCloseHarness> CreateAsync(
            decimal positionQuantity,
            bool seedPosition = true,
            bool stalePrivatePlane = false,
            bool readOnlyAccount = false,
            bool mismatchedAccountOwner = false,
            ExchangeCredentialStatus credentialStatus = ExchangeCredentialStatus.Active,
            ExecutionEnvironment effectiveMode = ExecutionEnvironment.BinanceTestnet,
            bool seedBrokerBackedTestnetExecutionEvidence = false,
            bool seedMirroredPosition = false)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
            var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
            var now = new DateTime(2026, 4, 27, 12, 0, 0, DateTimeKind.Utc);
            var exchangeAccountId = Guid.NewGuid();
            var botId = Guid.NewGuid();

            dbContext.Users.Add(new ApplicationUser
            {
                Id = "user-1",
                UserName = "user-1",
                TradingModeOverride = ExecutionEnvironment.BinanceTestnet
            });
            dbContext.ExchangeAccounts.Add(new ExchangeAccount
            {
                Id = exchangeAccountId,
                OwnerUserId = mismatchedAccountOwner ? "other-user" : "user-1",
                ExchangeName = "Binance",
                DisplayName = "Binance Testnet",
                IsReadOnly = readOnlyAccount,
                CredentialStatus = credentialStatus,
                CreatedDate = now,
                UpdatedDate = now
            });
            dbContext.TradingBots.Add(new TradingBot
            {
                Id = botId,
                OwnerUserId = "user-1",
                Name = "Manual Close Bot",
                StrategyKey = "manual-close",
                Symbol = "SOLUSDT",
                ExchangeAccountId = exchangeAccountId,
                IsEnabled = true,
                Leverage = 1,
                MarginType = "ISOLATED",
                CreatedDate = now,
                UpdatedDate = now
            });
            dbContext.ExchangeAccountSyncStates.Add(new ExchangeAccountSyncState
            {
                Id = Guid.NewGuid(),
                OwnerUserId = "user-1",
                ExchangeAccountId = exchangeAccountId,
                Plane = ExchangeDataPlane.Futures,
                PrivateStreamConnectionState = stalePrivatePlane ? ExchangePrivateStreamConnectionState.Disconnected : ExchangePrivateStreamConnectionState.Connected,
                DriftStatus = stalePrivatePlane ? ExchangeStateDriftStatus.Unknown : ExchangeStateDriftStatus.InSync,
                LastPrivateStreamEventAtUtc = stalePrivatePlane ? now.AddMinutes(-20) : now,
                LastPositionSyncedAtUtc = stalePrivatePlane ? now.AddMinutes(-20) : now,
                LastBalanceSyncedAtUtc = stalePrivatePlane ? now.AddMinutes(-20) : now,
                CreatedDate = now,
                UpdatedDate = now
            });

            if (seedPosition)
            {
                dbContext.ExchangePositions.Add(new ExchangePosition
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = "user-1",
                    ExchangeAccountId = exchangeAccountId,
                    Plane = ExchangeDataPlane.Futures,
                    Symbol = "SOLUSDT",
                    PositionSide = "BOTH",
                    Quantity = positionQuantity,
                    EntryPrice = 100m,
                    BreakEvenPrice = 100m,
                    ExchangeUpdatedAtUtc = now,
                    SyncedAtUtc = now,
                    CreatedDate = now,
                    UpdatedDate = now
                });
            }

            if (seedMirroredPosition)
            {
                dbContext.ExchangePositions.Add(new ExchangePosition
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = "mirror-user",
                    ExchangeAccountId = Guid.NewGuid(),
                    Plane = ExchangeDataPlane.Futures,
                    Symbol = "SOLUSDT",
                    PositionSide = "BOTH",
                    Quantity = -0.22m,
                    EntryPrice = 88m,
                    BreakEvenPrice = 88m,
                    ExchangeUpdatedAtUtc = now,
                    SyncedAtUtc = now,
                    CreatedDate = now,
                    UpdatedDate = now
                });
            }

            if (seedBrokerBackedTestnetExecutionEvidence)
            {
                dbContext.ExecutionOrders.Add(new ExecutionOrder
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = "user-1",
                    TradingStrategyId = Guid.NewGuid(),
                    TradingStrategyVersionId = Guid.NewGuid(),
                    StrategySignalId = Guid.NewGuid(),
                    SignalType = StrategySignalType.Entry,
                    BotId = botId,
                    ExchangeAccountId = exchangeAccountId,
                    Plane = ExchangeDataPlane.Futures,
                    StrategyKey = "manual-close",
                    Symbol = "SOLUSDT",
                    Timeframe = "1m",
                    BaseAsset = "SOL",
                    QuoteAsset = "USDT",
                    Side = ExecutionOrderSide.Buy,
                    OrderType = ExecutionOrderType.Market,
                    Quantity = Math.Abs(positionQuantity),
                    Price = 100m,
                    ReduceOnly = false,
                    ExecutionEnvironment = ExecutionEnvironment.BinanceTestnet,
                    ExecutorKind = ExecutionOrderExecutorKind.BinanceTestnet,
                    State = ExecutionOrderState.Filled,
                    SubmittedToBroker = true,
                    SubmittedAtUtc = now,
                    IdempotencyKey = $"seed-manual-close-{Guid.NewGuid():N}",
                    RootCorrelationId = "seed-manual-close-corr",
                    ExternalOrderId = "seed-manual-close-ext",
                    LastStateChangedAtUtc = now
                });
            }

            await dbContext.SaveChangesAsync();

            var executionEngine = new FakeExecutionEngine();
            var service = new AdminManualCloseService(
                dbContext,
                executionEngine,
                new FakeTradingModeResolver(effectiveMode),
                new FakeMarketDataService(),
                Options.Create(new BotExecutionPilotOptions
                {
                    PrivatePlaneFreshnessThresholdSeconds = 15
                }),
                new FixedTimeProvider(now));

            return new ManualCloseHarness(dbContext, service, executionEngine, botId, exchangeAccountId);
        }
    }

    private sealed class FakeExecutionEngine : IExecutionEngine
    {
        private readonly Dictionary<string, ExecutionOrderSnapshot> snapshotsByIdempotencyKey = new(StringComparer.Ordinal);

        public ExecutionCommand? LastCommand { get; private set; }

        public int DispatchCalls { get; private set; }

        public bool SuppressDuplicateRequests { get; set; }

        public Task<ExecutionDispatchResult> DispatchAsync(ExecutionCommand command, CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            var idempotencyKey = command.IdempotencyKey ?? Guid.NewGuid().ToString("N");

            if (SuppressDuplicateRequests &&
                snapshotsByIdempotencyKey.TryGetValue(idempotencyKey, out var existingSnapshot))
            {
                return Task.FromResult(new ExecutionDispatchResult(existingSnapshot, true));
            }

            DispatchCalls++;
            var snapshot = new ExecutionOrderSnapshot(
                Guid.NewGuid(),
                command.TradingStrategyId,
                command.TradingStrategyVersionId,
                command.StrategySignalId,
                command.SignalType,
                command.BotId,
                command.ExchangeAccountId,
                command.StrategyKey,
                command.Symbol,
                command.Timeframe,
                command.BaseAsset,
                command.QuoteAsset,
                command.Side,
                command.OrderType,
                command.Quantity,
                command.Price,
                0m,
                null,
                null,
                null,
                null,
                true,
                null,
                ExecutionEnvironment.BinanceTestnet,
                ExecutionOrderExecutorKind.BinanceTestnet,
                ExecutionOrderState.Submitted,
                idempotencyKey,
                "corr-manual-close",
                null,
                "ext-1",
                null,
                null,
                ExecutionRejectionStage.None,
                true,
                false,
                false,
                false,
                false,
                false,
                null,
                DateTime.UtcNow,
                null,
                ExchangeStateDriftStatus.Unknown,
                null,
                null,
                DateTime.UtcNow,
                Array.Empty<ExecutionOrderTransitionSnapshot>());
            snapshotsByIdempotencyKey[idempotencyKey] = snapshot;

            return Task.FromResult(new ExecutionDispatchResult(snapshot, false));
        }
    }

    private sealed class FakeMarketDataService : IMarketDataService
    {
        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<MarketPriceSnapshot?>(new MarketPriceSnapshot(symbol, 101m, DateTime.UtcNow, DateTime.UtcNow, "test"));
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(
                new SymbolMetadataSnapshot(symbol, "Binance", "SOL", "USDT", 0.01m, 0.01m, "TRADING", true, DateTime.UtcNow)
                {
                    MinQuantity = 0.01m,
                    MinNotional = 5m,
                    PricePrecision = 2,
                    QuantityPrecision = 2
                });
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(IEnumerable<string> symbols, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        private readonly DateTimeOffset current = new(utcNow, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => current;
    }

    private sealed class FakeTradingModeResolver(ExecutionEnvironment effectiveMode) : ITradingModeResolver
    {
        public Task<TradingModeResolution> ResolveAsync(TradingModeResolutionRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TradingModeResolution(
                ExecutionEnvironment.Demo,
                effectiveMode,
                null,
                null,
                effectiveMode,
                TradingModeResolutionSource.UserOverride,
                "Testnet",
                HasExplicitLiveApproval: true));
        }
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }
}
