using System.Net;
using System.Text;
using CoinBot.Application.Abstractions.Monitoring;
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
                  "pricePrecision": 2,
                  "quantityPrecision": 4,
                  "filters": [
                    { "filterType": "PRICE_FILTER", "tickSize": "0.01000000" },
                    { "filterType": "LOT_SIZE", "minQty": "0.00100000", "stepSize": "0.00010000" },
                    { "filterType": "MIN_NOTIONAL", "notional": "100.00000000" }
                  ]
                }
              ]
            }
            """);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://fapi.binance.com/")
        };
        var telemetryCollector = new RecordingMonitoringTelemetryCollector();
        var client = new BinanceExchangeInfoClient(
            httpClient,
            new AdjustableTimeProvider(now),
            NullLogger<BinanceExchangeInfoClient>.Instance,
            telemetryCollector);

        var snapshots = await client.GetSymbolMetadataAsync(["btcusdt"]);

        var snapshot = Assert.Single(snapshots);

        Assert.Equal("BTCUSDT", snapshot.Symbol);
        Assert.Equal("Binance", snapshot.Exchange);
        Assert.Equal("BTC", snapshot.BaseAsset);
        Assert.Equal("USDT", snapshot.QuoteAsset);
        Assert.Equal(0.01m, snapshot.TickSize);
        Assert.Equal(0.0001m, snapshot.StepSize);
        Assert.Equal(0.001m, snapshot.MinQuantity);
        Assert.Equal(100m, snapshot.MinNotional);
        Assert.Equal(2, snapshot.PricePrecision);
        Assert.Equal(4, snapshot.QuantityPrecision);
        Assert.Equal("TRADING", snapshot.TradingStatus);
        Assert.True(snapshot.IsTradingEnabled);
        Assert.Equal(now.UtcDateTime, snapshot.RefreshedAtUtc);
        Assert.Contains("fapi/v1/exchangeInfo", handler.LastRequestUri, StringComparison.Ordinal);
        Assert.Contains("symbol=BTCUSDT", handler.LastRequestUri, StringComparison.Ordinal);
        Assert.Contains("BTCUSDT", Uri.UnescapeDataString(handler.LastRequestUri), StringComparison.Ordinal);
        Assert.Equal(1, telemetryCollector.BinancePingCount);
        Assert.Equal(17, telemetryCollector.LastRateLimitUsage);
        Assert.NotNull(telemetryCollector.LastBinancePingLatency);
        Assert.Equal(now.UtcDateTime, telemetryCollector.LastObservedAtUtc);
    }

    [Fact]
    public async Task GetSymbolMetadataAsync_FiltersUnexpectedExtraSymbols_FromSingleSymbolExchangeInfoResponse()
    {
        var now = new DateTimeOffset(2026, 4, 6, 18, 0, 0, TimeSpan.Zero);
        var handler = new StubHttpMessageHandler(
            """
            {
              "symbols": [
                {
                  "symbol": "BTCUSDT",
                  "status": "TRADING",
                  "baseAsset": "BTC",
                  "quoteAsset": "USDT",
                  "pricePrecision": 2,
                  "quantityPrecision": 4,
                  "filters": [
                    { "filterType": "PRICE_FILTER", "tickSize": "0.01000000" },
                    { "filterType": "LOT_SIZE", "minQty": "0.00100000", "stepSize": "0.00010000" },
                    { "filterType": "MIN_NOTIONAL", "notional": "100.00000000" }
                  ]
                },
                {
                  "symbol": "ETHUSDT",
                  "status": "TRADING",
                  "baseAsset": "ETH",
                  "quoteAsset": "USDT",
                  "pricePrecision": 2,
                  "quantityPrecision": 4,
                  "filters": [
                    { "filterType": "PRICE_FILTER", "tickSize": "0.01000000" },
                    { "filterType": "LOT_SIZE", "minQty": "0.00100000", "stepSize": "0.00010000" },
                    { "filterType": "MIN_NOTIONAL", "notional": "100.00000000" }
                  ]
                }
              ]
            }
            """);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://testnet.binancefuture.com/")
        };
        var client = new BinanceExchangeInfoClient(
            httpClient,
            new AdjustableTimeProvider(now),
            NullLogger<BinanceExchangeInfoClient>.Instance);

        var snapshots = await client.GetSymbolMetadataAsync(["BTCUSDT"]);

        var snapshot = Assert.Single(snapshots);
        Assert.Equal("BTCUSDT", snapshot.Symbol);
        Assert.DoesNotContain(snapshots, item => item.Symbol == "ETHUSDT");
        Assert.Contains("symbol=BTCUSDT", handler.LastRequestUri, StringComparison.Ordinal);
    }
    [Fact]
    public async Task GetServerTimeUtcAsync_MapsServerTimeToUtcDateTime()
    {
        var serverTime = new DateTimeOffset(2026, 3, 24, 12, 5, 30, TimeSpan.Zero);
        var handler = new StubHttpMessageHandler(
            $"{{\n  \"serverTime\": {serverTime.ToUnixTimeMilliseconds()}\n}}");
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://fapi.binance.com/")
        };
        var client = new BinanceExchangeInfoClient(
            httpClient,
            new AdjustableTimeProvider(serverTime),
            NullLogger<BinanceExchangeInfoClient>.Instance);

        var observedServerTime = await client.GetServerTimeUtcAsync();

        Assert.Equal(serverTime.UtcDateTime, observedServerTime);
        Assert.Contains("fapi/v1/time", handler.LastRequestUri, StringComparison.Ordinal);
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
            response.Headers.TryAddWithoutValidation("X-MBX-USED-WEIGHT-1M", "17");

            return Task.FromResult(response);
        }
    }

    private sealed class RecordingMonitoringTelemetryCollector : IMonitoringTelemetryCollector
    {
        public int BinancePingCount { get; private set; }

        public TimeSpan? LastBinancePingLatency { get; private set; }

        public int? LastRateLimitUsage { get; private set; }

        public DateTime? LastObservedAtUtc { get; private set; }

        public void RecordBinancePing(TimeSpan latency, int? rateLimitUsage = null, DateTime? observedAtUtc = null)
        {
            BinancePingCount++;
            LastBinancePingLatency = latency;
            LastRateLimitUsage = rateLimitUsage;
            LastObservedAtUtc = observedAtUtc;
        }

        public void RecordWebSocketActivity(DateTime lastMessageAtUtc, int reconnectCount, int streamGapCount, int? lastMessageAgeSeconds = null, int? staleDurationSeconds = null)
        {
        }

        public void RecordSignalRConnectionCount(int activeConnectionCount, DateTime? observedAtUtc = null)
        {
        }

        public void AdjustSignalRConnectionCount(int delta, DateTime? observedAtUtc = null)
        {
        }

        public void RecordDatabaseLatency(TimeSpan latency, DateTime? observedAtUtc = null)
        {
        }

        public void RecordRedisLatency(TimeSpan? latency, DateTime? observedAtUtc = null)
        {
        }

        public MonitoringTelemetrySnapshot CaptureSnapshot(DateTime? capturedAtUtc = null)
        {
            return new MonitoringTelemetrySnapshot(
                capturedAtUtc ?? DateTime.UtcNow,
                LastBinancePingLatency is null ? null : (int)Math.Round(LastBinancePingLatency.Value.TotalMilliseconds),
                LastObservedAtUtc,
                LastRateLimitUsage,
                null,
                null,
                null,
                0,
                0,
                null,
                null,
                null,
                null,
                0);
        }
    }
}


