using System.Collections.Concurrent;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Web.Hubs;
using CoinBot.Web.ViewModels.Home;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Web;

public sealed class MarketDataHubBridgeServiceTests
{
    [Fact]
    public async Task BridgeService_ForwardsInternalMarketPriceStream_ToSignalRGroupWithMetadata()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 });
        var cachePolicyProvider = new MarketDataCachePolicyProvider(Options.Create(new InMemoryCacheOptions
        {
            SizeLimit = 64,
            SymbolMetadataTtlMinutes = 60,
            LatestPriceTtlSeconds = 15
        }));
        var symbolRegistry = new SharedSymbolRegistry(
            memoryCache,
            cachePolicyProvider,
            NullLogger<SharedSymbolRegistry>.Instance);
        var streamHub = new MarketPriceStreamHub();
        var hubContext = new TestHubContext();
        var service = new MarketDataHubBridgeService(
            streamHub,
            symbolRegistry,
            hubContext,
            NullLogger<MarketDataHubBridgeService>.Instance);
        symbolRegistry.Upsert(
        [
            new SymbolMetadataSnapshot(
                "BTCUSDT",
                "Binance",
                "BTC",
                "USDT",
                0.01m,
                0.0001m,
                "TRADING",
                true,
                At(0))
        ]);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        streamHub.Publish(new MarketPriceSnapshot("BTCUSDT", 64020.75m, At(0), At(0), "unit-test"));

        var invocation = await hubContext.WaitForGroupInvocationAsync("market-data:BTCUSDT");

        await service.StopAsync(CancellationToken.None);

        var payload = Assert.IsType<DashboardMarketTickerViewModel>(Assert.Single(invocation.Arguments));
        Assert.Equal("marketPriceUpdated", invocation.MethodName);
        Assert.Equal("BTCUSDT", payload.Symbol);
        Assert.Equal(64020.75m, payload.Price);
        Assert.Equal(0.0001m, payload.StepSize);
        Assert.Equal("TRADING", payload.TradingStatus);
        Assert.True(payload.IsTradingEnabled);
    }

    private static DateTime At(int minuteOffset)
    {
        return new DateTime(2026, 3, 23, 9, minuteOffset, 0, DateTimeKind.Utc);
    }

    private sealed class TestHubContext : IHubContext<MarketDataHub>
    {
        private readonly ConcurrentDictionary<string, TestClientProxy> proxies = new(StringComparer.Ordinal);

        public IHubClients Clients => new TestHubClients(proxies);

        public IGroupManager Groups { get; } = new TestGroupManager();

        public async Task<GroupInvocation> WaitForGroupInvocationAsync(string groupName)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (proxies.TryGetValue(groupName, out var proxy) && proxy.Invocations.TryPeek(out var invocation))
                {
                    return invocation;
                }

                await Task.Delay(20);
            }

            throw new TimeoutException($"No invocation captured for {groupName}.");
        }
    }

    private sealed class TestHubClients(ConcurrentDictionary<string, TestClientProxy> proxies) : IHubClients
    {
        private readonly TestClientProxy defaultProxy = new();

        public IClientProxy All => defaultProxy;

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => defaultProxy;

        public IClientProxy Client(string connectionId) => defaultProxy;

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => defaultProxy;

        public IClientProxy Group(string groupName) => proxies.GetOrAdd(groupName, static _ => new TestClientProxy());

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Group(groupName);

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => defaultProxy;

        public IClientProxy User(string userId) => defaultProxy;

        public IClientProxy Users(IReadOnlyList<string> userIds) => defaultProxy;
    }

    private sealed class TestGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class TestClientProxy : IClientProxy
    {
        public ConcurrentQueue<GroupInvocation> Invocations { get; } = new();

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Invocations.Enqueue(new GroupInvocation(method, args));
            return Task.CompletedTask;
        }
    }

    private sealed record GroupInvocation(string MethodName, object?[] Arguments);
}
