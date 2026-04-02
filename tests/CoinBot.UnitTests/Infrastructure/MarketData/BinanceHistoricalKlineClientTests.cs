using System.Net;
using System.Text;
using CoinBot.Infrastructure.MarketData;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoinBot.UnitTests.Infrastructure.MarketData;

public sealed class BinanceHistoricalKlineClientTests
{
    [Fact]
    public async Task GetClosedCandlesAsync_MapsClosedCandlesAndBuildsExpectedRequest()
    {
        var now = new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero);
        var handler = new StubHttpMessageHandler(
            """
            [
              [
                1711108740000,
                "64000.10",
                "64120.50",
                "63980.00",
                "64090.30",
                "12.50000000",
                1711108799999
              ]
            ]
            """);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://fapi.binance.com/")
        };
        var client = new BinanceHistoricalKlineClient(
            httpClient,
            new AdjustableTimeProvider(now),
            NullLogger<BinanceHistoricalKlineClient>.Instance);

        var startOpenTimeUtc = new DateTime(2024, 3, 22, 11, 59, 0, DateTimeKind.Utc);
        var endOpenTimeUtc = new DateTime(2024, 3, 22, 11, 59, 0, DateTimeKind.Utc);

        var snapshots = await client.GetClosedCandlesAsync(
            "btcusdt",
            "1m",
            startOpenTimeUtc,
            endOpenTimeUtc,
            limit: 50);

        var snapshot = Assert.Single(snapshots);
        Assert.Equal("BTCUSDT", snapshot.Symbol);
        Assert.Equal("1m", snapshot.Interval);
        Assert.Equal(new DateTime(2024, 3, 22, 11, 59, 0, DateTimeKind.Utc), snapshot.OpenTimeUtc);
        Assert.Equal(new DateTime(2024, 3, 22, 11, 59, 59, 999, DateTimeKind.Utc), snapshot.CloseTimeUtc);
        Assert.Equal(64000.10m, snapshot.OpenPrice);
        Assert.Equal(64120.50m, snapshot.HighPrice);
        Assert.Equal(63980.00m, snapshot.LowPrice);
        Assert.Equal(64090.30m, snapshot.ClosePrice);
        Assert.Equal(12.5m, snapshot.Volume);
        Assert.Equal(now.UtcDateTime, snapshot.ReceivedAtUtc);
        Assert.Equal("Binance.Rest.Kline", snapshot.Source);
        Assert.Contains("fapi/v1/klines", handler.LastRequestUri, StringComparison.Ordinal);
        Assert.Contains("symbol=BTCUSDT", handler.LastRequestUri, StringComparison.Ordinal);
        Assert.Contains("interval=1m", handler.LastRequestUri, StringComparison.Ordinal);
        Assert.Contains("limit=50", handler.LastRequestUri, StringComparison.Ordinal);
    }

    private sealed class StubHttpMessageHandler(string jsonPayload) : HttpMessageHandler
    {
        public string LastRequestUri { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString() ?? string.Empty;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            });
        }
    }
}
