using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Infrastructure.MarketData;

namespace CoinBot.UnitTests.Infrastructure.MarketData;

public sealed class SharedMarketDataCacheObservabilityCollectorTests
{
    [Fact]
    public void GetSnapshot_TracksReadMetrics_AndComputesFreshnessTimeoutReason()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 20, 0, 0, TimeSpan.Zero);
        var collector = new SharedMarketDataCacheObservabilityCollector(new FixedTimeProvider(nowUtc));
        var freshEntry = new SharedMarketDataCacheEntry<MarketPriceSnapshot>(
            SharedMarketDataCacheDataType.Ticker,
            "BTCUSDT",
            "spot",
            nowUtc.UtcDateTime.AddSeconds(-1),
            nowUtc.UtcDateTime,
            nowUtc.UtcDateTime.AddSeconds(10),
            nowUtc.UtcDateTime.AddMinutes(1),
            "Binance.WebSocket.Ticker",
            new MarketPriceSnapshot("BTCUSDT", 65000m, nowUtc.UtcDateTime.AddSeconds(-1), nowUtc.UtcDateTime, "Binance.WebSocket.Ticker"));
        var staleEntry = freshEntry with
        {
            FreshUntilUtc = nowUtc.UtcDateTime.AddSeconds(-1),
            Payload = freshEntry.Payload with
            {
                Symbol = "ETHUSDT",
                Source = "Binance.WebSocket.Ticker"
            },
            Symbol = "ETHUSDT"
        };

        collector.RecordRead(SharedMarketDataCacheDataType.Ticker, "BTCUSDT", null, SharedMarketDataCacheReadResult<MarketPriceSnapshot>.HitFresh(freshEntry));
        collector.RecordRead(SharedMarketDataCacheDataType.Ticker, "ETHUSDT", null, SharedMarketDataCacheReadResult<MarketPriceSnapshot>.HitStale(staleEntry));
        collector.RecordRead(SharedMarketDataCacheDataType.Kline, "ADAUSDT", "1m", SharedMarketDataCacheReadResult<MarketCandleSnapshot>.Miss("No kline."));
        collector.RecordRead(SharedMarketDataCacheDataType.Depth, "SOLUSDT", null, SharedMarketDataCacheReadResult<MarketDepthSnapshot>.ProviderUnavailable("Redis unavailable."));
        collector.RecordRead(SharedMarketDataCacheDataType.Depth, "XRPUSDT", null, SharedMarketDataCacheReadResult<MarketDepthSnapshot>.InvalidPayload("Bad depth."));
        collector.RecordRead(SharedMarketDataCacheDataType.Ticker, "DOGEUSDT", null, SharedMarketDataCacheReadResult<MarketPriceSnapshot>.DeserializeFailed("Malformed json."));

        var snapshot = collector.GetSnapshot(nowUtc.UtcDateTime.AddSeconds(30));

        Assert.Equal(1, snapshot.HitCount);
        Assert.Equal(1, snapshot.StaleHitCount);
        Assert.Equal(1, snapshot.MissCount);
        Assert.Equal(1, snapshot.ProviderUnavailableCount);
        Assert.Equal(1, snapshot.InvalidPayloadCount);
        Assert.Equal(1, snapshot.DeserializeFailedCount);
        Assert.Equal(DateTimeKind.Utc, snapshot.LastObservedAtUtc.Kind);

        var btc = Assert.Single(snapshot.SymbolFreshness, item => item.Symbol == "BTCUSDT" && item.DataType == SharedMarketDataCacheDataType.Ticker);
        Assert.Equal(SharedMarketDataCacheReadStatus.HitStale, btc.LastReadStatus);
        Assert.Equal(SharedMarketDataCacheStaleReasonCode.FreshnessTimeout, btc.StaleReasonCode);
        Assert.Equal("Binance.WebSocket.Ticker", btc.SourceLayer);

        var ada = Assert.Single(snapshot.SymbolFreshness, item => item.Symbol == "ADAUSDT" && item.DataType == SharedMarketDataCacheDataType.Kline);
        Assert.Equal(SharedMarketDataCacheReadStatus.Miss, ada.LastReadStatus);
        Assert.Equal(SharedMarketDataCacheStaleReasonCode.Miss, ada.StaleReasonCode);

        var depthStream = Assert.Single(snapshot.StreamSnapshots, item => item.DataType == SharedMarketDataCacheDataType.Depth);
        Assert.Equal("SOLUSDT", depthStream.Symbol);
        Assert.Equal(SharedMarketDataCacheStaleReasonCode.ProviderUnavailable, depthStream.StaleReasonCode);
    }

    [Fact]
    public void GetSnapshot_PreservesLastHealthyProjectionMetadata_WhenOutOfOrderOrDegradedUpdatesAreIgnored()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 20, 5, 0, TimeSpan.Zero);
        var collector = new SharedMarketDataCacheObservabilityCollector(new FixedTimeProvider(nowUtc));

        collector.RecordProjection(
            SharedMarketDataCacheDataType.Kline,
            "btcusdt",
            "1M",
            SharedMarketDataProjectionResult.Accepted(),
            nowUtc.UtcDateTime.AddMinutes(-1),
            nowUtc.UtcDateTime.AddSeconds(30),
            "Binance.WebSocket.Kline");
        collector.RecordProjection(
            SharedMarketDataCacheDataType.Kline,
            "BTCUSDT",
            "1m",
            SharedMarketDataProjectionResult.IgnoredOutOfOrder(SharedMarketDataProjectionReasonCode.KlineOutOfOrder, "Older kline."),
            nowUtc.UtcDateTime.AddMinutes(-2),
            nowUtc.UtcDateTime.AddSeconds(20),
            "Binance.WebSocket.Kline");
        collector.RecordProjection(
            SharedMarketDataCacheDataType.Depth,
            "BTCUSDT",
            null,
            SharedMarketDataProjectionResult.IgnoredDegraded(SharedMarketDataProjectionReasonCode.DepthDegraded, "Crossed book."),
            nowUtc.UtcDateTime,
            nowUtc.UtcDateTime.AddSeconds(20),
            "Binance.WebSocket.Depth");

        var snapshot = collector.GetSnapshot(nowUtc.UtcDateTime);

        var klineScope = Assert.Single(snapshot.SymbolFreshness, item => item.DataType == SharedMarketDataCacheDataType.Kline && item.Symbol == "BTCUSDT");
        Assert.Equal("1m", klineScope.Timeframe);
        Assert.Equal(nowUtc.UtcDateTime.AddMinutes(-1), klineScope.UpdatedAtUtc);
        Assert.Equal(nowUtc.UtcDateTime.AddSeconds(30), klineScope.FreshUntilUtc);
        Assert.Equal("Binance.WebSocket.Kline", klineScope.SourceLayer);
        Assert.Equal(SharedMarketDataProjectionStatus.IgnoredOutOfOrder, klineScope.LastProjectionStatus);
        Assert.Equal(SharedMarketDataCacheStaleReasonCode.IgnoredOutOfOrder, klineScope.StaleReasonCode);
        Assert.Equal("Older kline.", klineScope.ReasonSummary);

        var depthScope = Assert.Single(snapshot.SymbolFreshness, item => item.DataType == SharedMarketDataCacheDataType.Depth && item.Symbol == "BTCUSDT");
        Assert.Equal(SharedMarketDataProjectionStatus.IgnoredDegraded, depthScope.LastProjectionStatus);
        Assert.Equal(SharedMarketDataCacheStaleReasonCode.IgnoredDegraded, depthScope.StaleReasonCode);
        Assert.Equal("Binance.WebSocket.Depth", depthScope.SourceLayer);
        Assert.Equal("Crossed book.", depthScope.ReasonSummary);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

