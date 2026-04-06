using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Infrastructure.MarketData;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.MarketData;

public sealed class RedisSharedMarketDataCacheTests
{
    [Fact]
    public void Build_ReturnsDeterministicNamespacedKeys_WithNormalizedSymbolAndTimeframe()
    {
        var klineKey = SharedMarketDataCacheKeyBuilder.Build(SharedMarketDataCacheDataType.Kline, " btcusdt ", " 1M ");

        Assert.Equal(
            "coinbot:market-data:v1:kline:BTCUSDT:1m",
            klineKey);
        Assert.Equal(
            klineKey,
            SharedMarketDataCacheKeyBuilder.Build(SharedMarketDataCacheDataType.Kline, "BTCUSDT", "1m"));
        Assert.Equal(
            "coinbot:market-data:v1:ticker:BTCUSDT:spot",
            SharedMarketDataCacheKeyBuilder.Build(SharedMarketDataCacheDataType.Ticker, "btcusdt", null));
        Assert.Equal(
            "coinbot:market-data:v1:depth:BTCUSDT:spot",
            SharedMarketDataCacheKeyBuilder.Build(SharedMarketDataCacheDataType.Depth, "BTCUSDT", ""));
        Assert.NotEqual(
            SharedMarketDataCacheKeyBuilder.Build(SharedMarketDataCacheDataType.Ticker, "BTCUSDT", null),
            SharedMarketDataCacheKeyBuilder.Build(SharedMarketDataCacheDataType.Depth, "BTCUSDT", null));
        Assert.NotEqual(
            SharedMarketDataCacheKeyBuilder.Build(SharedMarketDataCacheDataType.Kline, "BTCUSDT", "1m"),
            SharedMarketDataCacheKeyBuilder.Build(SharedMarketDataCacheDataType.Kline, "BTCUSDT", "5m"));
    }

    [Fact]
    public async Task WriteAsync_AndReadAsync_ReturnHitFresh_WithPreservedMetadataAndPayload()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 16, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(nowUtc);
        await using var fakeRedis = await FakeRedisServer.StartAsync();
        var cache = CreateCache(fakeRedis.ConnectionString, timeProvider);

        var writeResult = await cache.WriteAsync(new SharedMarketDataCacheEntry<MarketPriceSnapshot>(
            SharedMarketDataCacheDataType.Ticker,
            "btcusdt",
            null,
            UpdatedAtUtc: nowUtc.UtcDateTime,
            CachedAtUtc: nowUtc.UtcDateTime,
            FreshUntilUtc: nowUtc.UtcDateTime.AddSeconds(15),
            ExpiresAtUtc: nowUtc.UtcDateTime.AddSeconds(60),
            Source: "unit-test",
            Payload: new MarketPriceSnapshot(
                "BTCUSDT",
                64123.45m,
                nowUtc.UtcDateTime.AddSeconds(-1),
                nowUtc.UtcDateTime,
                "unit-test")));

        var readResult = await cache.ReadAsync<MarketPriceSnapshot>(
            SharedMarketDataCacheDataType.Ticker,
            "BTCUSDT",
            null);

