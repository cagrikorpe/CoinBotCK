using System.Net;
using System.Text;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Exchange;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Exchange;

public sealed class BinanceSpotPrivateRestClientTests
{
    [Fact]
    public async Task GetAccountSnapshotAsync_MapsFreeLockedBalances_AndUsesSpotEndpoint()
    {
        using var handler = new RecordingMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"balances":[{"asset":"USDT","free":"10.5","locked":"1.25"},{"asset":"BTC","free":"0.5","locked":"0.0"},{"asset":"BNB","free":"0","locked":"0"}]}""",
                Encoding.UTF8,
                "application/json")
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.binance.com") };
        var sut = CreateClient(client, new FakeSpotTimeSyncService());

        var snapshot = await sut.GetAccountSnapshotAsync(
            Guid.NewGuid(),
            "user-spot",
            "Binance",
            "api-key",
            "api-secret");

        Assert.NotNull(handler.LastRequestUri);
        Assert.Contains("/api/v3/account", handler.LastRequestUri!, StringComparison.Ordinal);
        Assert.DoesNotContain("/fapi/", handler.LastRequestUri!, StringComparison.Ordinal);
        Assert.Equal(ExchangeDataPlane.Spot, snapshot.Plane);
        Assert.Equal([], snapshot.Positions);
        Assert.Equal(["BTC", "USDT"], snapshot.Balances.Select(entity => entity.Asset).ToArray());

        var usdt = snapshot.Balances.Single(entity => entity.Asset == "USDT");
        Assert.Equal(11.75m, usdt.WalletBalance);
        Assert.Equal(10.5m, usdt.AvailableBalance);
        Assert.Equal(10.5m, usdt.MaxWithdrawAmount);
        Assert.Equal(1.25m, usdt.LockedBalance);
        Assert.Equal(ExchangeDataPlane.Spot, usdt.Plane);
    }

    [Fact]
    public async Task GetOrderAsync_MapsSpotOrderStatus_AndUsesSpotEndpoint()
    {
        using var handler = new RecordingMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"symbol":"BTCUSDT","orderId":1001,"clientOrderId":"cbs_order","status":"PARTIALLY_FILLED","origQty":"0.05","executedQty":"0.02","cummulativeQuoteQty":"1280.00","updateTime":1710000000123}""",
                Encoding.UTF8,
                "application/json")
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.binance.com") };
        var sut = CreateClient(client, new FakeSpotTimeSyncService());

        var snapshot = await sut.GetOrderAsync(
            new BinanceOrderQueryRequest(
                Guid.NewGuid(),
                "BTCUSDT",
                ExchangeOrderId: "1001",
                ClientOrderId: null,
                ApiKey: "api-key",
                ApiSecret: "api-secret"));

        Assert.NotNull(handler.LastRequestUri);
        Assert.Contains("/api/v3/order", handler.LastRequestUri!, StringComparison.Ordinal);
        Assert.DoesNotContain("/fapi/", handler.LastRequestUri!, StringComparison.Ordinal);
        Assert.Equal("PARTIALLY_FILLED", snapshot.Status);
        Assert.Equal(0.05m, snapshot.OriginalQuantity);
        Assert.Equal(0.02m, snapshot.ExecutedQuantity);
        Assert.Equal(1280m, snapshot.CumulativeQuoteQuantity);
        Assert.Equal(64000m, snapshot.AveragePrice);
        Assert.Equal(ExchangeDataPlane.Spot, snapshot.Plane);
    }

    [Fact]
    public async Task GetTradeFillsAsync_MapsTradeUpdates_AndDoesNotUseFuturesEndpoint()
    {
        using var handler = new RecordingMessageHandler(request =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;

            return path.Contains("/api/v3/order", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"symbol":"BTCUSDT","orderId":1001,"clientOrderId":"cbs_order","status":"FILLED","origQty":"0.05","executedQty":"0.05","cummulativeQuoteQty":"3200.00","updateTime":1710000000123}""",
                        Encoding.UTF8,
                        "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """[{"id":77,"orderId":1001,"price":"64000","qty":"0.02","quoteQty":"1280","commission":"0.0001","commissionAsset":"BNB","time":1710000000123},{"id":78,"orderId":1001,"price":"64000","qty":"0.03","quoteQty":"1920","commission":"0.0002","commissionAsset":"BNB","time":1710000001123}]""",
                        Encoding.UTF8,
                        "application/json")
                };
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.binance.com") };
        var sut = CreateClient(client, new FakeSpotTimeSyncService());

        var fills = await sut.GetTradeFillsAsync(
            new BinanceOrderQueryRequest(
                Guid.NewGuid(),
                "BTCUSDT",
                ExchangeOrderId: null,
                ClientOrderId: "cbs_order",
                ApiKey: "api-key",
                ApiSecret: "api-secret"));

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.DoesNotContain("/fapi/", request.RequestUri!.ToString(), StringComparison.Ordinal));
        Assert.Contains("/api/v3/order", handler.Requests[0].RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains("/api/v3/myTrades", handler.Requests[1].RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains("orderId=1001", handler.Requests[1].RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("origClientOrderId", handler.Requests[1].RequestUri!.ToString(), StringComparison.Ordinal);

        var firstFill = fills.Single(fill => fill.TradeId == 77);
        Assert.Equal(77L, firstFill.TradeId);
        Assert.Equal("1001", firstFill.ExchangeOrderId);
        Assert.Equal("cbs_order", firstFill.ClientOrderId);
        Assert.Equal(0.02m, firstFill.Quantity);
        Assert.Equal(1280m, firstFill.QuoteQuantity);
        Assert.Equal(64000m, firstFill.Price);
        Assert.Equal("BNB", firstFill.FeeAsset);
        Assert.Equal(0.0001m, firstFill.FeeAmount);
        Assert.Equal(ExchangeDataPlane.Spot, firstFill.Plane);
    }

    [Fact]
    public async Task ListenKeyLifecycle_UsesSpotUserDataStreamEndpoint()
    {
        using var handler = new RecordingMessageHandler(request =>
        {
            return request.Method.Method switch
            {
                "POST" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"listenKey":"spot-listen-key"}""", Encoding.UTF8, "application/json")
                },
                "PUT" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                },
                "DELETE" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                },
                _ => throw new NotSupportedException()
            };
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.binance.com") };
        var sut = CreateClient(client, new FakeSpotTimeSyncService());

        var listenKey = await sut.StartListenKeyAsync("api-key");
        await sut.KeepAliveListenKeyAsync("api-key");
        await sut.CloseListenKeyAsync("api-key");

        Assert.Equal("spot-listen-key", listenKey);
        Assert.Equal(["POST", "PUT", "DELETE"], handler.Requests.Select(request => request.Method.Method).ToArray());
        Assert.All(handler.Requests, request =>
        {
            Assert.Contains("/api/v3/userDataStream", request.RequestUri!.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("/fapi/", request.RequestUri!.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task GetAccountSnapshotAsync_FailsClosed_WhenProviderUnavailable()
    {
        using var handler = new RecordingMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"code":-1001,"msg":"service unavailable"}""", Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.binance.com") };
        var sut = CreateClient(client, new FakeSpotTimeSyncService());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GetAccountSnapshotAsync(
            Guid.NewGuid(),
            "user-spot",
            "Binance",
            "api-key",
            "plain-secret"));

        Assert.Contains("status 503", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-secret", exception.Message, StringComparison.Ordinal);
    }

    private static BinanceSpotPrivateRestClient CreateClient(HttpClient httpClient, IBinanceSpotTimeSyncService timeSyncService)
    {
        return new BinanceSpotPrivateRestClient(
            httpClient,
            Options.Create(new BinancePrivateDataOptions
            {
                SpotRestBaseUrl = "https://api.binance.com",
                SpotWebSocketBaseUrl = "wss://stream.binance.com:9443",
                RecvWindowMilliseconds = 5000
            }),
            new AdjustableTimeProvider(DateTimeOffset.Parse("2026-04-05T10:00:00Z")),
            timeSyncService,
            NullLogger<BinanceSpotPrivateRestClient>.Instance);
    }

    private sealed class FakeSpotTimeSyncService(long currentTimestampMilliseconds = 1_710_000_000_123L) : IBinanceSpotTimeSyncService
    {
        public Task<BinanceTimeSyncSnapshot> GetSnapshotAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new BinanceTimeSyncSnapshot(
                new DateTime(2026, 4, 5, 10, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 4, 5, 10, 0, 0, DateTimeKind.Utc),
                0,
                10,
                new DateTime(2026, 4, 5, 10, 0, 0, DateTimeKind.Utc),
                "Synchronized",
                null));
        }

        public Task<long> GetCurrentTimestampMillisecondsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(currentTimestampMilliseconds);
        }
    }

    private sealed class RecordingMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public string? LastRequestUri { get; private set; }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();
            Requests.Add(CloneRequest(request));
            return Task.FromResult(responder(request));
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);

            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return clone;
        }
    }
}
