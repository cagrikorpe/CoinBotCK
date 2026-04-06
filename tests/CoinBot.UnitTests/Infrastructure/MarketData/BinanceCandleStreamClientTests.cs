using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Infrastructure.MarketData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.MarketData;

public sealed class BinanceCandleStreamClientTests
{
    [Fact]
    public void TryParseSnapshot_MapsCombinedKlinePayload_AndClampsReceivedAtUtc_WhenLocalClockLagsCloseTime()
    {
        var closeTimeUtc = DateTimeOffset.FromUnixTimeMilliseconds(1775477279999);
        var client = CreateClient(closeTimeUtc.AddMilliseconds(-500));
        const string payload = """
        {
          "stream": "btcusdt@kline_1m",
          "data": {
            "e": "kline",
            "E": 1775477279769,
            "s": "BTCUSDT",
            "k": {
              "t": 1775477220000,
              "T": 1775477279999,
              "s": "BTCUSDT",
              "i": "1m",
              "o": "69540.10",
              "c": "69541.20",
              "h": "69542.00",
              "l": "69539.90",
              "v": "12.450",
              "x": true
            }
          }
        }
        """;

        var snapshot = client.TryParseSnapshot(payload);
        var projectionResult = SharedMarketDataProjectionPolicy.NormalizeKline(snapshot!, null, out var normalizedSnapshot);

        Assert.NotNull(snapshot);
        Assert.Equal("BTCUSDT", snapshot!.Symbol);
        Assert.Equal("1m", snapshot.Interval);
        Assert.Equal(closeTimeUtc.UtcDateTime, snapshot.CloseTimeUtc);
        Assert.Equal(closeTimeUtc.UtcDateTime, snapshot.ReceivedAtUtc);
        Assert.True(snapshot.IsClosed);
        Assert.Equal(SharedMarketDataProjectionStatus.Accepted, projectionResult.Status);
        Assert.Equal(closeTimeUtc.UtcDateTime, normalizedSnapshot.ReceivedAtUtc);
    }

    [Fact]
    public void TryParseSnapshot_ReturnsNull_WhenRequiredKlineFieldsAreMissing()
    {
        var client = CreateClient(new DateTimeOffset(2026, 4, 6, 12, 0, 0, TimeSpan.Zero));
        const string payload = """{"stream":"btcusdt@kline_1m","data":{"s":"BTCUSDT","k":{"i":"1m","o":"1","h":"2","l":"0.5","c":"1.5","v":"10","x":true}}}""";

        var snapshot = client.TryParseSnapshot(payload);

        Assert.Null(snapshot);
    }

    private static BinanceCandleStreamClient CreateClient(DateTimeOffset nowUtc)
    {
        return new BinanceCandleStreamClient(
            Options.Create(new BinanceMarketDataOptions
            {
                Enabled = true,
                RestBaseUrl = "https://testnet.binancefuture.com",
                WebSocketBaseUrl = "wss://fstream.binancefuture.com",
                KlineInterval = "1m",
                ExchangeInfoRefreshIntervalMinutes = 60,
                ReconnectDelaySeconds = 1,
                HeartbeatPersistenceIntervalSeconds = 1,
                SeedSymbols = []
            }),
            new FixedTimeProvider(nowUtc),
            NullLogger<BinanceCandleStreamClient>.Instance);
    }

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return nowUtc;
        }
    }
}
