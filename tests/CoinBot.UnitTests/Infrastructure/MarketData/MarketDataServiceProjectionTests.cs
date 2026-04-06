using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.MarketData;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.MarketData;

public sealed class MarketDataServiceProjectionTests
{
    [Fact]
    public async Task RecordPriceAsync_ProjectsTicker_AndPreservesLatestEntry_WhenUpdateIsOutOfOrderOrDegraded()
    {
        using var fixture = CreateFixture();
        var acceptedResult = await fixture.MarketDataService.RecordPriceAsync(CreateTicker(
            "btcusdt",
            64000m,
            new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 3, 12, 0, 1, DateTimeKind.Utc)));
        var newerResult = await fixture.MarketDataService.RecordPriceAsync(CreateTicker(
            "BTCUSDT",
            64025m,
            new DateTime(2026, 4, 3, 12, 0, 2, DateTimeKind.Utc),
            new DateTime(2026, 4, 3, 12, 0, 3, DateTimeKind.Utc)));

        var outOfOrderResult = await fixture.MarketDataService.RecordPriceAsync(CreateTicker(
            "BTCUSDT",
            63999m,
            new DateTime(2026, 4, 3, 12, 0, 1, DateTimeKind.Utc),
            new DateTime(2026, 4, 3, 12, 0, 2, DateTimeKind.Utc)));
        var degradedResult = await fixture.MarketDataService.RecordPriceAsync(CreateTicker(
            "BTCUSDT",
            0m,
            new DateTime(2026, 4, 3, 12, 0, 4, DateTimeKind.Utc),
            new DateTime(2026, 4, 3, 12, 0, 5, DateTimeKind.Utc)));

        var latestTicker = await fixture.MarketDataService.GetLatestPriceAsync("BTCUSDT");

