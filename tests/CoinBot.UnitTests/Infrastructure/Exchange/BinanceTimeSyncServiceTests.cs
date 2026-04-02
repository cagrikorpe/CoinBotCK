using System.Net;
using System.Text;
using CoinBot.Infrastructure.Exchange;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Exchange;

public sealed class BinanceTimeSyncServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_ComputesOffset_AndReusesCache()
    {
        var now = DateTimeOffset.Parse("2026-04-02T10:00:00Z");
        var timeProvider = new AdjustableTimeProvider(now);
        using var handler = new SequenceServerTimeHandler(
            DateTimeOffset.Parse("2026-04-02T10:00:01.500Z").ToUnixTimeMilliseconds());
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://testnet.binancefuture.com") };
        using var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 16 });
        var service = CreateService(client, memoryCache, timeProvider);

        var synchronizedSnapshot = await service.GetSnapshotAsync();
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var cachedSnapshot = await service.GetSnapshotAsync();

        Assert.Equal("Synchronized", synchronizedSnapshot.StatusCode);
        Assert.Equal(1500, synchronizedSnapshot.OffsetMilliseconds);
        Assert.Equal("Cached", cachedSnapshot.StatusCode);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(1500, cachedSnapshot.OffsetMilliseconds);
    }

    [Fact]
    public async Task GetSnapshotAsync_ForceRefresh_BypassesCache()
    {
        var now = DateTimeOffset.Parse("2026-04-02T10:00:00Z");
        var timeProvider = new AdjustableTimeProvider(now);
        using var handler = new SequenceServerTimeHandler(
            DateTimeOffset.Parse("2026-04-02T10:00:01.500Z").ToUnixTimeMilliseconds(),
            DateTimeOffset.Parse("2026-04-02T09:59:59.250Z").ToUnixTimeMilliseconds());
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://testnet.binancefuture.com") };
        using var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 16 });
        var service = CreateService(client, memoryCache, timeProvider);

        var initialSnapshot = await service.GetSnapshotAsync();
        var refreshedSnapshot = await service.GetSnapshotAsync(forceRefresh: true);

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(1500, initialSnapshot.OffsetMilliseconds);
        Assert.Equal(-750, refreshedSnapshot.OffsetMilliseconds);
    }

    [Fact]
    public async Task GetCurrentTimestampMillisecondsAsync_ReturnsOffsetAdjustedTimestamp()
    {
        var now = DateTimeOffset.Parse("2026-04-02T10:00:00Z");
        var timeProvider = new AdjustableTimeProvider(now);
        using var handler = new SequenceServerTimeHandler(
            DateTimeOffset.Parse("2026-04-02T10:00:01.500Z").ToUnixTimeMilliseconds());
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://testnet.binancefuture.com") };
        using var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 16 });
        var service = CreateService(client, memoryCache, timeProvider);

        var timestamp = await service.GetCurrentTimestampMillisecondsAsync();

        Assert.Equal(now.ToUnixTimeMilliseconds() + 1500, timestamp);
    }

    private static BinanceTimeSyncService CreateService(HttpClient client, IMemoryCache memoryCache, TimeProvider timeProvider)
    {
        return new BinanceTimeSyncService(
            client,
            memoryCache,
            timeProvider,
            Options.Create(new BinancePrivateDataOptions
            {
                RestBaseUrl = "https://testnet.binancefuture.com",
                WebSocketBaseUrl = "wss://fstream.binancefuture.com",
                ServerTimeSyncRefreshSeconds = 30
            }),
            NullLogger<BinanceTimeSyncService>.Instance);
    }

    private sealed class SequenceServerTimeHandler(params long[] serverTimes) : HttpMessageHandler
    {
        private readonly Queue<long> remainingServerTimes = new(serverTimes);

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var serverTime = remainingServerTimes.Count > 0
                ? remainingServerTimes.Dequeue()
                : throw new InvalidOperationException("No more server time responses configured.");

            var payload = $$"""{"serverTime":{{serverTime}}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }
}
