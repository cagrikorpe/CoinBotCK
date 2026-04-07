using System.Net;
using System.Text;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.MarketData;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Exchange;

public sealed class BinanceCredentialProbeClientTests
{
    [Fact]
    public async Task ProbeAsync_DoesNotClassifyAmbiguousBinance2015Message_AsIpRestriction()
    {
        using var spotHandler = new StubHttpMessageHandler(HttpStatusCode.BadRequest, """{"code":-2015,"msg":"Invalid API-key, IP, or permissions for action."}""");
        using var futuresHandler = new StubHttpMessageHandler(HttpStatusCode.BadRequest, """{"code":-2015,"msg":"Invalid API-key, IP, or permissions for action."}""");
        var client = CreateClient(spotHandler, futuresHandler, new FakeTimeSyncService());

        var snapshot = await client.ProbeAsync("api-key", "api-secret");

        Assert.False(snapshot.HasIpRestrictionIssue);
        Assert.Equal("API key, secret veya gerekli izinler doğrulanamadı.", snapshot.SafeFailureReason);
    }

    [Fact]
    public async Task ProbeAsync_ClassifiesExplicitWhitelistMessage_AsIpRestriction()
    {
        using var spotHandler = new StubHttpMessageHandler(HttpStatusCode.BadRequest, """{"code":-2015,"msg":"This request is not in the IP whitelist."}""");
        using var futuresHandler = new StubHttpMessageHandler(HttpStatusCode.BadRequest, """{"code":-2015,"msg":"This request is not in the IP whitelist."}""");
        var client = CreateClient(spotHandler, futuresHandler, new FakeTimeSyncService());

        var snapshot = await client.ProbeAsync("api-key", "api-secret");

        Assert.True(snapshot.HasIpRestrictionIssue);
        Assert.Equal("Binance IP kısıtı nedeniyle doğrulamayı reddetti.", snapshot.SafeFailureReason);
    }

    [Fact]
    public async Task ProbeAsync_RefreshesTimeSync_WhenTimestampSkewIsDetected()
    {
        using var spotHandler = new StubHttpMessageHandler(HttpStatusCode.BadRequest, """{"code":-2015,"msg":"Invalid API-key, IP, or permissions for action."}""");
        using var futuresHandler = new StubHttpMessageHandler(HttpStatusCode.BadRequest, """{"code":-1021,"msg":"Timestamp for this request is outside of the recvWindow."}""");
        var timeSyncService = new FakeTimeSyncService();
        var client = CreateClient(spotHandler, futuresHandler, timeSyncService);

        var snapshot = await client.ProbeAsync("api-key", "api-secret");

        Assert.True(snapshot.HasTimestampSkew);
        Assert.Equal(1, timeSyncService.ForcedRefreshCalls);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.SafeFailureReason));
    }

    [Fact]
    public async Task ProbeAsync_ReportsTimestampSkew_WhenServerTimeOffsetCannotBeProduced()
    {
        using var spotHandler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"canTrade":true,"canWithdraw":true,"permissions":["SPOT"]}""");
        using var futuresHandler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"canTrade":true}""");
        var timeSyncService = new FakeTimeSyncService(
            currentTimestampException: new BinanceClockDriftException("Offset unavailable."));
        var client = CreateClient(spotHandler, futuresHandler, timeSyncService);

        var snapshot = await client.ProbeAsync("api-key", "api-secret");

        Assert.True(snapshot.HasTimestampSkew);
        Assert.False(snapshot.IsKeyValid);
        Assert.Equal("Binance server-time offset üretilemedi.", snapshot.SafeFailureReason);
        Assert.Equal(0, spotHandler.RequestCount);
        Assert.Equal(0, futuresHandler.RequestCount);
    }

    private static BinanceCredentialProbeClient CreateClient(
        StubHttpMessageHandler spotHandler,
        StubHttpMessageHandler futuresHandler,
        IBinanceTimeSyncService timeSyncService)
    {
        return new BinanceCredentialProbeClient(
            new StubHttpClientFactory(
                new HttpClient(spotHandler) { BaseAddress = new Uri("https://testnet.binance.vision") },
                new HttpClient(futuresHandler) { BaseAddress = new Uri("https://testnet.binancefuture.com") }),
            Options.Create(new BinanceMarketDataOptions
            {
                RestBaseUrl = "https://testnet.binance.vision",
                WebSocketBaseUrl = "wss://stream.testnet.binance.vision"
            }),
            Options.Create(new BinancePrivateDataOptions
            {
                RestBaseUrl = "https://testnet.binancefuture.com",
                WebSocketBaseUrl = "wss://fstream.binancefuture.com"
            }),
            TimeProvider.System,
            timeSyncService);
    }

    private sealed class StubHttpClientFactory(HttpClient spotClient, HttpClient futuresClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => name switch
        {
            "BinanceCredentialProbeSpot" => spotClient,
            "BinanceCredentialProbeFutures" => futuresClient,
            _ => throw new InvalidOperationException($"Unexpected client name '{name}'.")
        };
    }

    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FakeTimeSyncService(Exception? currentTimestampException = null) : IBinanceTimeSyncService
    {
        public int ForcedRefreshCalls { get; private set; }

        public Task<BinanceTimeSyncSnapshot> GetSnapshotAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            if (forceRefresh)
            {
                ForcedRefreshCalls++;
            }

            return Task.FromResult(new BinanceTimeSyncSnapshot(
                DateTime.UtcNow,
                DateTime.UtcNow,
                0,
                12,
                DateTime.UtcNow,
                "Synchronized",
                null));
        }

        public Task<long> GetCurrentTimestampMillisecondsAsync(CancellationToken cancellationToken = default)
        {
            if (currentTimestampException is not null)
            {
                return Task.FromException<long>(currentTimestampException);
            }

            return Task.FromResult(1_710_000_000_000L);
        }
    }
}
