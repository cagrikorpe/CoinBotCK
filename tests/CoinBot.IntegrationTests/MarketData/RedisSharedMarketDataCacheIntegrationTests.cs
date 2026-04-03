using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Infrastructure.MarketData;
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

        await workerService.RecordPriceAsync(new MarketPriceSnapshot(
            "btcusdt",
            65234.12m,
            nowUtc.UtcDateTime.AddSeconds(-1),
            nowUtc.UtcDateTime,
            "Binance.WebSocket.Kline"));

        var latestPrice = await webService.GetLatestPriceAsync("BTCUSDT");
        var rawRead = await CreateCache(fakeRedis.ConnectionString, nowUtc.AddSeconds(5))
            .ReadAsync<MarketPriceSnapshot>(SharedMarketDataCacheDataType.Ticker, "BTCUSDT", null);

        Assert.NotNull(latestPrice);
        Assert.Equal("BTCUSDT", latestPrice!.Symbol);
        Assert.Equal(65234.12m, latestPrice.Price);
        Assert.Equal("Binance.WebSocket.Kline", latestPrice.Source);
        Assert.Equal(SharedMarketDataCacheReadStatus.HitFresh, rawRead.Status);
        Assert.Equal("BTCUSDT", rawRead.Entry?.Symbol);
        Assert.Equal("spot", rawRead.Entry?.Timeframe);
        Assert.Equal(nowUtc.UtcDateTime, rawRead.Entry?.UpdatedAtUtc);
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

    private static RedisSharedMarketDataCache CreateCache(string connectionString, DateTimeOffset nowUtc)
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
            NullLogger<RedisSharedMarketDataCache>.Instance);
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
}
