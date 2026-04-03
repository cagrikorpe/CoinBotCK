using CoinBot.Infrastructure.MarketData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.MarketData;

public sealed class BinanceDepthStreamClientTests
{
    [Fact]
    public void TryParseSnapshot_MapsNativeDepthEvent_ToMarketDepthSnapshot_WithOrderingMetadata()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 15, 30, 0, TimeSpan.Zero);
        var client = CreateClient(nowUtc);
        const string payload = """
        {
          "stream": "btcusdt@depth5@100ms",
          "data": {
            "e": "depthUpdate",
            "E": 1775220600123,
            "s": "btcusdt",
            "u": 987654,
            "b": [["64000.50", "1.250"], ["63999.90", "0.800"]],
            "a": [["64001.10", "1.100"], ["64001.80", "0.500"]]
          }
        }
        """;

        var snapshot = client.TryParseSnapshot(payload);

        Assert.NotNull(snapshot);
        Assert.Equal("BTCUSDT", snapshot!.Symbol);
        Assert.Equal(987654, snapshot.LastUpdateId);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1775220600123).UtcDateTime, snapshot.EventTimeUtc);
        Assert.Equal(nowUtc.UtcDateTime, snapshot.ReceivedAtUtc);
        Assert.Equal("Binance.WebSocket.Depth", snapshot.Source);
        Assert.Equal(2, snapshot.Bids.Count);
        Assert.Equal(2, snapshot.Asks.Count);
        Assert.Equal(64000.50m, snapshot.Bids.First().Price);
        Assert.Equal(1.250m, snapshot.Bids.First().Quantity);
        Assert.Equal(64001.10m, snapshot.Asks.First().Price);
        Assert.Equal(1.100m, snapshot.Asks.First().Quantity);
    }

    [Fact]
    public void TryParseSnapshot_MapsPartialDepthPayload_UsingStreamNameSymbol_AndFallsBackToReceivedAt()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 15, 31, 0, TimeSpan.Zero);
        var client = CreateClient(nowUtc);
        const string payload = """
        {
          "stream": "ethusdt@depth5@100ms",
          "data": {
            "lastUpdateId": 445566,
            "bids": [["3300.10", "4.200"]],
            "asks": [["3300.60", "3.700"]]
          }
        }
        """;

        var snapshot = client.TryParseSnapshot(payload);

        Assert.NotNull(snapshot);
        Assert.Equal("ETHUSDT", snapshot!.Symbol);
        Assert.Equal(445566, snapshot.LastUpdateId);
        Assert.Equal(nowUtc.UtcDateTime, snapshot.EventTimeUtc);
        Assert.Equal(nowUtc.UtcDateTime, snapshot.ReceivedAtUtc);
        Assert.Equal(3300.10m, snapshot.Bids.Single().Price);
        Assert.Equal(3300.60m, snapshot.Asks.Single().Price);
    }

    [Theory]
    [InlineData("""{"stream":"btcusdt@depth5@100ms","data":{"b":[["64000","1"]],"a":[]}}""")]
    [InlineData("""{"stream":"btcusdt@depth5@100ms","data":{"b":[["bad","1"]],"a":[["64001","1"]]}}""")]
    [InlineData("""{"data":{"u":10,"b":[["64000","1"]],"a":[["64001","1"]]}}""")]
    public void TryParseSnapshot_ReturnsNull_WhenDepthPayloadIsInvalidOrMissingScope(string payload)
    {
        var client = CreateClient(new DateTimeOffset(2026, 4, 3, 15, 32, 0, TimeSpan.Zero));

        var snapshot = client.TryParseSnapshot(payload);

        Assert.Null(snapshot);
    }

    private static BinanceDepthStreamClient CreateClient(DateTimeOffset nowUtc)
    {
        return new BinanceDepthStreamClient(
            Options.Create(new BinanceMarketDataOptions
            {
                Enabled = true,
                RestBaseUrl = "https://api.binance.com",
                WebSocketBaseUrl = "wss://stream.binance.com:9443",
                KlineInterval = "1m",
                ExchangeInfoRefreshIntervalMinutes = 60,
                ReconnectDelaySeconds = 1,
                HeartbeatPersistenceIntervalSeconds = 1,
                SeedSymbols = []
            }),
            new FixedTimeProvider(nowUtc),
            NullLogger<BinanceDepthStreamClient>.Instance);
    }

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return nowUtc;
        }
    }
}