        Assert.Equal(SharedMarketDataCacheWriteStatus.Written, writeResult.Status);
        Assert.Equal(SharedMarketDataCacheReadStatus.HitFresh, readResult.Status);
        Assert.Equal("BTCUSDT", readResult.Entry?.Symbol);
        Assert.Equal("spot", readResult.Entry?.Timeframe);
        Assert.Equal(nowUtc.UtcDateTime, readResult.Entry?.UpdatedAtUtc);
        Assert.Equal(nowUtc.UtcDateTime.AddSeconds(15), readResult.Entry?.FreshUntilUtc);
        Assert.Equal("unit-test", readResult.Entry?.Source);
        Assert.Equal(64123.45m, readResult.Entry?.Payload.Price);
    }
    [Fact]
    public async Task WriteAsync_AndReadAsync_UseDevelopmentProcessFallback_WhenRedisConnectionStringIsMissing()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 16, 5, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(nowUtc);
        var cache = CreateCache(null, timeProvider, Environments.Development);

        var writeResult = await cache.WriteAsync(new SharedMarketDataCacheEntry<MarketPriceSnapshot>(
            SharedMarketDataCacheDataType.Ticker,
            "BTCUSDT",
            null,
            UpdatedAtUtc: nowUtc.UtcDateTime,
            CachedAtUtc: nowUtc.UtcDateTime,
            FreshUntilUtc: nowUtc.UtcDateTime.AddSeconds(15),
            ExpiresAtUtc: nowUtc.UtcDateTime.AddSeconds(60),
            Source: "unit-test",
            Payload: new MarketPriceSnapshot(
                "BTCUSDT",
                64200m,
                nowUtc.UtcDateTime.AddSeconds(-1),
                nowUtc.UtcDateTime,
                "unit-test")));

        var readResult = await cache.ReadAsync<MarketPriceSnapshot>(
            SharedMarketDataCacheDataType.Ticker,
            "BTCUSDT",
            null);

        Assert.Equal(SharedMarketDataCacheWriteStatus.Written, writeResult.Status);
        Assert.Equal(SharedMarketDataCacheReadStatus.HitFresh, readResult.Status);
        Assert.Equal("BTCUSDT", readResult.Entry?.Symbol);
        Assert.Equal(64200m, readResult.Entry?.Payload.Price);
    }


    [Fact]
    public async Task ReadAsync_ReturnsHitStaleMissProviderUnavailableDeserializeFailedAndInvalidPayload_Deterministically()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 16, 30, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(nowUtc);
        await using var fakeRedis = await FakeRedisServer.StartAsync();
        var cache = CreateCache(fakeRedis.ConnectionString, timeProvider);

        await cache.WriteAsync(new SharedMarketDataCacheEntry<MarketPriceSnapshot>(
            SharedMarketDataCacheDataType.Ticker,
            "BTCUSDT",
            null,
            UpdatedAtUtc: nowUtc.UtcDateTime.AddMinutes(-2),
            CachedAtUtc: nowUtc.UtcDateTime.AddMinutes(-2),
            FreshUntilUtc: nowUtc.UtcDateTime.AddMinutes(-1),
            ExpiresAtUtc: nowUtc.UtcDateTime.AddMinutes(5),
            Source: "unit-test",
            Payload: new MarketPriceSnapshot(
                "BTCUSDT",
                64000m,
                nowUtc.UtcDateTime.AddMinutes(-2),
                nowUtc.UtcDateTime.AddMinutes(-2),
                "unit-test")));

        fakeRedis.SetRawValue(
            SharedMarketDataCacheKeyBuilder.Build(SharedMarketDataCacheDataType.Ticker, "ETHUSDT", null),
            "{ this is not valid json }");

        fakeRedis.SetRawValue(
            SharedMarketDataCacheKeyBuilder.Build(SharedMarketDataCacheDataType.Depth, "SOLUSDT", null),
            JsonSerializer.Serialize(new SharedMarketDataCacheEntry<string>(
                SharedMarketDataCacheDataType.Depth,
                Symbol: "",
                Timeframe: null,
                UpdatedAtUtc: nowUtc.UtcDateTime,
                CachedAtUtc: nowUtc.UtcDateTime,
                FreshUntilUtc: nowUtc.UtcDateTime.AddSeconds(30),
                ExpiresAtUtc: nowUtc.UtcDateTime.AddMinutes(1),
                Source: "unit-test",
                Payload: "depth")));

        var staleResult = await cache.ReadAsync<MarketPriceSnapshot>(SharedMarketDataCacheDataType.Ticker, "BTCUSDT", null);
        var missResult = await cache.ReadAsync<MarketPriceSnapshot>(SharedMarketDataCacheDataType.Ticker, "BNBUSDT", null);
        var deserializeFailedResult = await cache.ReadAsync<MarketPriceSnapshot>(SharedMarketDataCacheDataType.Ticker, "ETHUSDT", null);
        var invalidPayloadResult = await cache.ReadAsync<string>(SharedMarketDataCacheDataType.Depth, "SOLUSDT", null);
        var providerUnavailableResult = await CreateCache(null, timeProvider)
            .ReadAsync<MarketPriceSnapshot>(SharedMarketDataCacheDataType.Ticker, "BTCUSDT", null);

        Assert.Equal(SharedMarketDataCacheReadStatus.HitStale, staleResult.Status);
        Assert.Equal(SharedMarketDataCacheReadStatus.Miss, missResult.Status);
        Assert.Equal(SharedMarketDataCacheReadStatus.DeserializeFailed, deserializeFailedResult.Status);
        Assert.Equal(SharedMarketDataCacheReadStatus.InvalidPayload, invalidPayloadResult.Status);
        Assert.Equal(SharedMarketDataCacheReadStatus.ProviderUnavailable, providerUnavailableResult.Status);
    }

    [Fact]
    public async Task MarketDataService_DoesNotFallbackToProcessLocalMemory_WhenSharedCacheIsUnavailable()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 17, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(nowUtc);
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
        var service = new MarketDataService(
            symbolRegistry,
            memoryCache,
            cachePolicyProvider,
            new MarketPriceStreamHub(),
            CreateCache(null, timeProvider),
            timeProvider,
            NullLogger<MarketDataService>.Instance);

        await service.RecordPriceAsync(new MarketPriceSnapshot(
            "BTCUSDT",
            64000m,
            nowUtc.UtcDateTime,
            nowUtc.UtcDateTime,
            "unit-test"));

        var latestPrice = await service.GetLatestPriceAsync("BTCUSDT");

        Assert.Null(latestPrice);
    }

    private static RedisSharedMarketDataCache CreateCache(string? redisConnectionString, TimeProvider timeProvider, string? environmentName = null)
    {
        var configurationValues = new Dictionary<string, string?>();

        if (!string.IsNullOrWhiteSpace(redisConnectionString))
        {
            configurationValues["ConnectionStrings:Redis"] = redisConnectionString;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();

        return new RedisSharedMarketDataCache(
            configuration,
            timeProvider,
            NullLogger<RedisSharedMarketDataCache>.Instance,
            environmentName is null ? null : new TestHostEnvironment(environmentName));
    }



    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "CoinBot.UnitTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
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

        public void SetRawValue(string key, string value)
        {
            values[key] = value;
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
            var prefix = await ReadByteAsync(stream, cancellationToken);
            if (prefix != '*')
            {
                throw new InvalidOperationException("Invalid Redis command frame.");
            }

            var countText = await ReadLineAsync(stream, cancellationToken);
            var count = int.Parse(countText, System.Globalization.CultureInfo.InvariantCulture);
            var parts = new List<string>(count);

            for (var index = 0; index < count; index++)
            {
                var bulkPrefix = await ReadByteAsync(stream, cancellationToken);
                if (bulkPrefix != '$')
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
}
