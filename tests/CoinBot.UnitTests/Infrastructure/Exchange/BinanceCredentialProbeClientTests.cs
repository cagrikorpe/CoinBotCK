using System.Net;
using System.Text;
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
        var client = CreateClient(spotHandler, futuresHandler);

        var snapshot = await client.ProbeAsync("api-key", "api-secret");

        Assert.False(snapshot.HasIpRestrictionIssue);
        Assert.Equal("API key, secret veya gerekli izinler doğrulanamadı.", snapshot.SafeFailureReason);
    }

    [Fact]
    public async Task ProbeAsync_ClassifiesExplicitWhitelistMessage_AsIpRestriction()
    {
        using var spotHandler = new StubHttpMessageHandler(HttpStatusCode.BadRequest, """{"code":-2015,"msg":"This request is not in the IP whitelist."}""");
        using var futuresHandler = new StubHttpMessageHandler(HttpStatusCode.BadRequest, """{"code":-2015,"msg":"This request is not in the IP whitelist."}""");
        var client = CreateClient(spotHandler, futuresHandler);

        var snapshot = await client.ProbeAsync("api-key", "api-secret");

        Assert.True(snapshot.HasIpRestrictionIssue);
        Assert.Equal("Binance IP kısıtı nedeniyle doğrulamayı reddetti.", snapshot.SafeFailureReason);
    }

    private static BinanceCredentialProbeClient CreateClient(HttpMessageHandler spotHandler, HttpMessageHandler futuresHandler)
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
            TimeProvider.System);
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
