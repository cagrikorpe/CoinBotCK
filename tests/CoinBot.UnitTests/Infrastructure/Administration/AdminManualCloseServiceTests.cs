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
        Assert.Contains("ExecutionIntent=ManualExitCloseOnly", harness.ExecutionEngine.LastCommand.Context, StringComparison.Ordinal);
        Assert.Contains("ManualClose=True", harness.ExecutionEngine.LastCommand.Context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualClose_ShortPosition_DispatchesBuyReduceOnly_ToBinanceTestnet()
    {
        await using var harness = await ManualCloseHarness.CreateAsync(positionQuantity: -0.06m);

        var result = await harness.Service.CloseAsync(harness.CreateRequest());

        Assert.True(result.IsSuccess);
        Assert.Equal(ExecutionOrderSide.Buy, harness.ExecutionEngine.LastCommand!.Side);
        Assert.True(harness.ExecutionEngine.LastCommand.ReduceOnly);
    }

    [Fact]
    public async Task ManualClose_NoOpenPosition_DoesNotDispatch()
    {
        await using var harness = await ManualCloseHarness.CreateAsync(positionQuantity: 0m, seedPosition: false);

        var result = await harness.Service.CloseAsync(harness.CreateRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal("ManualCloseNoOpenPosition", result.OutcomeCode);
        Assert.Null(harness.ExecutionEngine.LastCommand);
    }

    [Fact]
    public async Task ManualClose_PrivatePlaneStale_DoesNotDispatch()
    {
        await using var harness = await ManualCloseHarness.CreateAsync(positionQuantity: 0.06m, stalePrivatePlane: true);

        var result = await harness.Service.CloseAsync(harness.CreateRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal("ManualCloseBlockedPrivatePlaneStale", result.OutcomeCode);
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

    private sealed class ManualCloseHarness : IAsyncDisposable
    {
        private ManualCloseHarness(
            ApplicationDbContext dbContext,
            AdminManualCloseService service,
            FakeExecutionEngine executionEngine,
            Guid botId)
        {
            DbContext = dbContext;
            Service = service;
            ExecutionEngine = executionEngine;
            BotId = botId;
        }

        public ApplicationDbContext DbContext { get; }

        public AdminManualCloseService Service { get; }

        public FakeExecutionEngine ExecutionEngine { get; }

        public Guid BotId { get; }

        public AdminManualCloseRequest CreateRequest()
        {
            return new AdminManualCloseRequest(BotId, "admin-01", "admin:admin-01", "corr-manual-close");
        }

        public ValueTask DisposeAsync() => DbContext.DisposeAsync();

        public static async Task<ManualCloseHarness> CreateAsync(
            decimal positionQuantity,
            bool seedPosition = true,
            bool stalePrivatePlane = false,
            bool readOnlyAccount = false,
            bool mismatchedAccountOwner = false,
            ExchangeCredentialStatus credentialStatus = ExchangeCredentialStatus.Active,
            ExecutionEnvironment effectiveMode = ExecutionEnvironment.BinanceTestnet)
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

            return new ManualCloseHarness(dbContext, service, executionEngine, botId);
        }
    }

    private sealed class FakeExecutionEngine : IExecutionEngine
    {
        public ExecutionCommand? LastCommand { get; private set; }

        public Task<ExecutionDispatchResult> DispatchAsync(ExecutionCommand command, CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            return Task.FromResult(
                new ExecutionDispatchResult(
                    new ExecutionOrderSnapshot(
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
                        "manual-close",
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
                        Array.Empty<ExecutionOrderTransitionSnapshot>()),
                    false));
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
