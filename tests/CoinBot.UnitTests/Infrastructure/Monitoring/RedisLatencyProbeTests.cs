using System.Net;
using System.Net.Sockets;
using System.Text;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Infrastructure.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoinBot.UnitTests.Infrastructure.Monitoring;

public sealed class RedisLatencyProbeTests
{
    [Fact]
    public async Task ProbeAsync_ReturnsLatency_WhenRedisServerRepliesToPing()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = RunFakeRedisServerAsync(listener, CancellationToken.None);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Redis"] = $"127.0.0.1:{port}"
            })
            .Build();

        var probe = new RedisLatencyProbe(configuration, NullLogger<RedisLatencyProbe>.Instance);

        var result = await probe.ProbeAsync();

        Assert.Equal(RedisProbeStatus.Succeeded, result.Status);
        Assert.Equal($"127.0.0.1:{port}", result.Endpoint);
        Assert.NotNull(result.Latency);

        listener.Stop();
        await serverTask;
    }

    [Fact]
    public async Task ProbeAsync_ReturnsNotConfigured_WhenConnectionStringIsMissing()
    {
        var configuration = new ConfigurationBuilder().Build();
        var probe = new RedisLatencyProbe(configuration, NullLogger<RedisLatencyProbe>.Instance);

        var result = await probe.ProbeAsync();

        Assert.Equal(RedisProbeStatus.NotConfigured, result.Status);
        Assert.Null(result.Latency);
        Assert.Null(result.Endpoint);
    }

    [Fact]
    public async Task ProbeAsync_ReturnsFailed_WhenRedisServerIsUnavailable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Redis"] = $"127.0.0.1:{port}"
            })
            .Build();

        var probe = new RedisLatencyProbe(configuration, NullLogger<RedisLatencyProbe>.Instance);

        var result = await probe.ProbeAsync();

        Assert.Equal(RedisProbeStatus.Failed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureCode));
    }

    private static async Task RunFakeRedisServerAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();

        var command = await ReadRedisCommandAsync(stream, cancellationToken);
        Assert.Equal("PING", command[0]);

        await stream.WriteAsync(Encoding.ASCII.GetBytes("+PONG\r\n"), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> ReadRedisCommandAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = await ReadByteAsync(stream, cancellationToken);
        if (prefix != '*')
        {
            throw new InvalidOperationException("Invalid Redis command frame.");
        }

        if (!int.TryParse(await ReadLineAsync(stream, cancellationToken), out var count) || count <= 0)
        {
            throw new InvalidOperationException("Invalid Redis command length.");
        }

        var parts = new List<string>(count);

        for (var index = 0; index < count; index++)
        {
            var partPrefix = await ReadByteAsync(stream, cancellationToken);
            if (partPrefix != '$')
            {
                throw new InvalidOperationException("Invalid Redis bulk string frame.");
            }

            if (!int.TryParse(await ReadLineAsync(stream, cancellationToken), out var length) || length < 0)
            {
                throw new InvalidOperationException("Invalid Redis bulk string length.");
            }

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

            var cr = await ReadByteAsync(stream, cancellationToken);
            var lf = await ReadByteAsync(stream, cancellationToken);
            if (cr != '\r' || lf != '\n')
            {
                throw new InvalidOperationException("Invalid Redis command terminator.");
            }

            parts.Add(Encoding.UTF8.GetString(buffer));
        }

        return parts;
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(32);

        while (true)
        {
            var next = await ReadByteAsync(stream, cancellationToken);
            if (next == '\r')
            {
                var lf = await ReadByteAsync(stream, cancellationToken);
                if (lf != '\n')
                {
                    throw new InvalidOperationException("Invalid Redis line terminator.");
                }

                break;
            }

            bytes.Add((byte)next);
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
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
}
