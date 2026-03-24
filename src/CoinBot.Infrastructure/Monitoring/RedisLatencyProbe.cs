using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using CoinBot.Application.Abstractions.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Monitoring;

internal sealed class RedisLatencyProbe(
    IConfiguration configuration,
    ILogger<RedisLatencyProbe> logger) : IRedisLatencyProbe
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public async Task<RedisLatencyProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var connectionString = ResolveConnectionString();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new RedisLatencyProbeResult(
                RedisProbeStatus.NotConfigured,
                null,
                null,
                null);
        }

        if (!TryParseConnectionDetails(connectionString, out var details))
        {
            return new RedisLatencyProbeResult(
                RedisProbeStatus.Failed,
                null,
                null,
                "InvalidConnectionString");
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var tcpClient = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(details.Timeout);

            await tcpClient.ConnectAsync(details.Host, details.Port).WaitAsync(details.Timeout, timeoutCts.Token);

            Stream stream = tcpClient.GetStream();
            if (details.UseSsl)
            {
                var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
                await sslStream.AuthenticateAsClientAsync(details.Host).WaitAsync(details.Timeout, timeoutCts.Token);
                stream = sslStream;
            }

            using var responseStream = stream;

            if (!string.IsNullOrWhiteSpace(details.Password))
            {
                var authParts = string.IsNullOrWhiteSpace(details.Username)
                    ? new[] { "AUTH", details.Password }
                    : new[] { "AUTH", details.Username, details.Password };

                await WriteCommandAsync(responseStream, timeoutCts.Token, authParts);
                var authResponse = await ReadResponseAsync(responseStream, timeoutCts.Token);

                if (!IsOkResponse(authResponse))
                {
                    return Failure(details, "AuthenticationFailed");
                }
            }

            await WriteCommandAsync(responseStream, timeoutCts.Token, ["PING"]);
            var pingResponse = await ReadResponseAsync(responseStream, timeoutCts.Token);

            if (!string.Equals(pingResponse, "PONG", StringComparison.OrdinalIgnoreCase))
            {
                return Failure(details, "UnexpectedResponse");
            }

            stopwatch.Stop();

            return new RedisLatencyProbeResult(
                RedisProbeStatus.Succeeded,
                stopwatch.Elapsed,
                details.EndpointDisplay,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(details, "Timeout");
        }
        catch (TimeoutException)
        {
            return Failure(details, "Timeout");
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Redis latency probe failed for {Endpoint}.",
                details.EndpointDisplay);

            return Failure(details, "ConnectionFailed");
        }
    }

    private static RedisLatencyProbeResult Failure(RedisConnectionDetails details, string failureCode)
    {
        return new RedisLatencyProbeResult(
            RedisProbeStatus.Failed,
            null,
            details.EndpointDisplay,
            failureCode);
    }

    private string? ResolveConnectionString()
    {
        var healthMonitoringRedisConnectionString = configuration["HealthMonitoring:Redis:ConnectionString"]?.Trim();

        if (!string.IsNullOrWhiteSpace(healthMonitoringRedisConnectionString))
        {
            return healthMonitoringRedisConnectionString;
        }

        var redisConnectionString = configuration.GetConnectionString("Redis")?.Trim();

        return string.IsNullOrWhiteSpace(redisConnectionString)
            ? null
            : redisConnectionString;
    }

    private static bool TryParseConnectionDetails(string connectionString, out RedisConnectionDetails details)
    {
        if (TryParseUriConnectionDetails(connectionString, out details))
        {
            return true;
        }

        return TryParseLegacyConnectionDetails(connectionString, out details);
    }

    private static bool TryParseUriConnectionDetails(string connectionString, out RedisConnectionDetails details)
    {
        details = default!;

        if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "redis", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "rediss", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        var useSsl = string.Equals(uri.Scheme, "rediss", StringComparison.OrdinalIgnoreCase);
        var username = default(string);
        var password = default(string);
        var userInfo = uri.UserInfo;

        if (!string.IsNullOrWhiteSpace(userInfo))
        {
            var parts = Uri.UnescapeDataString(userInfo).Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 1)
            {
                password = parts[0];
            }
            else
            {
                username = parts[0];
                password = parts[1];
            }
        }

        var port = uri.Port > 0 ? uri.Port : (useSsl ? 6380 : 6379);

        details = new RedisConnectionDetails(
            Host: uri.Host,
            Port: port,
            UseSsl: useSsl,
            Username: username,
            Password: password,
            Timeout: DefaultTimeout,
            EndpointDisplay: BuildEndpointDisplay(uri.Host, port, useSsl));

        return true;
    }

    private static bool TryParseLegacyConnectionDetails(string connectionString, out RedisConnectionDetails details)
    {
        details = default!;

        var normalized = connectionString.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var tokens = normalized.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        var host = string.Empty;
        var port = 6379;
        var useSsl = false;
        string? username = null;
        string? password = null;
        var timeout = DefaultTimeout;

        var firstToken = tokens[0];
        if (firstToken.Contains("://", StringComparison.Ordinal))
        {
            if (TryParseUriConnectionDetails(firstToken, out details))
            {
                return true;
            }
        }
        else
        {
            var hostPort = firstToken.Split(':', 2, StringSplitOptions.TrimEntries);
            host = hostPort[0];

            if (hostPort.Length == 2 && int.TryParse(hostPort[1], out var parsedPort) && parsedPort > 0)
            {
                port = parsedPort;
            }
        }

        for (var index = 1; index < tokens.Length; index++)
        {
            var token = tokens[index];
            var separatorIndex = token.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = token[..separatorIndex].Trim();
            var value = token[(separatorIndex + 1)..].Trim();

            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (string.Equals(key, "port", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(value, out var parsedPort) &&
                parsedPort > 0)
            {
                port = parsedPort;
            }
            else if (string.Equals(key, "ssl", StringComparison.OrdinalIgnoreCase) &&
                bool.TryParse(value, out var parsedSsl))
            {
                useSsl = parsedSsl;
            }
            else if (string.Equals(key, "password", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "auth", StringComparison.OrdinalIgnoreCase))
            {
                password = value;
            }
            else if (string.Equals(key, "user", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "username", StringComparison.OrdinalIgnoreCase))
            {
                username = value;
            }
            else if (string.Equals(key, "timeout", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(value, out var parsedTimeout) &&
                parsedTimeout > 0)
            {
                timeout = TimeSpan.FromMilliseconds(parsedTimeout);
            }
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        details = new RedisConnectionDetails(
            Host: host,
            Port: port,
            UseSsl: useSsl,
            Username: username,
            Password: password,
            Timeout: timeout,
            EndpointDisplay: BuildEndpointDisplay(host, port, useSsl));

        return true;
    }

    private static string BuildEndpointDisplay(string host, int port, bool useSsl)
    {
        return useSsl
            ? $"{host}:{port} (TLS)"
            : $"{host}:{port}";
    }

    private static async Task WriteCommandAsync(Stream stream, CancellationToken cancellationToken, params string[] parts)
    {
        await WriteAsciiAsync(stream, $"*{parts.Length}\r\n", cancellationToken);

        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            await WriteAsciiAsync(stream, $"${bytes.Length}\r\n", cancellationToken);
            await stream.WriteAsync(bytes, cancellationToken);
            await WriteAsciiAsync(stream, "\r\n", cancellationToken);
        }

        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<string> ReadResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = await ReadByteAsync(stream, cancellationToken);

        return prefix switch
        {
            '+' => await ReadLineAsync(stream, cancellationToken),
            ':' => await ReadLineAsync(stream, cancellationToken),
            '$' => await ReadBulkStringAsync(stream, cancellationToken),
            '-' => throw new InvalidOperationException($"Redis returned error: {await ReadLineAsync(stream, cancellationToken)}"),
            _ => throw new InvalidOperationException($"Unexpected Redis response prefix '{(char)prefix}'.")
        };
    }

    private static async Task<string> ReadBulkStringAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthText = await ReadLineAsync(stream, cancellationToken);
        if (!int.TryParse(lengthText, out var length))
        {
            throw new InvalidOperationException("Redis returned an invalid bulk string length.");
        }

        if (length < 0)
        {
            return string.Empty;
        }

        var buffer = new byte[length];
        var offset = 0;

        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
            if (read == 0)
            {
                throw new IOException("Redis connection closed while reading bulk string.");
            }

            offset += read;
        }

        var cr = await ReadByteAsync(stream, cancellationToken);
        var lf = await ReadByteAsync(stream, cancellationToken);

        if (cr != '\r' || lf != '\n')
        {
            throw new InvalidOperationException("Redis bulk string terminator was invalid.");
        }

        return Encoding.UTF8.GetString(buffer);
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
                    throw new InvalidOperationException("Redis line terminator was invalid.");
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
            throw new IOException("Redis connection closed while reading response.");
        }

        return buffer[0];
    }

    private static async Task WriteAsciiAsync(Stream stream, string value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static bool IsOkResponse(string response)
    {
        return string.Equals(response, "OK", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(response, "PONG", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record RedisConnectionDetails(
        string Host,
        int Port,
        bool UseSsl,
        string? Username,
        string? Password,
        TimeSpan Timeout,
        string EndpointDisplay);
}
