using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.MarketData;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.MarketData;

public sealed class BinanceWebSocketManagerTests
{
    [Fact]
    public async Task RunCycleAsync_RefreshesMetadata_CachesLatestPrice_AndBroadcastsClosedCandleToMultipleConsumers()
    {
        var now = new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(now);
        using var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 });
        var cachePolicyProvider = new MarketDataCachePolicyProvider(Options.Create(new InMemoryCacheOptions
        {
            SizeLimit = 64,
            SymbolMetadataTtlMinutes = 60,
            LatestPriceTtlSeconds = 15
        }));
        var symbolRegistry = new SharedSymbolRegistry(
            memoryCache,
            cachePolicyProvider,
            NullLogger<SharedSymbolRegistry>.Instance);
        var marketDataService = new MarketDataService(
            symbolRegistry,
            memoryCache,
            cachePolicyProvider,
            new MarketPriceStreamHub(),
            new TestSharedMarketDataCache(),
            timeProvider,
            NullLogger<MarketDataService>.Instance);
        var indicatorDataService = new IndicatorDataService(
            marketDataService,
            new IndicatorStreamHub(),
            Options.Create(new IndicatorEngineOptions()),
            NullLogger<IndicatorDataService>.Instance);
        var exchangeInfoClient = new FakeExchangeInfoClient(
        [
            new SymbolMetadataSnapshot(
                "BTCUSDT",
                "Binance",
                "BTC",
                "USDT",
                0.01m,
                0.0001m,
                "TRADING",
                true,
                now.UtcDateTime)
        ]);
        var candleStreamClient = new FakeCandleStreamClient(
        [
            CreateClosedCandleSnapshot(
                "BTCUSDT",
                openTimeUtc: now.UtcDateTime,
                closeTimeUtc: now.UtcDateTime.AddMinutes(1).AddMilliseconds(-1),
                closePrice: 64000.50m)
        ]);
        var heartbeatRecorder = new FakeHeartbeatRecorder();
        var manager = CreateManager(
            symbolRegistry,
            marketDataService,
            indicatorDataService,
            exchangeInfoClient,
            candleStreamClient,
            heartbeatRecorder,
            timeProvider);

        await marketDataService.TrackSymbolAsync("btcusdt");

        using var consumerOneCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var consumerTwoCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var indicatorConsumerCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var consumerOne = marketDataService
            .WatchAsync(["BTCUSDT"], consumerOneCts.Token)
            .GetAsyncEnumerator(consumerOneCts.Token);
        await using var consumerTwo = marketDataService
            .WatchAsync(["BTCUSDT"], consumerTwoCts.Token)
            .GetAsyncEnumerator(consumerTwoCts.Token);
        await using var indicatorConsumer = indicatorDataService
            .WatchAsync([new IndicatorSubscription("BTCUSDT", "1m")], indicatorConsumerCts.Token)
            .GetAsyncEnumerator(indicatorConsumerCts.Token);

        var consumerOneMoveNextTask = consumerOne.MoveNextAsync().AsTask();
        var consumerTwoMoveNextTask = consumerTwo.MoveNextAsync().AsTask();
        var indicatorMoveNextTask = indicatorConsumer.MoveNextAsync().AsTask();

        await manager.RunCycleAsync();

        Assert.True(await consumerOneMoveNextTask);
        Assert.True(await consumerTwoMoveNextTask);
        Assert.True(await indicatorMoveNextTask);
        Assert.Equal(consumerOne.Current, consumerTwo.Current);
        Assert.Equal("BTCUSDT", consumerOne.Current.Symbol);
        Assert.Equal(64000.50m, consumerOne.Current.Price);
        Assert.Equal("BTCUSDT", indicatorConsumer.Current.Symbol);
        Assert.Equal("1m", indicatorConsumer.Current.Timeframe);
        Assert.Equal(IndicatorDataState.WarmingUp, indicatorConsumer.Current.State);
        Assert.Equal(1, indicatorConsumer.Current.SampleCount);
        Assert.Single(heartbeatRecorder.RecordedResults);
        Assert.True(heartbeatRecorder.RecordedResults[0].IsAccepted);

        var latestPrice = await marketDataService.GetLatestPriceAsync("BTCUSDT");
        var latestKline = await marketDataService.GetLatestKlineAsync("BTCUSDT", "1m");
        var latestIndicator = await indicatorDataService.GetLatestAsync("BTCUSDT", "1m");
        var metadata = await marketDataService.GetSymbolMetadataAsync("BTCUSDT");
        var listedSymbols = await symbolRegistry.ListSymbolsAsync();

        Assert.NotNull(latestPrice);
        Assert.NotNull(latestKline);
        Assert.NotNull(latestIndicator);
        Assert.Equal("BTCUSDT", latestKline!.Symbol);
        Assert.Equal("1m", latestKline.Interval);
        Assert.Equal(64000.50m, latestKline.ClosePrice);
        Assert.True(latestKline.IsClosed);
        Assert.NotNull(metadata);
        Assert.Single(listedSymbols);
        Assert.Equal(IndicatorDataState.WarmingUp, latestIndicator!.State);
        Assert.Equal(DegradedModeReasonCode.None, latestIndicator.DataQualityReasonCode);
        Assert.Equal(0.01m, metadata!.TickSize);
        Assert.Equal(0.0001m, metadata.StepSize);
        Assert.Equal("TRADING", metadata.TradingStatus);
        Assert.True(metadata.IsTradingEnabled);
        Assert.Equal(1, candleStreamClient.InvocationCount);
        Assert.Equal(["BTCUSDT"], exchangeInfoClient.RequestedSymbols);
    }

    [Fact]
    public async Task RunCycleAsync_BlocksDiscontinuousGapCandle_AndKeepsLastGoodPrice()
    {
        var now = new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(now);
        using var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 });
        var cachePolicyProvider = new MarketDataCachePolicyProvider(Options.Create(new InMemoryCacheOptions
        {
            SizeLimit = 64,
            SymbolMetadataTtlMinutes = 60,
            LatestPriceTtlSeconds = 15
        }));
        var symbolRegistry = new SharedSymbolRegistry(
            memoryCache,
            cachePolicyProvider,
            NullLogger<SharedSymbolRegistry>.Instance);
        var marketDataService = new MarketDataService(
            symbolRegistry,
            memoryCache,
            cachePolicyProvider,
            new MarketPriceStreamHub(),
            new TestSharedMarketDataCache(),
            timeProvider,
            NullLogger<MarketDataService>.Instance);
        var indicatorDataService = new IndicatorDataService(
            marketDataService,
            new IndicatorStreamHub(),
            Options.Create(new IndicatorEngineOptions()),
            NullLogger<IndicatorDataService>.Instance);
        var exchangeInfoClient = new FakeExchangeInfoClient(
        [
            new SymbolMetadataSnapshot(
                "BTCUSDT",
                "Binance",
                "BTC",
                "USDT",
                0.01m,
                0.0001m,
                "TRADING",
                true,
                now.UtcDateTime)
        ]);
        var candleStreamClient = new FakeCandleStreamClient(
        [
            CreateClosedCandleSnapshot(
                "BTCUSDT",
                openTimeUtc: now.UtcDateTime,
                closeTimeUtc: now.UtcDateTime.AddMinutes(1).AddMilliseconds(-1),
                closePrice: 64000.50m),
            CreateClosedCandleSnapshot(
                "BTCUSDT",
                openTimeUtc: now.UtcDateTime.AddMinutes(2),
                closeTimeUtc: now.UtcDateTime.AddMinutes(3).AddMilliseconds(-1),
                closePrice: 64120.10m)
        ]);
        var heartbeatRecorder = new FakeHeartbeatRecorder();
        var manager = CreateManager(
            symbolRegistry,
            marketDataService,
            indicatorDataService,
            exchangeInfoClient,
            candleStreamClient,
            heartbeatRecorder,
            timeProvider);

        await marketDataService.TrackSymbolAsync("BTCUSDT");

        await manager.RunCycleAsync();

        var latestPrice = await marketDataService.GetLatestPriceAsync("BTCUSDT");
        var latestKline = await marketDataService.GetLatestKlineAsync("BTCUSDT", "1m");
        var latestIndicator = await indicatorDataService.GetLatestAsync("BTCUSDT", "1m");

        Assert.NotNull(latestPrice);
        Assert.NotNull(latestKline);
        Assert.NotNull(latestIndicator);
        Assert.Equal(64000.50m, latestPrice!.Price);
        Assert.Equal(64000.50m, latestKline!.ClosePrice);
        Assert.Equal(now.UtcDateTime, latestKline.OpenTimeUtc);
        Assert.Equal(now.UtcDateTime.AddMinutes(1).AddMilliseconds(-1), latestKline.CloseTimeUtc);
        Assert.Equal(IndicatorDataState.MissingData, latestIndicator!.State);
        Assert.Equal(DegradedModeReasonCode.CandleDataGapDetected, latestIndicator.DataQualityReasonCode);
        Assert.Equal(1, latestIndicator.SampleCount);
        Assert.False(latestIndicator.Rsi.IsReady);
        Assert.False(latestIndicator.Macd.IsReady);
        Assert.False(latestIndicator.Bollinger.IsReady);
        Assert.Equal(2, heartbeatRecorder.RecordedResults.Count);
        Assert.True(heartbeatRecorder.RecordedResults[0].IsAccepted);
        Assert.False(heartbeatRecorder.RecordedResults[1].IsAccepted);
        Assert.Equal(CoinBot.Domain.Enums.DegradedModeReasonCode.CandleDataGapDetected, heartbeatRecorder.RecordedResults[1].GuardReasonCode);
    }

    [Fact]
    public async Task RunCycleAsync_DoesNotCallMoveNextConcurrently_WhenPollingForRestartSignals()
    {
        var now = new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(now);
        using var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 });
        var cachePolicyProvider = new MarketDataCachePolicyProvider(Options.Create(new InMemoryCacheOptions
        {
            SizeLimit = 64,
            SymbolMetadataTtlMinutes = 60,
            LatestPriceTtlSeconds = 15
        }));
        var symbolRegistry = new SharedSymbolRegistry(
            memoryCache,
            cachePolicyProvider,
            NullLogger<SharedSymbolRegistry>.Instance);
        var marketDataService = new MarketDataService(
            symbolRegistry,
            memoryCache,
            cachePolicyProvider,
            new MarketPriceStreamHub(),
            new TestSharedMarketDataCache(),
            timeProvider,
            NullLogger<MarketDataService>.Instance);
        var indicatorDataService = new IndicatorDataService(
            marketDataService,
            new IndicatorStreamHub(),
            Options.Create(new IndicatorEngineOptions()),
            NullLogger<IndicatorDataService>.Instance);
        var exchangeInfoClient = new FakeExchangeInfoClient(
        [
            new SymbolMetadataSnapshot(
                "BTCUSDT",
                "Binance",
                "BTC",
                "USDT",
                0.01m,
                0.0001m,
                "TRADING",
                true,
                now.UtcDateTime)
        ]);
        var candleStreamClient = new BlockingCandleStreamClient();
        var heartbeatRecorder = new FakeHeartbeatRecorder();
        var manager = CreateManager(
            symbolRegistry,
            marketDataService,
            indicatorDataService,
            exchangeInfoClient,
            candleStreamClient,
            heartbeatRecorder,
            timeProvider);

        await marketDataService.TrackSymbolAsync("BTCUSDT");

        var runTask = manager.RunCycleAsync();

        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        Assert.False(candleStreamClient.ConcurrentMoveNextDetected);

        candleStreamClient.CompleteCurrentMoveNext(false);
        await runTask;

        Assert.Equal(1, candleStreamClient.MoveNextCallCount);
    }

    private static BinanceWebSocketManager CreateManager(
        SharedSymbolRegistry symbolRegistry,
        MarketDataService marketDataService,
        IndicatorDataService indicatorDataService,
        FakeExchangeInfoClient exchangeInfoClient,
        IBinanceCandleStreamClient candleStreamClient,
        FakeHeartbeatRecorder heartbeatRecorder,
        AdjustableTimeProvider timeProvider)
    {
        return new BinanceWebSocketManager(
            symbolRegistry,
            marketDataService,
            indicatorDataService,
            exchangeInfoClient,
            candleStreamClient,
            new CandleDataQualityGuard(
                new CandleContinuityValidator(NullLogger<CandleContinuityValidator>.Instance),
                NullLogger<CandleDataQualityGuard>.Instance),
            heartbeatRecorder,
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
            timeProvider,
            NullLogger<BinanceWebSocketManager>.Instance);
    }

    private static MarketCandleSnapshot CreateClosedCandleSnapshot(
        string symbol,
        DateTime openTimeUtc,
        DateTime closeTimeUtc,
        decimal closePrice)
    {
        return new MarketCandleSnapshot(
            symbol,
            "1m",
            openTimeUtc,
            closeTimeUtc,
            OpenPrice: closePrice,
            HighPrice: closePrice,
            LowPrice: closePrice,
            ClosePrice: closePrice,
            Volume: 12.5m,
            IsClosed: true,
            ReceivedAtUtc: closeTimeUtc,
            Source: "Binance.WebSocket.Kline");
    }

    private sealed class FakeExchangeInfoClient(IReadOnlyCollection<SymbolMetadataSnapshot> snapshots) : IBinanceExchangeInfoClient
    {
        public IReadOnlyCollection<string> RequestedSymbols { get; private set; } = Array.Empty<string>();

        public Task<IReadOnlyCollection<SymbolMetadataSnapshot>> GetSymbolMetadataAsync(
            IReadOnlyCollection<string> symbols,
            CancellationToken cancellationToken = default)
        {
            RequestedSymbols = symbols.OrderBy(symbol => symbol, StringComparer.Ordinal).ToArray();
            return Task.FromResult(snapshots);
        }

        public Task<DateTime?> GetServerTimeUtcAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<DateTime?>(null);
        }
    }

    private sealed class FakeCandleStreamClient(IReadOnlyCollection<MarketCandleSnapshot> snapshots) : IBinanceCandleStreamClient
    {
        public int InvocationCount { get; private set; }

        public async IAsyncEnumerable<MarketCandleSnapshot> StreamAsync(
            IReadOnlyCollection<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            InvocationCount++;

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
        public List<CandleDataQualityGuardResult> RecordedResults { get; } = [];

        public Task RecordAsync(CandleDataQualityGuardResult guardResult, CancellationToken cancellationToken = default)
        {
            RecordedResults.Add(guardResult);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingCandleStreamClient : IBinanceCandleStreamClient
    {
        private readonly TaskCompletionSource<bool> moveNextCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MoveNextCallCount { get; private set; }

        public bool ConcurrentMoveNextDetected { get; private set; }

        public IAsyncEnumerable<MarketCandleSnapshot> StreamAsync(
            IReadOnlyCollection<string> symbols,
            CancellationToken cancellationToken = default)
        {
            return new BlockingAsyncEnumerable(this, cancellationToken);
        }

        public void CompleteCurrentMoveNext(bool result)
        {
            moveNextCompletionSource.TrySetResult(result);
        }

        private sealed class BlockingAsyncEnumerable(
            BlockingCandleStreamClient owner,
            CancellationToken cancellationToken) : IAsyncEnumerable<MarketCandleSnapshot>, IAsyncEnumerator<MarketCandleSnapshot>
        {
            private bool moveNextInFlight;

            public MarketCandleSnapshot Current => throw new InvalidOperationException("No candle snapshot is available.");

            public IAsyncEnumerator<MarketCandleSnapshot> GetAsyncEnumerator(CancellationToken enumeratorCancellationToken = default)
            {
                return this;
            }

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }

            public ValueTask<bool> MoveNextAsync()
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (moveNextInFlight)
                {
                    owner.ConcurrentMoveNextDetected = true;
                }

                moveNextInFlight = true;
                owner.MoveNextCallCount++;

                return AwaitMoveNextAsync();
            }

            private async ValueTask<bool> AwaitMoveNextAsync()
            {
                try
                {
                    return await owner.moveNextCompletionSource.Task;
                }
                finally
                {
                    moveNextInFlight = false;
                }
            }
        }
    }

    private sealed class TestSharedMarketDataCache : ISharedMarketDataCache
    {
        private readonly Dictionary<string, object> entries = new(StringComparer.Ordinal);

        public ValueTask<SharedMarketDataCacheWriteResult> WriteAsync<TPayload>(
            SharedMarketDataCacheEntry<TPayload> entry,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            entries[SharedMarketDataCacheKeyBuilder.Build(entry.DataType, entry.Symbol, entry.Timeframe)] = entry;

            return ValueTask.FromResult(SharedMarketDataCacheWriteResult.Written());
        }

        public ValueTask<SharedMarketDataCacheReadResult<TPayload>> ReadAsync<TPayload>(
            SharedMarketDataCacheDataType dataType,
            string symbol,
            string? timeframe,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!entries.TryGetValue(SharedMarketDataCacheKeyBuilder.Build(dataType, symbol, timeframe), out var entry) ||
                entry is not SharedMarketDataCacheEntry<TPayload> typedEntry)
            {
                return ValueTask.FromResult(SharedMarketDataCacheReadResult<TPayload>.Miss());
            }

            return ValueTask.FromResult(SharedMarketDataCacheReadResult<TPayload>.HitFresh(
                new SharedMarketDataCacheEntry<TPayload>(
                    typedEntry.DataType,
                    typedEntry.Symbol,
                    typedEntry.Timeframe,
                    typedEntry.UpdatedAtUtc,
                    typedEntry.CachedAtUtc,
                    typedEntry.FreshUntilUtc,
                    typedEntry.ExpiresAtUtc,
                    typedEntry.Source,
                    typedEntry.Payload)));
        }
    }
}
