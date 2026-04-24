using System.Net;
using System.Text;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Exchange;

public sealed class BinancePrivateRestClientClockDriftTests
{
    [Fact]
    public async Task PlaceOrderAsync_UsesTimeSyncTimestampInSignedRequest()
    {
        using var handler = new RecordingMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"orderId":"1001","clientOrderId":"cbp0_test"}""", Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://testnet.binancefuture.com") };
        var timeSyncService = new FakeTimeSyncService(currentTimestampMilliseconds: 1_710_000_000_123L);
        var sut = CreateClient(client, timeSyncService);

        var result = await sut.PlaceOrderAsync(CreateRequest());

        Assert.Equal("1001", result.OrderId);
        Assert.NotNull(handler.LastRequestUri);
        Assert.Contains("timestamp=1710000000123", handler.LastRequestUri!, StringComparison.Ordinal);
        Assert.Equal(1, timeSyncService.CurrentTimestampCalls);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task PlaceOrderAsync_ThrowsClockDriftException_WhenExchangeRejectsTimestamp()
    {
        using var handler = new RecordingMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"code":-1021,"msg":"Timestamp for this request is outside of the recvWindow."}""", Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://testnet.binancefuture.com") };
        var timeSyncService = new FakeTimeSyncService(
            currentTimestampMilliseconds: 1_710_000_000_123L,
            refreshedSnapshot: new BinanceTimeSyncSnapshot(
                new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 4, 2, 9, 59, 57, 500, DateTimeKind.Utc),
                -2500,
                30,
                new DateTime(2026, 4, 2, 10, 0, 1, DateTimeKind.Utc),
                "Synchronized",
                null));
        var sut = CreateClient(client, timeSyncService);

        var exception = await Assert.ThrowsAsync<BinanceClockDriftException>(() => sut.PlaceOrderAsync(CreateRequest()));

        Assert.Contains("DriftMs=2500", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, timeSyncService.ForcedRefreshCalls);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task PlaceOrderAsync_ThrowsStableFailureCode_WhenExchangeRejectsInsufficientMargin()
    {
        using var handler = new RecordingMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"code":-2019,"msg":"Margin is insufficient."}""", Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://testnet.binancefuture.com") };
        var timeSyncService = new FakeTimeSyncService(currentTimestampMilliseconds: 1_710_000_000_123L);
        var sut = CreateClient(client, timeSyncService);

        var exception = await Assert.ThrowsAsync<BinanceExchangeRejectedException>(() => sut.PlaceOrderAsync(CreateRequest()));

        Assert.Equal("FuturesMarginInsufficient", exception.FailureCode);
        Assert.Equal("-2019", exception.ExchangeCode);
        Assert.Contains("Margin is insufficient", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
    }
    [Fact]
    public async Task PlaceOrderAsync_FailsClosed_WhenTimeSyncOffsetCannotBeSynchronized()
    {
        using var handler = new RecordingMessageHandler(_ => throw new InvalidOperationException("HTTP request should not be sent when timestamp sync is unavailable."));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://testnet.binancefuture.com") };
        var timeSyncService = new FakeTimeSyncService(
            currentTimestampMilliseconds: 0,
            currentTimestampException: new BinanceClockDriftException("Offset unavailable."));
        var sut = CreateClient(client, timeSyncService);

        await Assert.ThrowsAsync<BinanceClockDriftException>(() => sut.PlaceOrderAsync(CreateRequest()));

        Assert.Equal(1, timeSyncService.CurrentTimestampCalls);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetAccountSnapshotAsync_UsesPositionRiskForFuturesPositionTruth()
    {
        using var handler = new RecordingMessageHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath;
            if (string.Equals(path, "/fapi/v3/account", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"assets":[{"asset":"USDT","walletBalance":"100","crossWalletBalance":"100","availableBalance":"100","maxWithdrawAmount":"100","updateTime":1710000000123}],"positions":[]}
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            if (string.Equals(path, "/fapi/v2/positionRisk", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        [{"symbol":"SOLUSDT","positionSide":"BOTH","positionAmt":"0.600","entryPrice":"84.22","breakEvenPrice":"84.22","unRealizedProfit":"0","marginType":"cross","isolatedWallet":"0","updateTime":1710000000456}]
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://testnet.binancefuture.com") };
        var timeSyncService = new FakeTimeSyncService(currentTimestampMilliseconds: 1_710_000_000_123L);
        var sut = CreateClient(client, timeSyncService);

        var snapshot = await sut.GetAccountSnapshotAsync(
            Guid.NewGuid(),
            "user-1",
            "Binance",
            "api-key",
            "api-secret");

        var position = Assert.Single(snapshot.Positions);
        Assert.Equal("SOLUSDT", position.Symbol);
        Assert.Equal("BOTH", position.PositionSide);
        Assert.Equal(0.600m, position.Quantity);
        Assert.Equal("Binance.PrivateRest.Account+PositionRisk", snapshot.Source);
        Assert.Contains(handler.RequestUris, uri => uri.Contains("/fapi/v3/account?", StringComparison.Ordinal));
        Assert.Contains(handler.RequestUris, uri => uri.Contains("/fapi/v2/positionRisk?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetOrderAsync_WritesMaskedExecutionTrace_WithStableCorrelationMetadata()
    {
        using var provider = BuildTraceProvider();
        using var handler = new RecordingMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"symbol":"BTCUSDT","orderId":"1001","clientOrderId":"cbp0_test","status":"PARTIALLY_FILLED","origQty":"0.05","executedQty":"0.02","cumQuote":"1280.00","updateTime":1710000000123}""",
                Encoding.UTF8,
                "application/json")
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://testnet.binancefuture.com") };
        var timeSyncService = new FakeTimeSyncService(currentTimestampMilliseconds: 1_710_000_000_123L);
        var sut = CreateClient(client, timeSyncService, provider.GetRequiredService<IServiceScopeFactory>());
        var executionOrderId = Guid.NewGuid();

        var snapshot = await sut.GetOrderAsync(
            new BinanceOrderQueryRequest(
                Guid.NewGuid(),
                "BTCUSDT",
                ExchangeOrderId: "1001",
                ClientOrderId: "cbp0_test",
                ApiKey: "api-key",
                ApiSecret: "api-secret",
                CommandId: "cmd-futures-order-query",
                CorrelationId: "corr-futures-order-query",
                ExecutionAttemptId: "attempt-futures-order-query",
                ExecutionOrderId: executionOrderId,
                UserId: "user-futures-order-query"));

        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var trace = await dbContext.ExecutionTraces.SingleAsync();

        Assert.Equal("PARTIALLY_FILLED", snapshot.Status);
        Assert.Equal("/fapi/v1/order", trace.Endpoint);
        Assert.Equal("corr-futures-order-query", trace.CorrelationId);
        Assert.Equal("attempt-futures-order-query", trace.ExecutionAttemptId);
        Assert.Equal(executionOrderId, trace.ExecutionOrderId);
        Assert.Equal("user-futures-order-query", trace.UserId);
        Assert.Contains("***REDACTED***", trace.RequestMasked ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("api-secret", trace.RequestMasked ?? string.Empty, StringComparison.Ordinal);
    }

    private static BinancePrivateRestClient CreateClient(
        HttpClient httpClient,
        IBinanceTimeSyncService timeSyncService,
        IServiceScopeFactory? serviceScopeFactory = null)
    {
        return new BinancePrivateRestClient(
            httpClient,
            Options.Create(new BinancePrivateDataOptions
            {
                RestBaseUrl = "https://testnet.binancefuture.com",
                WebSocketBaseUrl = "wss://fstream.binancefuture.com",
                RecvWindowMilliseconds = 5000
            }),
            new AdjustableTimeProvider(DateTimeOffset.Parse("2026-04-02T10:00:00Z")),
            timeSyncService,
            NullLogger<BinancePrivateRestClient>.Instance,
            serviceScopeFactory: serviceScopeFactory);
    }

    private static ServiceProvider BuildTraceProvider()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICorrelationContextAccessor, CorrelationContextAccessor>();
        services.AddSingleton<IDataScopeContext>(new TestDataScopeContext());
        services.AddSingleton<TimeProvider>(new AdjustableTimeProvider(DateTimeOffset.Parse("2026-04-02T10:00:00Z")));
        services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddScoped<ITraceService, TraceService>();
        return services.BuildServiceProvider();
    }

    private static BinanceOrderPlacementRequest CreateRequest()
    {
        return new BinanceOrderPlacementRequest(
            Guid.NewGuid(),
            "BTCUSDT",
            ExecutionOrderSide.Buy,
            ExecutionOrderType.Market,
            0.001m,
            0m,
            "cbp0_test",
            "api-key",
            "api-secret");
    }

    private sealed class FakeTimeSyncService(
        long currentTimestampMilliseconds,
        BinanceTimeSyncSnapshot? refreshedSnapshot = null,
        Exception? currentTimestampException = null) : IBinanceTimeSyncService
    {
        public int CurrentTimestampCalls { get; private set; }

        public int ForcedRefreshCalls { get; private set; }

        private readonly BinanceTimeSyncSnapshot refreshedSnapshotValue = refreshedSnapshot ?? new BinanceTimeSyncSnapshot(
            new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
            0,
            10,
            new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
            "Synchronized",
            null);

        public Task<BinanceTimeSyncSnapshot> GetSnapshotAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            if (forceRefresh)
            {
                ForcedRefreshCalls++;
            }

            return Task.FromResult(refreshedSnapshotValue);
        }

        public Task<long> GetCurrentTimestampMillisecondsAsync(CancellationToken cancellationToken = default)
        {
            CurrentTimestampCalls++;

            if (currentTimestampException is not null)
            {
                return Task.FromException<long>(currentTimestampException);
            }

            return Task.FromResult(currentTimestampMilliseconds);
        }
    }

    private sealed class RecordingMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly List<string> requestUris = [];

        public string? LastRequestUri { get; private set; }

        public IReadOnlyList<string> RequestUris => requestUris;

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri?.ToString();
            if (LastRequestUri is not null)
            {
                requestUris.Add(LastRequestUri);
            }

            return Task.FromResult(responder(request));
        }
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }
}

