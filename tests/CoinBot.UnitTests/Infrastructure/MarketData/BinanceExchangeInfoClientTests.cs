using System.Net;
using System.Text;
using CoinBot.Infrastructure.MarketData;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoinBot.UnitTests.Infrastructure.MarketData;

public sealed class BinanceExchangeInfoClientTests
{
    [Fact]
    public async Task GetSymbolMetadataAsync_MapsTickSizeStepSizeAndTradingStatus()
    {
        var now = new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero);
        var handler = new StubHttpMessageHandler(
            """
            {
              "symbols": [
                {
                  "symbol": "BTCUSDT",
                  "status": "TRADING",
                  "baseAsset": "BTC",
                  "quoteAsset": "USDT",
                  "filters": [
                    { "filterType": "PRICE_FILTER", "tickSize": "0.01000000" },
                    { "filterType": "LOT_SIZE", "stepSize": "0.00010000" }
                  ]
                }
              ]
            }
            """);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.binance.com/")
        };
        var client = new BinanceExchangeInfoClient(
            httpClient,
            new AdjustableTimeProvider(now),
            NullLogger<BinanceExchangeInfoClient>.Instance);

        var snapshots = await client.GetSymbolMetadataAsync(["btcusdt"]);

        var snapshot = Assert.Single(snapshots);

        Assert.Equal("BTCUSDT", snapshot.Symbol);
        Assert.Equal("Binance", snapshot.Exchange);
        Assert.Equal("BTC", snapshot.BaseAsset);
        Assert.Equal("USDT", snapshot.QuoteAsset);
        Assert.Equal(0.01m, snapshot.TickSize);
        Assert.Equal(0.0001m, snapshot.StepSize);
        Assert.Equal("TRADING", snapshot.TradingStatus);
        Assert.True(snapshot.IsTradingEnabled);
        Assert.Equal(now.UtcDateTime, snapshot.RefreshedAtUtc);
        Assert.Contains("exchangeInfo", handler.LastRequestUri, StringComparison.Ordinal);
        Assert.Contains("BTCUSDT", Uri.UnescapeDataString(handler.LastRequestUri), StringComparison.Ordinal);
    }

    private sealed class StubHttpMessageHandler(string jsonPayload) : HttpMessageHandler
    {
        public string LastRequestUri { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString() ?? string.Empty;

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };

            return Task.FromResult(response);
        }
    }
}
