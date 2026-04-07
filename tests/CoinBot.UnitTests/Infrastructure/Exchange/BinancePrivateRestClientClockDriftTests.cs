using System.Net;
using System.Text;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Exchange;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Exchange;

public sealed class BinancePrivateRestClientClockDriftTests
{
    [Fact]
    public async Task PlaceOrderAsync_UsesTimeSyncTimestampInSignedRequest()
    {
        using var handler = new RecordingMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"orderId":"1001","clientOrderId":"cbp0_test"}""", Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://testnet.binancefuture.com") };
        var timeSyncService = new FakeTimeSyncService(currentTimestampMilliseconds: 1_710_000_000_123L);
        var sut = CreateClient(client, timeSyncService);

        var result = await sut.PlaceOrderAsync(CreateRequest());

        Assert.Equal("1001", result.OrderId);
        Assert.NotNull(handler.LastRequestUri);
        Assert.Contains("timestamp=1710000000123", handler.LastRequestUri!, StringComparison.Ordinal);
        Assert.Equal(1, timeSyncService.CurrentTimestampCalls);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task PlaceOrderAsync_ThrowsClockDriftException_WhenExchangeRejectsTimestamp()
    {
        using var handler = new RecordingMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"code":-1021,"msg":"Timestamp for this request is outside of the recvWindow."}""", Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://testnet.binancefuture.com") };
        var timeSyncService = new FakeTimeSyncService(
            currentTimestampMilliseconds: 1_710_000_000_123L,
            refreshedSnapshot: new BinanceTimeSyncSnapshot(
                new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 4, 2, 9, 59, 57, 500, DateTimeKind.Utc),
                -2500,
                30,
                new DateTime(2026, 4, 2, 10, 0, 1, DateTimeKind.Utc),
                "Synchronized",
                null));
        var sut = CreateClient(client, timeSyncService);

        var exception = await Assert.ThrowsAsync<BinanceClockDriftException>(() => sut.PlaceOrderAsync(CreateRequest()));

        Assert.Contains("DriftMs=2500", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, timeSyncService.ForcedRefreshCalls);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task PlaceOrderAsync_FailsClosed_WhenTimeSyncOffsetCannotBeSynchronized()
    {
        using var handler = new RecordingMessageHandler(_ => throw new InvalidOperationException("HTTP request should not be sent when timestamp sync is unavailable."));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://testnet.binancefuture.com") };
        var timeSyncService = new FakeTimeSyncService(
            currentTimestampMilliseconds: 0,
            currentTimestampException: new BinanceClockDriftException("Offset unavailable."));
        var sut = CreateClient(client, timeSyncService);

        await Assert.ThrowsAsync<BinanceClockDriftException>(() => sut.PlaceOrderAsync(CreateRequest()));

        Assert.Equal(1, timeSyncService.CurrentTimestampCalls);
        Assert.Equal(0, handler.RequestCount);
    }

    private static BinancePrivateRestClient CreateClient(HttpClient httpClient, IBinanceTimeSyncService timeSyncService)
    {
        return new BinancePrivateRestClient(
            httpClient,
            Options.Create(new BinancePrivateDataOptions
            {
                RestBaseUrl = "https://testnet.binancefuture.com",
                WebSocketBaseUrl = "wss://fstream.binancefuture.com",
                RecvWindowMilliseconds = 5000
            }),
            new AdjustableTimeProvider(DateTimeOffset.Parse("2026-04-02T10:00:00Z")),
            timeSyncService,
            NullLogger<BinancePrivateRestClient>.Instance);
    }

    private static BinanceOrderPlacementRequest CreateRequest()
    {
        return new BinanceOrderPlacementRequest(
            Guid.NewGuid(),
            "BTCUSDT",
            ExecutionOrderSide.Buy,
            ExecutionOrderType.Market,
            0.001m,
            0m,
            "cbp0_test",
            "api-key",
            "api-secret");
    }

    private sealed class FakeTimeSyncService(
        long currentTimestampMilliseconds,
        BinanceTimeSyncSnapshot? refreshedSnapshot = null,
        Exception? currentTimestampException = null) : IBinanceTimeSyncService
    {
        public int CurrentTimestampCalls { get; private set; }

        public int ForcedRefreshCalls { get; private set; }

        private readonly BinanceTimeSyncSnapshot refreshedSnapshotValue = refreshedSnapshot ?? new BinanceTimeSyncSnapshot(
            new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
            0,
            10,
            new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
            "Synchronized",
            null);

        public Task<BinanceTimeSyncSnapshot> GetSnapshotAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            if (forceRefresh)
            {
                ForcedRefreshCalls++;
            }

            return Task.FromResult(refreshedSnapshotValue);
        }

        public Task<long> GetCurrentTimestampMillisecondsAsync(CancellationToken cancellationToken = default)
        {
            CurrentTimestampCalls++;

            if (currentTimestampException is not null)
            {
                return Task.FromException<long>(currentTimestampException);
            }

            return Task.FromResult(currentTimestampMilliseconds);
        }
    }

    private sealed class RecordingMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public string? LastRequestUri { get; private set; }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri?.ToString();
            return Task.FromResult(responder(request));
        }
    }
}