        Assert.Equal(SharedMarketDataProjectionStatus.Accepted, acceptedResult.Status);
        Assert.Equal(SharedMarketDataProjectionStatus.Accepted, newerResult.Status);
        Assert.Equal(SharedMarketDataProjectionStatus.IgnoredOutOfOrder, outOfOrderResult.Status);
        Assert.Equal(SharedMarketDataProjectionReasonCode.TickerOutOfOrder, outOfOrderResult.ReasonCode);
        Assert.Equal(SharedMarketDataProjectionStatus.IgnoredDegraded, degradedResult.Status);
        Assert.Equal(SharedMarketDataProjectionReasonCode.TickerDegraded, degradedResult.ReasonCode);
        Assert.NotNull(latestTicker);
        Assert.Equal(64025m, latestTicker!.Price);
        Assert.Equal(new DateTime(2026, 4, 3, 12, 0, 2, DateTimeKind.Utc), latestTicker.ObservedAtUtc);
    }

    [Fact]
    public async Task RecordKlineAsync_ProjectsLatestClosedCandle_AndIgnoresOpenRejectedOrOutOfOrderSnapshots()
    {
        using var fixture = CreateFixture();
        var initialClose = new DateTime(2026, 4, 3, 12, 0, 59, 999, DateTimeKind.Utc);
        var updatedSameCandle = new DateTime(2026, 4, 3, 12, 1, 0, 500, DateTimeKind.Utc);

        var initialResult = await fixture.MarketDataService.RecordKlineAsync(CreateCandle(
            "btcusdt",
            "1M",
            initialClose.AddMilliseconds(-59999),
            initialClose,
            64000m,
            isClosed: true,
            receivedAtUtc: initialClose));
        var sameCandleOverwriteResult = await fixture.MarketDataService.RecordKlineAsync(CreateCandle(
            "BTCUSDT",
            "1m",
            initialClose.AddMilliseconds(-59999),
            initialClose,
            64010m,
            isClosed: true,
            receivedAtUtc: updatedSameCandle));
        var outOfOrderResult = await fixture.MarketDataService.RecordKlineAsync(CreateCandle(
            "BTCUSDT",
            "1m",
            initialClose.AddMinutes(-1).AddMilliseconds(-59999),
            initialClose.AddMinutes(-1),
            63900m,
            isClosed: true,
            receivedAtUtc: updatedSameCandle.AddSeconds(1)));
        var openCandleResult = await fixture.MarketDataService.RecordKlineAsync(CreateCandle(
            "BTCUSDT",
            "1m",
            initialClose.AddMilliseconds(1),
            initialClose.AddMinutes(1),
            64100m,
            isClosed: false,
            receivedAtUtc: updatedSameCandle.AddSeconds(2)));
        var guardRejectedResult = await fixture.MarketDataService.RecordKlineAsync(
            CreateCandle(
                "BTCUSDT",
                "1m",
                initialClose.AddMilliseconds(1),
                initialClose.AddMinutes(1),
                64120m,
                isClosed: true,
                receivedAtUtc: updatedSameCandle.AddSeconds(3)),
            new CandleDataQualityGuardResult(
                false,
                DegradedModeStateCode.Degraded,
                DegradedModeReasonCode.CandleDataGapDetected,
                initialClose,
                null,
                "BTCUSDT",
                "1m",
                1));

        var latestKline = await fixture.MarketDataService.GetLatestKlineAsync("BTCUSDT", "1m");

        Assert.Equal(SharedMarketDataProjectionStatus.Accepted, initialResult.Status);
        Assert.Equal(SharedMarketDataProjectionStatus.Accepted, sameCandleOverwriteResult.Status);
        Assert.Equal(SharedMarketDataProjectionStatus.IgnoredOutOfOrder, outOfOrderResult.Status);
        Assert.Equal(SharedMarketDataProjectionReasonCode.KlineOutOfOrder, outOfOrderResult.ReasonCode);
        Assert.Equal(SharedMarketDataProjectionStatus.IgnoredDegraded, openCandleResult.Status);
        Assert.Equal(SharedMarketDataProjectionReasonCode.KlineDegraded, openCandleResult.ReasonCode);
        Assert.Equal(SharedMarketDataProjectionStatus.IgnoredDegraded, guardRejectedResult.Status);
        Assert.Equal(SharedMarketDataProjectionReasonCode.KlineDegraded, guardRejectedResult.ReasonCode);
        Assert.NotNull(latestKline);
        Assert.Equal("BTCUSDT", latestKline!.Symbol);
        Assert.Equal("1m", latestKline.Interval);
        Assert.True(latestKline.IsClosed);
        Assert.Equal(64010m, latestKline.ClosePrice);
        Assert.Equal(updatedSameCandle, latestKline.ReceivedAtUtc);
    }

    [Fact]
    public async Task RecordDepthAsync_ProjectsLatestDepthSnapshot_AndIgnoresLowerSequenceOrDegradedSnapshots()
    {
        using var fixture = CreateFixture();

        var firstResult = await fixture.MarketDataService.RecordDepthAsync(CreateDepth(
            "btcusdt",
            101,
            new DateTime(2026, 4, 3, 12, 0, 1, DateTimeKind.Utc),
            new DateTime(2026, 4, 3, 12, 0, 2, DateTimeKind.Utc),
            [new MarketDepthLevelSnapshot(64000m, 1.25m)],
            [new MarketDepthLevelSnapshot(64001m, 1.5m)]));
        var acceptedResult = await fixture.MarketDataService.RecordDepthAsync(CreateDepth(
            "BTCUSDT",
            103,
            new DateTime(2026, 4, 3, 12, 0, 3, DateTimeKind.Utc),
            new DateTime(2026, 4, 3, 12, 0, 4, DateTimeKind.Utc),
            [new MarketDepthLevelSnapshot(64010m, 1.75m)],
            [new MarketDepthLevelSnapshot(64011m, 2.25m)]));
        var outOfOrderResult = await fixture.MarketDataService.RecordDepthAsync(CreateDepth(
            "BTCUSDT",
            102,
            new DateTime(2026, 4, 3, 12, 0, 5, DateTimeKind.Utc),
            new DateTime(2026, 4, 3, 12, 0, 6, DateTimeKind.Utc),
            [new MarketDepthLevelSnapshot(64020m, 1m)],
            [new MarketDepthLevelSnapshot(64021m, 1m)]));
        var degradedResult = await fixture.MarketDataService.RecordDepthAsync(CreateDepth(
            "BTCUSDT",
            104,
            new DateTime(2026, 4, 3, 12, 0, 7, DateTimeKind.Utc),
            new DateTime(2026, 4, 3, 12, 0, 8, DateTimeKind.Utc),
            [new MarketDepthLevelSnapshot(64030m, 1m)],
            []));
        var invalidResult = await fixture.MarketDataService.RecordDepthAsync(CreateDepth(
            "BTCUSDT",
            105,
            new DateTime(2026, 4, 3, 12, 0, 9, DateTimeKind.Utc),
            new DateTime(2026, 4, 3, 12, 0, 10, DateTimeKind.Utc),
            [new MarketDepthLevelSnapshot(64030m, -1m)],
            [new MarketDepthLevelSnapshot(64031m, 1m)]));

        var latestDepth = await fixture.MarketDataService.GetLatestDepthAsync("BTCUSDT");

        Assert.Equal(SharedMarketDataProjectionStatus.Accepted, firstResult.Status);
        Assert.Equal(SharedMarketDataProjectionStatus.Accepted, acceptedResult.Status);
        Assert.Equal(SharedMarketDataProjectionStatus.IgnoredOutOfOrder, outOfOrderResult.Status);
        Assert.Equal(SharedMarketDataProjectionReasonCode.DepthOutOfOrder, outOfOrderResult.ReasonCode);
        Assert.Equal(SharedMarketDataProjectionStatus.IgnoredDegraded, degradedResult.Status);
        Assert.Equal(SharedMarketDataProjectionReasonCode.DepthDegraded, degradedResult.ReasonCode);
        Assert.Equal(SharedMarketDataProjectionStatus.InvalidPayload, invalidResult.Status);
        Assert.Equal(SharedMarketDataProjectionReasonCode.InvalidDepthPayload, invalidResult.ReasonCode);
        Assert.NotNull(latestDepth);
        Assert.Equal("BTCUSDT", latestDepth!.Symbol);
        Assert.Equal(103, latestDepth.LastUpdateId);
        Assert.Equal(64010m, latestDepth.Bids.First().Price);
        Assert.Equal(64011m, latestDepth.Asks.First().Price);
    }

    [Fact]
    public async Task RecordKlineAsync_FailsClosed_WithTypedResult_WhenSharedCacheProviderIsUnavailableOrWriteFails()
    {
        using var unavailableFixture = CreateFixture(new ConfigurableSharedMarketDataCache
        {
            ReadResultFactory = () => SharedMarketDataCacheReadResult<MarketCandleSnapshot>.ProviderUnavailable("redis unavailable")
        });
        var providerUnavailableResult = await unavailableFixture.MarketDataService.RecordKlineAsync(CreateCandle(
            "BTCUSDT",
            "1m",
            new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 3, 12, 0, 59, 999, DateTimeKind.Utc),
            64000m,
            isClosed: true,
            receivedAtUtc: new DateTime(2026, 4, 3, 12, 1, 0, DateTimeKind.Utc)));

        using var writeFailedFixture = CreateFixture(new ConfigurableSharedMarketDataCache
        {
            WriteResult = SharedMarketDataCacheWriteResult.SerializeFailed("serialize failed")
        });
        var writeFailedResult = await writeFailedFixture.MarketDataService.RecordDepthAsync(CreateDepth(
            "BTCUSDT",
            200,
            new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 3, 12, 0, 1, DateTimeKind.Utc),
            [new MarketDepthLevelSnapshot(64000m, 1m)],
            [new MarketDepthLevelSnapshot(64001m, 1m)]));

        Assert.Equal(SharedMarketDataProjectionStatus.ProviderUnavailable, providerUnavailableResult.Status);
        Assert.Equal(SharedMarketDataProjectionReasonCode.ProviderUnavailable, providerUnavailableResult.ReasonCode);
        Assert.Equal(SharedMarketDataProjectionStatus.CacheWriteFailed, writeFailedResult.Status);
        Assert.Equal(SharedMarketDataProjectionReasonCode.CacheWriteFailed, writeFailedResult.ReasonCode);
    }

    [Fact]
    public async Task ReadLatestSnapshotsAsync_ReturnTypedStatusAndFreshnessMetadata_FromSharedCacheContract()
    {
        using var fixture = CreateFixture();
        var observedAtUtc = new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc);
        var receivedAtUtc = new DateTime(2026, 4, 3, 12, 0, 1, DateTimeKind.Utc);
        var closeTimeUtc = new DateTime(2026, 4, 3, 12, 0, 59, 999, DateTimeKind.Utc);
        var klineReceivedAtUtc = closeTimeUtc.AddMilliseconds(1);

        await fixture.MarketDataService.RecordPriceAsync(CreateTicker("btcusdt", 64000m, observedAtUtc, receivedAtUtc));
        await fixture.MarketDataService.RecordKlineAsync(CreateCandle(
            "btcusdt",
            "1M",
            closeTimeUtc.AddMilliseconds(-59999),
            closeTimeUtc,
            64000m,
            isClosed: true,
            klineReceivedAtUtc));
        await fixture.MarketDataService.RecordDepthAsync(CreateDepth(
            "btcusdt",
            1001,
            observedAtUtc,
            receivedAtUtc,
            [new MarketDepthLevelSnapshot(63999m, 1m)],
            [new MarketDepthLevelSnapshot(64001m, 1m)]));

        var tickerRead = await fixture.MarketDataService.ReadLatestPriceAsync("BTCUSDT");
        var klineRead = await fixture.MarketDataService.ReadLatestKlineAsync("BTCUSDT", "1m");
        var depthRead = await fixture.MarketDataService.ReadLatestDepthAsync("BTCUSDT");

        Assert.Equal(SharedMarketDataCacheReadStatus.HitFresh, tickerRead.Status);
        Assert.Equal("BTCUSDT", tickerRead.Entry?.Symbol);
        Assert.Equal(receivedAtUtc, tickerRead.Entry?.UpdatedAtUtc);
        Assert.Equal(receivedAtUtc.AddSeconds(15), tickerRead.Entry?.FreshUntilUtc);
        Assert.Equal(64000m, tickerRead.Entry?.Payload.Price);

        Assert.Equal(SharedMarketDataCacheReadStatus.HitFresh, klineRead.Status);
        Assert.Equal("BTCUSDT", klineRead.Entry?.Symbol);
        Assert.Equal("1m", klineRead.Entry?.Timeframe);
        Assert.Equal(klineReceivedAtUtc, klineRead.Entry?.UpdatedAtUtc);
        Assert.Equal(klineReceivedAtUtc.AddSeconds(15), klineRead.Entry?.FreshUntilUtc);
        Assert.Equal(64000m, klineRead.Entry?.Payload.ClosePrice);

        Assert.Equal(SharedMarketDataCacheReadStatus.HitFresh, depthRead.Status);
        Assert.Equal("BTCUSDT", depthRead.Entry?.Symbol);
        Assert.Equal(1001, depthRead.Entry?.Payload.LastUpdateId);
    }

    private static TestFixture CreateFixture(ConfigurableSharedMarketDataCache? sharedMarketDataCache = null)
    {
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 4, 3, 12, 5, 0, TimeSpan.Zero));
        var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 128 });
        var cachePolicyProvider = new MarketDataCachePolicyProvider(Options.Create(new InMemoryCacheOptions
        {
            SizeLimit = 128,
            SymbolMetadataTtlMinutes = 60,
            LatestPriceTtlSeconds = 15
        }));

        var symbolRegistry = new SharedSymbolRegistry(
            memoryCache,
            cachePolicyProvider,
            NullLogger<SharedSymbolRegistry>.Instance);

        return new TestFixture(
            memoryCache,
            new MarketDataService(
                symbolRegistry,
                memoryCache,
                cachePolicyProvider,
                new MarketPriceStreamHub(),
                sharedMarketDataCache ?? new ConfigurableSharedMarketDataCache(),
                timeProvider,
                NullLogger<MarketDataService>.Instance));
    }

    private static MarketPriceSnapshot CreateTicker(
        string symbol,
        decimal price,
        DateTime observedAtUtc,
        DateTime receivedAtUtc)
    {
        return new MarketPriceSnapshot(
            symbol,
            price,
            observedAtUtc,
            receivedAtUtc,
            "unit-test");
    }

    private static MarketCandleSnapshot CreateCandle(
        string symbol,
        string timeframe,
        DateTime openTimeUtc,
        DateTime closeTimeUtc,
        decimal closePrice,
        bool isClosed,
        DateTime receivedAtUtc)
    {
        return new MarketCandleSnapshot(
            symbol,
            timeframe,
            openTimeUtc,
            closeTimeUtc,
            closePrice,
            closePrice + 10m,
            closePrice - 10m,
            closePrice,
            10m,
            isClosed,
            receivedAtUtc,
            "unit-test");
    }

    private static MarketDepthSnapshot CreateDepth(
        string symbol,
        long? lastUpdateId,
        DateTime eventTimeUtc,
        DateTime receivedAtUtc,
        IReadOnlyCollection<MarketDepthLevelSnapshot> bids,
        IReadOnlyCollection<MarketDepthLevelSnapshot> asks)
    {
        return new MarketDepthSnapshot(
            symbol,
            bids,
            asks,
            lastUpdateId,
            eventTimeUtc,
            receivedAtUtc,
            "unit-test");
    }

    private sealed class TestFixture(
        MemoryCache memoryCache,
        MarketDataService marketDataService) : IDisposable
    {
        public MarketDataService MarketDataService { get; } = marketDataService;

        public void Dispose()
        {
            memoryCache.Dispose();
        }
    }

    private sealed class ConfigurableSharedMarketDataCache : ISharedMarketDataCache
    {
        private readonly Dictionary<string, object> entries = new(StringComparer.Ordinal);

        public Func<SharedMarketDataCacheReadResult<MarketCandleSnapshot>>? ReadResultFactory { get; init; }

        public SharedMarketDataCacheWriteResult WriteResult { get; init; } = SharedMarketDataCacheWriteResult.Written();

        public ValueTask<SharedMarketDataCacheWriteResult> WriteAsync<TPayload>(
            SharedMarketDataCacheEntry<TPayload> entry,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (WriteResult.Status == SharedMarketDataCacheWriteStatus.Written)
            {
                entries[SharedMarketDataCacheKeyBuilder.Build(entry.DataType, entry.Symbol, entry.Timeframe)] = entry;
            }

            return ValueTask.FromResult(WriteResult);
        }

        public ValueTask<SharedMarketDataCacheReadResult<TPayload>> ReadAsync<TPayload>(
            SharedMarketDataCacheDataType dataType,
            string symbol,
            string? timeframe,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ReadResultFactory is not null &&
                typeof(TPayload) == typeof(MarketCandleSnapshot))
            {
                var forcedResult = ReadResultFactory();
                return ValueTask.FromResult(new SharedMarketDataCacheReadResult<TPayload>(
                    forcedResult.Status,
                    forcedResult.Entry is null
                        ? null
                        : new SharedMarketDataCacheEntry<TPayload>(
                            forcedResult.Entry.DataType,
                            forcedResult.Entry.Symbol,
                            forcedResult.Entry.Timeframe,
                            forcedResult.Entry.UpdatedAtUtc,
                            forcedResult.Entry.CachedAtUtc,
                            forcedResult.Entry.FreshUntilUtc,
                            forcedResult.Entry.ExpiresAtUtc,
                            forcedResult.Entry.Source,
                            (TPayload)(object)forcedResult.Entry.Payload),
                    forcedResult.ReasonCode,
                    forcedResult.ReasonSummary));
            }

            if (!entries.TryGetValue(SharedMarketDataCacheKeyBuilder.Build(dataType, symbol, timeframe), out var entry) ||
                entry is not SharedMarketDataCacheEntry<TPayload> typedEntry)
            {
                return ValueTask.FromResult(SharedMarketDataCacheReadResult<TPayload>.Miss());
            }

            return ValueTask.FromResult(SharedMarketDataCacheReadResult<TPayload>.HitFresh(typedEntry));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return nowUtc;
        }
    }
}


