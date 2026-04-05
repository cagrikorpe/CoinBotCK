using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Autonomy;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using CoinBot.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.IntegrationTests.MarketData;

public sealed class RedisSharedMarketDataCacheIntegrationTests
{
    [Fact]
    public async Task MarketDataService_SharesTickerSnapshotAcrossIndependentWriterAndReaderInstances_ThroughRedis()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 18, 0, 0, TimeSpan.Zero);
        await using var fakeRedis = await FakeRedisServer.StartAsync();
        using var workerMemoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 });
        using var webMemoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 });
        var workerPolicyProvider = CreatePolicyProvider();
        var webPolicyProvider = CreatePolicyProvider();
        var workerSymbolRegistry = new SharedSymbolRegistry(
            workerMemoryCache,
            workerPolicyProvider,
            NullLogger<SharedSymbolRegistry>.Instance);
        var webSymbolRegistry = new SharedSymbolRegistry(
            webMemoryCache,
            webPolicyProvider,
            NullLogger<SharedSymbolRegistry>.Instance);
        var workerService = new MarketDataService(
            workerSymbolRegistry,
            workerMemoryCache,
            workerPolicyProvider,
            new MarketPriceStreamHub(),
            CreateCache(fakeRedis.ConnectionString, nowUtc),
            new FixedTimeProvider(nowUtc),
            NullLogger<MarketDataService>.Instance);
        var webService = new MarketDataService(
            webSymbolRegistry,
            webMemoryCache,
            webPolicyProvider,
            new MarketPriceStreamHub(),
            CreateCache(fakeRedis.ConnectionString, nowUtc.AddSeconds(5)),
            new FixedTimeProvider(nowUtc.AddSeconds(5)),
            NullLogger<MarketDataService>.Instance);

        var tickerWriteResult = await workerService.RecordPriceAsync(new MarketPriceSnapshot(
            "btcusdt",
            65234.12m,
            nowUtc.UtcDateTime.AddSeconds(-1),
            nowUtc.UtcDateTime,
            "Binance.WebSocket.Kline"));
        var klineWriteResult = await workerService.RecordKlineAsync(new MarketCandleSnapshot(
            "btcusdt",
            "1M",
            nowUtc.UtcDateTime.AddMinutes(-1),
            nowUtc.UtcDateTime.AddMilliseconds(-1),
            65100m,
            65300m,
            65050m,
            65234.12m,
            120.5m,
            true,
            nowUtc.UtcDateTime,
            "Binance.WebSocket.Kline"));
        var depthWriteResult = await workerService.RecordDepthAsync(new MarketDepthSnapshot(
            "btcusdt",
            [
                new MarketDepthLevelSnapshot(65234.00m, 1.2m),
                new MarketDepthLevelSnapshot(65233.90m, 0.8m)
            ],
            [
                new MarketDepthLevelSnapshot(65234.20m, 1.4m),
                new MarketDepthLevelSnapshot(65234.30m, 0.9m)
            ],
            LastUpdateId: 500,
            EventTimeUtc: nowUtc.UtcDateTime,
            ReceivedAtUtc: nowUtc.UtcDateTime.AddMilliseconds(50),
            Source: "integration-test-depth"));
        var staleDepthWriteResult = await workerService.RecordDepthAsync(new MarketDepthSnapshot(
            "BTCUSDT",
            [new MarketDepthLevelSnapshot(65000m, 9.9m)],
            [new MarketDepthLevelSnapshot(65001m, 8.8m)],
            LastUpdateId: 499,
            EventTimeUtc: nowUtc.UtcDateTime.AddSeconds(2),
            ReceivedAtUtc: nowUtc.UtcDateTime.AddSeconds(3),
            Source: "integration-test-depth"));
        var degradedKlineWriteResult = await workerService.RecordKlineAsync(new MarketCandleSnapshot(
            "BTCUSDT",
            "1m",
            nowUtc.UtcDateTime,
            nowUtc.UtcDateTime.AddMinutes(1).AddMilliseconds(-1),
            65234.12m,
            65240m,
            65220m,
            65238m,
            90m,
            false,
            nowUtc.UtcDateTime.AddSeconds(2),
            "Binance.WebSocket.Kline"));

        var latestPrice = await webService.GetLatestPriceAsync("BTCUSDT");
        var latestKline = await webService.GetLatestKlineAsync("BTCUSDT", "1m");
        var latestDepth = await webService.GetLatestDepthAsync("BTCUSDT");
        var rawRead = await CreateCache(fakeRedis.ConnectionString, nowUtc.AddSeconds(5))
            .ReadAsync<MarketPriceSnapshot>(SharedMarketDataCacheDataType.Ticker, "BTCUSDT", null);
        var rawKlineRead = await CreateCache(fakeRedis.ConnectionString, nowUtc.AddSeconds(5))
            .ReadAsync<MarketCandleSnapshot>(SharedMarketDataCacheDataType.Kline, "BTCUSDT", "1m");
        var rawDepthRead = await CreateCache(fakeRedis.ConnectionString, nowUtc.AddSeconds(5))
            .ReadAsync<MarketDepthSnapshot>(SharedMarketDataCacheDataType.Depth, "BTCUSDT", null);

        Assert.Equal(SharedMarketDataProjectionStatus.Accepted, tickerWriteResult.Status);
        Assert.Equal(SharedMarketDataProjectionStatus.Accepted, klineWriteResult.Status);
        Assert.Equal(SharedMarketDataProjectionStatus.Accepted, depthWriteResult.Status);
        Assert.Equal(SharedMarketDataProjectionStatus.IgnoredOutOfOrder, staleDepthWriteResult.Status);
        Assert.Equal(SharedMarketDataProjectionReasonCode.DepthOutOfOrder, staleDepthWriteResult.ReasonCode);
        Assert.Equal(SharedMarketDataProjectionStatus.IgnoredDegraded, degradedKlineWriteResult.Status);
        Assert.Equal(SharedMarketDataProjectionReasonCode.KlineDegraded, degradedKlineWriteResult.ReasonCode);
        Assert.NotNull(latestPrice);
        Assert.Equal("BTCUSDT", latestPrice!.Symbol);
        Assert.Equal(65234.12m, latestPrice.Price);
        Assert.Equal("Binance.WebSocket.Kline", latestPrice.Source);
        Assert.NotNull(latestKline);
        Assert.Equal("BTCUSDT", latestKline!.Symbol);
        Assert.Equal("1m", latestKline.Interval);
        Assert.True(latestKline.IsClosed);
        Assert.Equal(65234.12m, latestKline.ClosePrice);
        Assert.NotNull(latestDepth);
        Assert.Equal("BTCUSDT", latestDepth!.Symbol);
        Assert.Equal(500, latestDepth.LastUpdateId);
        Assert.Equal(65234.00m, latestDepth.Bids.First().Price);
        Assert.Equal(65234.20m, latestDepth.Asks.First().Price);
        Assert.Equal(SharedMarketDataCacheReadStatus.HitFresh, rawRead.Status);
        Assert.Equal("BTCUSDT", rawRead.Entry?.Symbol);
        Assert.Equal("spot", rawRead.Entry?.Timeframe);
        Assert.Equal(nowUtc.UtcDateTime, rawRead.Entry?.UpdatedAtUtc);
        Assert.Equal(SharedMarketDataCacheReadStatus.HitFresh, rawKlineRead.Status);
        Assert.Equal("BTCUSDT", rawKlineRead.Entry?.Symbol);
        Assert.Equal("1m", rawKlineRead.Entry?.Timeframe);
        Assert.Equal(nowUtc.UtcDateTime.AddMilliseconds(-1), rawKlineRead.Entry?.UpdatedAtUtc);
        Assert.Equal(SharedMarketDataCacheReadStatus.HitFresh, rawDepthRead.Status);
        Assert.Equal("BTCUSDT", rawDepthRead.Entry?.Symbol);
        Assert.Equal("spot", rawDepthRead.Entry?.Timeframe);
        Assert.Equal(nowUtc.UtcDateTime, rawDepthRead.Entry?.UpdatedAtUtc);
    }

    [Fact]
    public async Task BinanceWebSocketManager_ProjectsNativeDepthStreamSnapshots_ToRedisSharedCache_AndPreservesLatestHealthyDepth()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 18, 10, 0, TimeSpan.Zero);
        await using var fakeRedis = await FakeRedisServer.StartAsync();
        using var workerMemoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 });
        using var webMemoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 });
        var workerPolicyProvider = CreatePolicyProvider();
        var webPolicyProvider = CreatePolicyProvider();
        var workerSymbolRegistry = new SharedSymbolRegistry(
            workerMemoryCache,
            workerPolicyProvider,
            NullLogger<SharedSymbolRegistry>.Instance);
        var webSymbolRegistry = new SharedSymbolRegistry(
            webMemoryCache,
            webPolicyProvider,
            NullLogger<SharedSymbolRegistry>.Instance);
        var workerService = new MarketDataService(
            workerSymbolRegistry,
            workerMemoryCache,
            workerPolicyProvider,
            new MarketPriceStreamHub(),
            CreateCache(fakeRedis.ConnectionString, nowUtc),
            new FixedTimeProvider(nowUtc),
            NullLogger<MarketDataService>.Instance);
        var webService = new MarketDataService(
            webSymbolRegistry,
            webMemoryCache,
            webPolicyProvider,
            new MarketPriceStreamHub(),
            CreateCache(fakeRedis.ConnectionString, nowUtc.AddSeconds(3)),
            new FixedTimeProvider(nowUtc.AddSeconds(3)),
            NullLogger<MarketDataService>.Instance);
        var indicatorDataService = new IndicatorDataService(
            workerService,
            new IndicatorStreamHub(),
            Options.Create(new IndicatorEngineOptions()),
            NullLogger<IndicatorDataService>.Instance);
        var manager = new BinanceWebSocketManager(
            workerSymbolRegistry,
            workerService,
            indicatorDataService,
            new FakeExchangeInfoClient(nowUtc.UtcDateTime),
            new FakeCandleStreamClient([
                new MarketCandleSnapshot(
                    "BTCUSDT",
                    "1m",
                    nowUtc.UtcDateTime,
                    nowUtc.UtcDateTime.AddMinutes(1).AddMilliseconds(-1),
                    65000m,
                    65050m,
                    64980m,
                    65025m,
                    15m,
                    true,
                    nowUtc.UtcDateTime.AddMinutes(1),
                    "Binance.WebSocket.Kline")
            ]),
            new FakeDepthStreamClient([
                new MarketDepthSnapshot(
                    "btcusdt",
                    [new MarketDepthLevelSnapshot(65024.5m, 1.1m)],
                    [new MarketDepthLevelSnapshot(65025.5m, 1.3m)],
                    770001,
                    nowUtc.UtcDateTime.AddSeconds(1),
                    nowUtc.UtcDateTime.AddSeconds(1),
                    "Binance.WebSocket.Depth"),
                new MarketDepthSnapshot(
                    "BTCUSDT",
                    [new MarketDepthLevelSnapshot(65020m, 9m)],
                    [new MarketDepthLevelSnapshot(65021m, 9m)],
                    770000,
                    nowUtc.UtcDateTime.AddSeconds(2),
                    nowUtc.UtcDateTime.AddSeconds(2),
                    "Binance.WebSocket.Depth"),
                new MarketDepthSnapshot(
                    "BTCUSDT",
                    [new MarketDepthLevelSnapshot(65030m, 2m)],
                    [],
                    770002,
                    nowUtc.UtcDateTime.AddSeconds(3),
                    nowUtc.UtcDateTime.AddSeconds(3),
                    "Binance.WebSocket.Depth")
            ]),
            new CandleDataQualityGuard(
                new CandleContinuityValidator(NullLogger<CandleContinuityValidator>.Instance),
                NullLogger<CandleDataQualityGuard>.Instance),
            new FakeHeartbeatRecorder(),
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
            NullLogger<BinanceWebSocketManager>.Instance);

        await workerService.TrackSymbolAsync("BTCUSDT");

        await manager.RunCycleAsync();

        var latestDepth = await webService.GetLatestDepthAsync("BTCUSDT");
        var rawDepthRead = await CreateCache(fakeRedis.ConnectionString, nowUtc.AddSeconds(3))
            .ReadAsync<MarketDepthSnapshot>(SharedMarketDataCacheDataType.Depth, "BTCUSDT", null);

        Assert.NotNull(latestDepth);
        Assert.Equal("BTCUSDT", latestDepth!.Symbol);
        Assert.Equal(770001, latestDepth.LastUpdateId);
        Assert.Equal(nowUtc.UtcDateTime.AddSeconds(1), latestDepth.EventTimeUtc);
        Assert.Equal(nowUtc.UtcDateTime.AddSeconds(1), latestDepth.ReceivedAtUtc);
        Assert.Equal(65024.5m, latestDepth.Bids.Single().Price);
        Assert.Equal(65025.5m, latestDepth.Asks.Single().Price);
        Assert.Equal(SharedMarketDataCacheReadStatus.HitFresh, rawDepthRead.Status);
        Assert.Equal(770001, rawDepthRead.Entry?.Payload.LastUpdateId);
    }

    [Fact]
    public async Task MarketDataService_AndIndicatorDataService_ReadSameSharedKlineSnapshot_FromIndependentConsumerInstance()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 18, 20, 0, TimeSpan.Zero);
        await using var fakeRedis = await FakeRedisServer.StartAsync();
        using var workerMemoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 });
        using var webMemoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 });
        var workerPolicyProvider = CreatePolicyProvider();
        var webPolicyProvider = CreatePolicyProvider();
        var workerService = new MarketDataService(
            new SharedSymbolRegistry(
                workerMemoryCache,
                workerPolicyProvider,
                NullLogger<SharedSymbolRegistry>.Instance),
            workerMemoryCache,
            workerPolicyProvider,
            new MarketPriceStreamHub(),
            CreateCache(fakeRedis.ConnectionString, nowUtc),
            new FixedTimeProvider(nowUtc),
            NullLogger<MarketDataService>.Instance);
        var webService = new MarketDataService(
            new SharedSymbolRegistry(
                webMemoryCache,
                webPolicyProvider,
                NullLogger<SharedSymbolRegistry>.Instance),
            webMemoryCache,
            webPolicyProvider,
            new MarketPriceStreamHub(),
            CreateCache(fakeRedis.ConnectionString, nowUtc.AddSeconds(2)),
            new FixedTimeProvider(nowUtc.AddSeconds(2)),
            NullLogger<MarketDataService>.Instance);
        var indicatorDataService = new IndicatorDataService(
            webService,
            new IndicatorStreamHub(),
            Options.Create(new IndicatorEngineOptions()),
            NullLogger<IndicatorDataService>.Instance);
        var sharedKline = new MarketCandleSnapshot(
            "btcusdt",
            "1M",
            nowUtc.UtcDateTime.AddMinutes(-1),
            nowUtc.UtcDateTime.AddMilliseconds(-1),
            65200m,
            65300m,
            65100m,
            65250m,
            50m,
            true,
            nowUtc.UtcDateTime,
            "Binance.WebSocket.Kline");

        var writeResult = await workerService.RecordKlineAsync(sharedKline);

        var webRead = await webService.ReadLatestKlineAsync("BTCUSDT", "1m");
        var indicatorSnapshot = await indicatorDataService.GetLatestAsync("BTCUSDT", "1m");

        Assert.Equal(SharedMarketDataProjectionStatus.Accepted, writeResult.Status);
        Assert.Equal(SharedMarketDataCacheReadStatus.HitFresh, webRead.Status);
        Assert.NotNull(webRead.Entry);
        Assert.Equal("BTCUSDT", webRead.Entry!.Payload.Symbol);
        Assert.Equal("1m", webRead.Entry.Payload.Interval);
        Assert.Equal(65250m, webRead.Entry.Payload.ClosePrice);
        Assert.Equal(sharedKline.CloseTimeUtc, webRead.Entry.UpdatedAtUtc);
        Assert.NotNull(indicatorSnapshot);
        Assert.Equal("BTCUSDT", indicatorSnapshot!.Symbol);
        Assert.Equal("1m", indicatorSnapshot.Timeframe);
        Assert.Equal(sharedKline.CloseTimeUtc, indicatorSnapshot.CloseTimeUtc);
        Assert.Equal(IndicatorDataState.WarmingUp, indicatorSnapshot.State);
        Assert.Equal(1, indicatorSnapshot.SampleCount);
    }

    [Fact]
    public async Task MarketDataService_ProjectsCacheHealthSnapshotAndConsumerParityAcrossScannerAndIndicatorConsumers()
    {
        var databaseName = $"CoinBotMarketDataCacheHealthInt_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var nowUtc = new DateTimeOffset(2026, 4, 3, 18, 30, 0, TimeSpan.Zero);
        await using var fakeRedis = await FakeRedisServer.StartAsync();
        await using var dbContext = CreateDbContext(connectionString);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        try
        {
            using var workerMemoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 });
            using var webMemoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 });
            using var adminMemoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 });
            var cacheCollector = new SharedMarketDataCacheObservabilityCollector(new FixedTimeProvider(nowUtc.AddSeconds(2)));
            var workerPolicyProvider = CreatePolicyProvider();
            var webPolicyProvider = CreatePolicyProvider();
            var workerService = new MarketDataService(
                new SharedSymbolRegistry(workerMemoryCache, workerPolicyProvider, NullLogger<SharedSymbolRegistry>.Instance),
                workerMemoryCache,
                workerPolicyProvider,
                new MarketPriceStreamHub(),
                CreateCache(fakeRedis.ConnectionString, nowUtc, cacheCollector),
                new FixedTimeProvider(nowUtc),
                NullLogger<MarketDataService>.Instance,
                cacheCollector);
            var webService = new MarketDataService(
                new SharedSymbolRegistry(webMemoryCache, webPolicyProvider, NullLogger<SharedSymbolRegistry>.Instance),
                webMemoryCache,
                webPolicyProvider,
                new MarketPriceStreamHub(),
                CreateCache(fakeRedis.ConnectionString, nowUtc.AddSeconds(2), cacheCollector),
                new FixedTimeProvider(nowUtc.AddSeconds(2)),
                NullLogger<MarketDataService>.Instance,
                cacheCollector);
            var indicatorDataService = new IndicatorDataService(
                webService,
                new IndicatorStreamHub(),
                Options.Create(new IndicatorEngineOptions()),
                NullLogger<IndicatorDataService>.Instance);

            var tickerWrite = await workerService.RecordPriceAsync(new MarketPriceSnapshot(
                "btcusdt",
                65250m,
                nowUtc.UtcDateTime.AddSeconds(-1),
                nowUtc.UtcDateTime,
                "Binance.WebSocket.Ticker"));
            var klineWrite = await workerService.RecordKlineAsync(new MarketCandleSnapshot(
                "btcusdt",
                "1m",
                nowUtc.UtcDateTime.AddMinutes(-1),
                nowUtc.UtcDateTime.AddMilliseconds(-1),
                65100m,
                65300m,
                65000m,
                65250m,
                10m,
                true,
                nowUtc.UtcDateTime,
                "Binance.WebSocket.Kline"));
            var depthWrite = await workerService.RecordDepthAsync(new MarketDepthSnapshot(
                "btcusdt",
                [new MarketDepthLevelSnapshot(65249.5m, 1.5m)],
                [new MarketDepthLevelSnapshot(65250.5m, 1.25m)],
                LastUpdateId: 700001,
                EventTimeUtc: nowUtc.UtcDateTime,
                ReceivedAtUtc: nowUtc.UtcDateTime.AddMilliseconds(25),
                Source: "Binance.WebSocket.Depth"));

            var tickerRead = await webService.ReadLatestPriceAsync("BTCUSDT");
            var klineRead = await webService.ReadLatestKlineAsync("BTCUSDT", "1m");
            var depthRead = await webService.ReadLatestDepthAsync("BTCUSDT");
            var indicatorSnapshot = await indicatorDataService.GetLatestAsync("BTCUSDT", "1m");
            var adminService = new AdminMonitoringReadModelService(
                dbContext,
                adminMemoryCache,
                new FixedTimeProvider(nowUtc.AddSeconds(2)),
                Options.Create(new DataLatencyGuardOptions()),
                cacheCollector);

            var dashboardSnapshot = await adminService.GetSnapshotAsync();

            Assert.Equal(SharedMarketDataProjectionStatus.Accepted, tickerWrite.Status);
            Assert.Equal(SharedMarketDataProjectionStatus.Accepted, klineWrite.Status);
            Assert.Equal(SharedMarketDataProjectionStatus.Accepted, depthWrite.Status);
            Assert.Equal(SharedMarketDataCacheReadStatus.HitFresh, tickerRead.Status);
            Assert.Equal(SharedMarketDataCacheReadStatus.HitFresh, klineRead.Status);
            Assert.Equal(SharedMarketDataCacheReadStatus.HitFresh, depthRead.Status);
            Assert.NotNull(tickerRead.Entry);
            Assert.NotNull(klineRead.Entry);
            Assert.NotNull(depthRead.Entry);
            Assert.NotNull(indicatorSnapshot);
            Assert.Equal(65250m, tickerRead.Entry!.Payload.Price);
            Assert.Equal(klineRead.Entry!.UpdatedAtUtc, indicatorSnapshot!.CloseTimeUtc);
            Assert.Equal("BTCUSDT", indicatorSnapshot.Symbol);
            Assert.Equal("1m", indicatorSnapshot.Timeframe);
            Assert.Equal(700001, depthRead.Entry!.Payload.LastUpdateId);
            Assert.True(dashboardSnapshot.MarketDataCache.HitCount >= 3);
            Assert.True(dashboardSnapshot.MarketDataCache.MissCount >= 2);

            var tickerScope = Assert.Single(dashboardSnapshot.MarketDataCache.SymbolFreshness, item => item.DataType == SharedMarketDataCacheDataType.Ticker && item.Symbol == "BTCUSDT");
            Assert.Equal(SharedMarketDataCacheReadStatus.HitFresh, tickerScope.LastReadStatus);
            Assert.Equal(SharedMarketDataCacheStaleReasonCode.Fresh, tickerScope.StaleReasonCode);
            Assert.Equal("Binance.WebSocket.Ticker", tickerScope.SourceLayer);
            Assert.Equal(tickerRead.Entry.UpdatedAtUtc, tickerScope.UpdatedAtUtc);

            var klineScope = Assert.Single(dashboardSnapshot.MarketDataCache.SymbolFreshness, item => item.DataType == SharedMarketDataCacheDataType.Kline && item.Symbol == "BTCUSDT" && item.Timeframe == "1m");
            Assert.Equal(SharedMarketDataCacheReadStatus.HitFresh, klineScope.LastReadStatus);
            Assert.Equal(SharedMarketDataCacheStaleReasonCode.Fresh, klineScope.StaleReasonCode);
            Assert.Equal("Binance.WebSocket.Kline", klineScope.SourceLayer);
            Assert.Equal(klineRead.Entry.UpdatedAtUtc, klineScope.UpdatedAtUtc);

            var klineStream = Assert.Single(dashboardSnapshot.MarketDataCache.StreamSnapshots, item => item.DataType == SharedMarketDataCacheDataType.Kline);
            Assert.Equal("BTCUSDT", klineStream.Symbol);
            Assert.Equal("1m", klineStream.Timeframe);
            Assert.Equal("Binance.WebSocket.Kline", klineStream.SourceLayer);

            var depthScope = Assert.Single(dashboardSnapshot.MarketDataCache.SymbolFreshness, item => item.DataType == SharedMarketDataCacheDataType.Depth && item.Symbol == "BTCUSDT");
            Assert.Equal(SharedMarketDataCacheReadStatus.HitFresh, depthScope.LastReadStatus);
            Assert.Equal(SharedMarketDataCacheStaleReasonCode.Fresh, depthScope.StaleReasonCode);
            Assert.Equal("Binance.WebSocket.Depth", depthScope.SourceLayer);
            Assert.Equal(depthRead.Entry.UpdatedAtUtc, depthScope.UpdatedAtUtc);
        }
        finally
        {
            await dbContext.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task MarketScannerService_ReadsLatestTickerFromSharedRedisCache_WithoutLegacyPriceFallback()
    {
        var databaseName = $"CoinBotMarketScannerSharedReadInt_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var nowUtc = new DateTimeOffset(2026, 4, 3, 18, 40, 0, TimeSpan.Zero);
        await using var fakeRedis = await FakeRedisServer.StartAsync();
        await using var dbContext = CreateDbContext(connectionString);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        try
        {
            SeedScannerCandles(dbContext, "BTCUSDT", nowUtc.UtcDateTime, closePrice: 100m, volume: 2_000m);
            SeedScannerCandles(dbContext, "SOLUSDT", nowUtc.UtcDateTime, closePrice: 25m, volume: 0.1m);
            await dbContext.SaveChangesAsync();

            using var workerMemoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 });
            using var scannerMemoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 });
            var workerPolicyProvider = CreatePolicyProvider();
            var scannerPolicyProvider = CreatePolicyProvider();
            var workerService = new MarketDataService(
                new SharedSymbolRegistry(
                    workerMemoryCache,
                    workerPolicyProvider,
                    NullLogger<SharedSymbolRegistry>.Instance),
                workerMemoryCache,
                workerPolicyProvider,
                new MarketPriceStreamHub(),
                CreateCache(fakeRedis.ConnectionString, nowUtc),
                new FixedTimeProvider(nowUtc),
                NullLogger<MarketDataService>.Instance);
            var scannerInnerService = new MarketDataService(
                new SharedSymbolRegistry(
                    scannerMemoryCache,
                    scannerPolicyProvider,
                    NullLogger<SharedSymbolRegistry>.Instance),
                scannerMemoryCache,
                scannerPolicyProvider,
                new MarketPriceStreamHub(),
                CreateCache(fakeRedis.ConnectionString, nowUtc.AddSeconds(1)),
                new FixedTimeProvider(nowUtc.AddSeconds(1)),
                NullLogger<MarketDataService>.Instance);
            var scannerMarketDataService = new ScannerSharedReadProbeMarketDataService(scannerInnerService);

            await workerService.RecordPriceAsync(new MarketPriceSnapshot(
                "BTCUSDT",
                100m,
                nowUtc.UtcDateTime,
                nowUtc.UtcDateTime,
                "Binance.WebSocket.Ticker"));
            await workerService.RecordPriceAsync(new MarketPriceSnapshot(
                "SOLUSDT",
                25m,
                nowUtc.UtcDateTime,
                nowUtc.UtcDateTime,
                "Binance.WebSocket.Ticker"));

            var scannerService = new MarketScannerService(
                dbContext,
                scannerMarketDataService,
                new FakeSharedSymbolRegistry([
                    new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime),
                    new SymbolMetadataSnapshot("SOLUSDT", "Binance", "SOL", "USDT", 0.1m, 0.001m, "TRADING", true, nowUtc.UtcDateTime)
                ]),
                Options.Create(new MarketScannerOptions
                {
                    TopCandidateCount = 1,
                    MaxUniverseSymbols = 10,
                    Min24hQuoteVolume = 1_000m,
                    MaxDataAgeSeconds = 120,
                    AllowedQuoteAssets = ["USDT"],
                    HandoffEnabled = false
                }),
                Options.Create(new BinanceMarketDataOptions
                {
                    KlineInterval = "1m",
                    SeedSymbols = ["BTCUSDT", "SOLUSDT"]
                }),
                new FixedTimeProvider(nowUtc.AddSeconds(1)),
                NullLogger<MarketScannerService>.Instance);

            var cycle = await scannerService.RunOnceAsync();

            var candidateRows = await dbContext.MarketScannerCandidates
                .AsNoTracking()
                .Where(entity => entity.ScanCycleId == cycle.Id)
                .OrderBy(entity => entity.Symbol)
                .ToListAsync();

            var btcCandidate = Assert.Single(candidateRows, candidate => candidate.Symbol == "BTCUSDT");
            var solCandidate = Assert.Single(candidateRows, candidate => candidate.Symbol == "SOLUSDT");

            Assert.Equal("BTCUSDT", cycle.BestCandidateSymbol);
            Assert.True(btcCandidate.IsEligible);
            Assert.Equal(100m, btcCandidate.LastPrice);
            Assert.Equal(1, btcCandidate.Rank);
            Assert.False(solCandidate.IsEligible);
            Assert.Equal("LowQuoteVolume", solCandidate.RejectionReason);
            Assert.Equal(2, scannerMarketDataService.SharedPriceReadCount);
            Assert.Equal(0, scannerMarketDataService.LegacyPriceReadCount);
        }
        finally
        {
            await dbContext.Database.EnsureDeletedAsync();
        }
    }

    private static MarketDataCachePolicyProvider CreatePolicyProvider()
    {
        return new MarketDataCachePolicyProvider(Options.Create(new InMemoryCacheOptions
        {
            SizeLimit = 64,
            SymbolMetadataTtlMinutes = 60,
            LatestPriceTtlSeconds = 15
        }));
    }

    private static RedisSharedMarketDataCache CreateCache(string connectionString, DateTimeOffset nowUtc, ISharedMarketDataCacheObservabilityCollector? cacheCollector = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Redis"] = connectionString
            })
            .Build();

        return new RedisSharedMarketDataCache(
            configuration,
            new FixedTimeProvider(nowUtc),
            NullLogger<RedisSharedMarketDataCache>.Instance,
            cacheCollector);
    }

    private static ApplicationDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private static void SeedScannerCandles(
        ApplicationDbContext dbContext,
        string symbol,
        DateTime latestCloseTimeUtc,
        decimal closePrice,
        decimal volume)
    {
        var firstOpenTimeUtc = latestCloseTimeUtc.AddMinutes(-10);

        for (var index = 0; index < 10; index++)
        {
            var openTimeUtc = firstOpenTimeUtc.AddMinutes(index);
            dbContext.HistoricalMarketCandles.Add(new HistoricalMarketCandle
            {
                Id = Guid.NewGuid(),
                Symbol = symbol,
                Interval = "1m",
                OpenTimeUtc = openTimeUtc,
                CloseTimeUtc = openTimeUtc.AddMinutes(1),
                OpenPrice = closePrice,
                HighPrice = closePrice,
                LowPrice = closePrice,
                ClosePrice = closePrice,
                Volume = volume,
                ReceivedAtUtc = openTimeUtc.AddMinutes(1),
                Source = "integration-test"
            });
        }
    }
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }

    private sealed class FakeRedisServer : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly ConcurrentDictionary<string, string> values = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource cancellationTokenSource = new();
        private readonly Task serverTask;

        private FakeRedisServer(TcpListener listener)
        {
            this.listener = listener;
            serverTask = Task.Run(() => RunAsync(cancellationTokenSource.Token));
        }

        public string ConnectionString { get; private set; } = string.Empty;

        public static async Task<FakeRedisServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            var server = new FakeRedisServer(listener)
            {
                ConnectionString = $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"
            };

            await Task.Yield();
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            cancellationTokenSource.Cancel();
            listener.Stop();

            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }

            cancellationTokenSource.Dispose();
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var tcpClient = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(tcpClient, cancellationToken), cancellationToken);
            }
        }

        private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken cancellationToken)
        {
            using var client = tcpClient;
            var stream = client.GetStream();
            var command = await ReadCommandAsync(stream, cancellationToken);

            if (command.Count >= 1 && string.Equals(command[0], "GET", StringComparison.OrdinalIgnoreCase))
            {
                if (command.Count < 2 || !values.TryGetValue(command[1], out var value))
                {
                    await WriteAsciiAsync(stream, "$-1\r\n", cancellationToken);
                    return;
                }

                var bytes = Encoding.UTF8.GetBytes(value);
                await WriteAsciiAsync(stream, $"${bytes.Length}\r\n", cancellationToken);
                await stream.WriteAsync(bytes, cancellationToken);
                await WriteAsciiAsync(stream, "\r\n", cancellationToken);
                return;
            }

            if (command.Count >= 3 && string.Equals(command[0], "SET", StringComparison.OrdinalIgnoreCase))
            {
                values[command[1]] = command[2];
                await WriteAsciiAsync(stream, "+OK\r\n", cancellationToken);
                return;
            }

            await WriteAsciiAsync(stream, "-ERR unsupported\r\n", cancellationToken);
        }

        private static async Task<IReadOnlyList<string>> ReadCommandAsync(Stream stream, CancellationToken cancellationToken)
        {
            if (await ReadByteAsync(stream, cancellationToken) != '*')
            {
                throw new InvalidOperationException("Invalid Redis command frame.");
            }

            var count = int.Parse(await ReadLineAsync(stream, cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            var parts = new List<string>(count);

            for (var index = 0; index < count; index++)
            {
                if (await ReadByteAsync(stream, cancellationToken) != '$')
                {
                    throw new InvalidOperationException("Invalid Redis bulk string frame.");
                }

                var length = int.Parse(await ReadLineAsync(stream, cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
                var buffer = new byte[length];
                var offset = 0;

                while (offset < length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
                    if (read == 0)
                    {
                        throw new IOException("Redis command frame ended unexpectedly.");
                    }

                    offset += read;
                }

                if (await ReadByteAsync(stream, cancellationToken) != '\r' ||
                    await ReadByteAsync(stream, cancellationToken) != '\n')
                {
                    throw new InvalidOperationException("Invalid Redis command terminator.");
                }

                parts.Add(Encoding.UTF8.GetString(buffer));
            }

            return parts;
        }

        private static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[1];
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                throw new IOException("Redis stream closed unexpectedly.");
            }

            return buffer[0];
        }

        private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
        {
            var bytes = new List<byte>(16);
            while (true)
            {
                var next = await ReadByteAsync(stream, cancellationToken);
                if (next == '\r')
                {
                    if (await ReadByteAsync(stream, cancellationToken) != '\n')
                    {
                        throw new InvalidOperationException("Invalid Redis line terminator.");
                    }

                    break;
                }

                bytes.Add((byte)next);
            }

            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private static async Task WriteAsciiAsync(Stream stream, string value, CancellationToken cancellationToken)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(value), cancellationToken);
        }
    }

    private sealed class FakeExchangeInfoClient(DateTime updatedAtUtc) : IBinanceExchangeInfoClient
    {
        public Task<IReadOnlyCollection<SymbolMetadataSnapshot>> GetSymbolMetadataAsync(
            IReadOnlyCollection<string> symbols,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyCollection<SymbolMetadataSnapshot> snapshots = symbols
                .Select(symbol => new SymbolMetadataSnapshot(
                    symbol,
                    "Binance",
                    symbol[..^4],
                    symbol[^4..],
                    0.01m,
                    0.0001m,
                    "TRADING",
                    true,
                    updatedAtUtc))
                .ToArray();

            return Task.FromResult(snapshots);
        }

        public Task<DateTime?> GetServerTimeUtcAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<DateTime?>(updatedAtUtc);
        }
    }

    private sealed class FakeCandleStreamClient(IReadOnlyCollection<MarketCandleSnapshot> snapshots) : IBinanceCandleStreamClient
    {
        public async IAsyncEnumerable<MarketCandleSnapshot> StreamAsync(
            IReadOnlyCollection<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var snapshot in snapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return snapshot;
                await Task.Yield();
            }
        }
    }

    private sealed class FakeDepthStreamClient(IReadOnlyCollection<MarketDepthSnapshot> snapshots) : IBinanceDepthStreamClient
    {
        public async IAsyncEnumerable<MarketDepthSnapshot> StreamAsync(
            IReadOnlyCollection<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var snapshot in snapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return snapshot;
                await Task.Yield();
            }
        }
    }

    private sealed class FakeHeartbeatRecorder : IMarketDataHeartbeatRecorder
    {
        public Task RecordAsync(CandleDataQualityGuardResult guardResult, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeSharedSymbolRegistry(IReadOnlyCollection<SymbolMetadataSnapshot> snapshots) : ISharedSymbolRegistry
    {
        public ValueTask<SymbolMetadataSnapshot?> GetSymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(snapshots.SingleOrDefault(item => item.Symbol == symbol));
        }

        public ValueTask<IReadOnlyCollection<SymbolMetadataSnapshot>> ListSymbolsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(snapshots);
        }
    }

    private sealed class ScannerSharedReadProbeMarketDataService(IMarketDataService innerMarketDataService) : IMarketDataService
    {
        public int SharedPriceReadCount { get; private set; }

        public int LegacyPriceReadCount { get; private set; }

        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return innerMarketDataService.TrackSymbolAsync(symbol, cancellationToken);
        }

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
        {
            return innerMarketDataService.TrackSymbolsAsync(symbols, cancellationToken);
        }

        public async ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(
            string symbol,
            CancellationToken cancellationToken = default)
        {
            LegacyPriceReadCount++;
            return await innerMarketDataService.GetLatestPriceAsync(symbol, cancellationToken);
        }

        public async ValueTask<SharedMarketDataCacheReadResult<MarketPriceSnapshot>> ReadLatestPriceAsync(
            string symbol,
            CancellationToken cancellationToken = default)
        {
            SharedPriceReadCount++;
            return await innerMarketDataService.ReadLatestPriceAsync(symbol, cancellationToken);
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(
            string symbol,
            CancellationToken cancellationToken = default)
        {
            return innerMarketDataService.GetSymbolMetadataAsync(symbol, cancellationToken);
        }

        public IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            CancellationToken cancellationToken = default)
        {
            return innerMarketDataService.WatchAsync(symbols, cancellationToken);
        }
    }
}


