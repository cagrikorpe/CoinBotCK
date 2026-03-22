using System.Runtime.CompilerServices;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.MarketData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.MarketData;

public sealed class IndicatorDataServiceTests
{
    [Fact]
    public async Task RecordAcceptedCandleAsync_KeepsSnapshotWarmingUp_UntilMacdSignalIsReady()
    {
        var marketDataService = new FakeMarketDataService();
        var service = CreateService(marketDataService);

        await service.TrackSymbolAsync("BTCUSDT");

        for (var index = 0; index < 20; index++)
        {
            await service.RecordAcceptedCandleAsync(CreateClosedCandleSnapshot("BTCUSDT", index, 100m));
        }

        var latestSnapshot = await service.GetLatestAsync("BTCUSDT", "1m");

        Assert.NotNull(latestSnapshot);
        Assert.Equal(["BTCUSDT"], marketDataService.TrackedSymbols);
        Assert.Equal(IndicatorDataState.WarmingUp, latestSnapshot!.State);
        Assert.Equal(20, latestSnapshot.SampleCount);
        Assert.Equal(34, latestSnapshot.RequiredSampleCount);
        Assert.Equal(DegradedModeReasonCode.None, latestSnapshot.DataQualityReasonCode);
        Assert.True(latestSnapshot.Rsi.IsReady);
        Assert.False(latestSnapshot.Macd.IsReady);
        Assert.True(latestSnapshot.Bollinger.IsReady);
        Assert.Equal(50m, latestSnapshot.Rsi.Value);
        Assert.Equal(100m, latestSnapshot.Bollinger.MiddleBand);
        Assert.Equal(100m, latestSnapshot.Bollinger.UpperBand);
        Assert.Equal(100m, latestSnapshot.Bollinger.LowerBand);
        Assert.Null(latestSnapshot.Macd.MacdLine);
    }

    [Fact]
    public async Task RecordAcceptedCandleAsync_PromotesSnapshotToReady_WhenWarmupCompletes()
    {
        var service = CreateService(new FakeMarketDataService());

        for (var index = 0; index < 34; index++)
        {
            await service.RecordAcceptedCandleAsync(CreateClosedCandleSnapshot("BTCUSDT", index, 100m));
        }

        var latestSnapshot = await service.GetLatestAsync("BTCUSDT", "1m");

        Assert.NotNull(latestSnapshot);
        Assert.Equal(IndicatorDataState.Ready, latestSnapshot!.State);
        Assert.Equal(34, latestSnapshot.SampleCount);
        Assert.Equal(34, latestSnapshot.RequiredSampleCount);
        Assert.Equal(DegradedModeReasonCode.None, latestSnapshot.DataQualityReasonCode);
        Assert.True(latestSnapshot.Rsi.IsReady);
        Assert.True(latestSnapshot.Macd.IsReady);
        Assert.True(latestSnapshot.Bollinger.IsReady);
        Assert.Equal(50m, latestSnapshot.Rsi.Value);
        Assert.Equal(0m, latestSnapshot.Macd.MacdLine);
        Assert.Equal(0m, latestSnapshot.Macd.SignalLine);
        Assert.Equal(0m, latestSnapshot.Macd.Histogram);
        Assert.Equal(100m, latestSnapshot.Bollinger.MiddleBand);
        Assert.Equal(100m, latestSnapshot.Bollinger.UpperBand);
        Assert.Equal(100m, latestSnapshot.Bollinger.LowerBand);
        Assert.Equal(0m, latestSnapshot.Bollinger.StandardDeviation);
    }

    [Fact]
    public async Task RecordRejectedCandleAsync_MarksSnapshotMissingData_AndClearsIndicatorValues()
    {
        var service = CreateService(new FakeMarketDataService());

        for (var index = 0; index < 34; index++)
        {
            await service.RecordAcceptedCandleAsync(CreateClosedCandleSnapshot("BTCUSDT", index, 100m));
        }

        var rejectedCandle = CreateClosedCandleSnapshot("BTCUSDT", 35, 100m);
        await service.RecordRejectedCandleAsync(
            rejectedCandle,
            new CandleDataQualityGuardResult(
                IsAccepted: false,
                GuardStateCode: DegradedModeStateCode.Stopped,
                GuardReasonCode: DegradedModeReasonCode.CandleDataGapDetected,
                EffectiveDataTimestampUtc: rejectedCandle.CloseTimeUtc,
                ExpectedOpenTimeUtc: rejectedCandle.OpenTimeUtc));

        var latestSnapshot = await service.GetLatestAsync("BTCUSDT", "1m");

        Assert.NotNull(latestSnapshot);
        Assert.Equal(IndicatorDataState.MissingData, latestSnapshot!.State);
        Assert.Equal(34, latestSnapshot.SampleCount);
        Assert.Equal(DegradedModeReasonCode.CandleDataGapDetected, latestSnapshot.DataQualityReasonCode);
        Assert.False(latestSnapshot.Rsi.IsReady);
        Assert.False(latestSnapshot.Macd.IsReady);
        Assert.False(latestSnapshot.Bollinger.IsReady);
        Assert.Null(latestSnapshot.Rsi.Value);
        Assert.Null(latestSnapshot.Macd.MacdLine);
        Assert.Null(latestSnapshot.Macd.SignalLine);
        Assert.Null(latestSnapshot.Macd.Histogram);
        Assert.Null(latestSnapshot.Bollinger.MiddleBand);
        Assert.Null(latestSnapshot.Bollinger.UpperBand);
        Assert.Null(latestSnapshot.Bollinger.LowerBand);
    }

    private static IndicatorDataService CreateService(FakeMarketDataService marketDataService)
    {
        return new IndicatorDataService(
            marketDataService,
            new IndicatorStreamHub(),
            Options.Create(new IndicatorEngineOptions()),
            NullLogger<IndicatorDataService>.Instance);
    }

    private static MarketCandleSnapshot CreateClosedCandleSnapshot(string symbol, int minuteOffset, decimal closePrice)
    {
        var openTimeUtc = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc).AddMinutes(minuteOffset);
        var closeTimeUtc = openTimeUtc.AddMinutes(1).AddMilliseconds(-1);

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

    private sealed class FakeMarketDataService : IMarketDataService
    {
        private readonly HashSet<string> trackedSymbols = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> TrackedSymbols => trackedSymbols.OrderBy(symbol => symbol, StringComparer.Ordinal).ToArray();

        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trackedSymbols.Add(MarketDataSymbolNormalizer.Normalize(symbol));
            return ValueTask.CompletedTask;
        }

        public async ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
        {
            foreach (var symbol in symbols)
            {
                await TrackSymbolAsync(symbol, cancellationToken);
            }
        }

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<MarketPriceSnapshot?>(null);
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(null);
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }
}
