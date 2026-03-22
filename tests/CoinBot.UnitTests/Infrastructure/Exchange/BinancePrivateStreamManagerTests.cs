using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Exchange;

public sealed class BinancePrivateStreamManagerTests
{
    [Fact]
    public async Task RunSessionCycleAsync_PublishesSeedAndUpdatedSnapshot_AndRequestsReconnectWhenListenKeyExpires()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        var databaseName = Guid.NewGuid().ToString("N");
        var exchangeAccountId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(now);
        var seedSnapshot = CreateSnapshot(
            exchangeAccountId,
            walletBalance: 100m,
            quantity: 1m,
            observedAtUtc: now.UtcDateTime);
        var updatedEventTimeUtc = now.UtcDateTime.AddSeconds(5);
        var restClient = new FakePrivateRestClient(seedSnapshot);
        var streamClient = new FakePrivateStreamClient(
        [
            new BinancePrivateStreamEvent(
                "ACCOUNT_UPDATE",
                updatedEventTimeUtc,
                [
                    new ExchangeBalanceSnapshot(
                        "USDT",
                        120m,
                        120m,
                        AvailableBalance: null,
                        MaxWithdrawAmount: null,
                        updatedEventTimeUtc)
                ],
                [
                    new ExchangePositionSnapshot(
                        "BTCUSDT",
                        "LONG",
                        2m,
                        51000m,
                        51000m,
                        15m,
                        "cross",
                        0m,
                        updatedEventTimeUtc)
                ]),
            new BinancePrivateStreamEvent("listenKeyExpired", updatedEventTimeUtc.AddSeconds(1), [], [])
        ]);
        var snapshotHub = new ExchangeAccountSnapshotHub();
        using var provider = BuildProvider(databaseName, databaseRoot, new FakeExchangeCredentialService(exchangeAccountId));
        var manager = new BinancePrivateStreamManager(
            provider.GetRequiredService<IServiceScopeFactory>(),
            restClient,
            streamClient,
            snapshotHub,
            Options.Create(new BinancePrivateDataOptions
            {
                Enabled = true,
                RestBaseUrl = "https://fapi.binance.com",
                WebSocketBaseUrl = "wss://fstream.binance.com",
                SessionScanIntervalSeconds = 15,
                ReconnectDelaySeconds = 1,
                ListenKeyRenewalIntervalMinutes = 30,
                ReconciliationIntervalMinutes = 5,
                RecvWindowMilliseconds = 5000
            }),
            timeProvider,
            NullLogger<BinancePrivateStreamManager>.Instance);
        var account = new ExchangeSyncAccountDescriptor(exchangeAccountId, "user-private", "Binance");

        await using var enumerator = snapshotHub
            .SubscribeAsync()
            .GetAsyncEnumerator();
        var firstMoveNext = enumerator.MoveNextAsync().AsTask();
        var runTask = manager.RunSessionCycleAsync(account);

        Assert.True(await firstMoveNext);
        var firstSnapshot = enumerator.Current;

        var secondMoveNext = enumerator.MoveNextAsync().AsTask();
        var cycleResult = await runTask;

        Assert.True(await secondMoveNext);
        var secondSnapshot = enumerator.Current;

        Assert.NotNull(cycleResult);
        Assert.True(cycleResult!.ShouldReconnect);
        Assert.Equal(ExchangePrivateStreamConnectionState.ListenKeyExpired, cycleResult.ConnectionState);
        Assert.Equal("ListenKeyExpired", cycleResult.ErrorCode);
        Assert.Equal(1, restClient.StartListenKeyCalls);
        Assert.Equal(1, restClient.CloseListenKeyCalls);
        Assert.Equal(100m, Assert.Single(firstSnapshot.Balances).WalletBalance);
        Assert.Equal(120m, Assert.Single(secondSnapshot.Balances).WalletBalance);
        Assert.Equal(2m, Assert.Single(secondSnapshot.Positions).Quantity);

        await using var scope = provider.CreateAsyncScope();
        using var bypass = scope.ServiceProvider
            .GetRequiredService<IDataScopeContextAccessor>()
            .BeginScope(hasIsolationBypass: true);
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var state = await dbContext.ExchangeAccountSyncStates.SingleAsync(entity => entity.ExchangeAccountId == exchangeAccountId);

        Assert.Equal(ExchangePrivateStreamConnectionState.Connected, state.PrivateStreamConnectionState);
        Assert.NotNull(state.LastListenKeyStartedAtUtc);
        Assert.Equal(updatedEventTimeUtc, state.LastPrivateStreamEventAtUtc);
    }

    private static ExchangeAccountSnapshot CreateSnapshot(
        Guid exchangeAccountId,
        decimal walletBalance,
        decimal quantity,
        DateTime observedAtUtc)
    {
        return new ExchangeAccountSnapshot(
            exchangeAccountId,
            "user-private",
            "Binance",
            [
                new ExchangeBalanceSnapshot(
                    "USDT",
                    walletBalance,
                    walletBalance,
                    walletBalance,
                    walletBalance,
                    observedAtUtc)
            ],
            [
                new ExchangePositionSnapshot(
                    "BTCUSDT",
                    "LONG",
                    quantity,
                    50000m,
                    50000m,
                    10m,
                    "cross",
                    0m,
                    observedAtUtc)
            ],
            observedAtUtc,
            observedAtUtc,
            "Binance.PrivateRest.Account");
    }

    private static ServiceProvider BuildProvider(
        string databaseName,
        InMemoryDatabaseRoot databaseRoot,
        IExchangeCredentialService exchangeCredentialService)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddScoped<IDataScopeContextAccessor, DataScopeContextAccessor>();
        services.AddScoped<IDataScopeContext>(serviceProvider => serviceProvider.GetRequiredService<IDataScopeContextAccessor>());
        services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(databaseName, databaseRoot));
        services.AddScoped(_ => exchangeCredentialService);
        services.AddScoped<ExchangeAccountSyncStateService>();

        return services.BuildServiceProvider();
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

    private sealed class FakePrivateRestClient(ExchangeAccountSnapshot snapshot) : IBinancePrivateRestClient
    {
        public int StartListenKeyCalls { get; private set; }

        public int CloseListenKeyCalls { get; private set; }

        public Task<string> StartListenKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            StartListenKeyCalls++;
            return Task.FromResult("listen-key");
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

        public Task<ExchangeAccountSnapshot> GetAccountSnapshotAsync(
            Guid exchangeAccountId,
            string ownerUserId,
            string exchangeName,
            string apiKey,
            string apiSecret,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(snapshot);
        }
    }

    private sealed class FakePrivateStreamClient(IReadOnlyCollection<BinancePrivateStreamEvent> events) : IBinancePrivateStreamClient
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
