using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.MarketData;

internal sealed class RedisSharedMarketDataCache(
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<RedisSharedMarketDataCache> logger,
    ISharedMarketDataCacheObservabilityCollector? cacheObservabilityCollector = null) : ISharedMarketDataCache
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ISharedMarketDataCacheObservabilityCollector cacheObservabilityCollector =
        cacheObservabilityCollector ?? SharedMarketDataCacheObservabilityCollector.NoOp;

    public async ValueTask<SharedMarketDataCacheWriteResult> WriteAsync<TPayload>(
        SharedMarketDataCacheEntry<TPayload> entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!TryValidateWriteEntry(entry, out var normalizedEntry, out var invalidReason))
        {
            return SharedMarketDataCacheWriteResult.InvalidPayload(invalidReason);
        }

        var connectionString = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString) ||
            !TryParseConnectionDetails(connectionString, out var details))
        {
            return SharedMarketDataCacheWriteResult.ProviderUnavailable("Redis connection string is missing or invalid.");
        }

        var key = SharedMarketDataCacheKeyBuilder.Build(
            normalizedEntry.DataType,
            normalizedEntry.Symbol,
            normalizedEntry.Timeframe);

        string payloadJson;
        try
        {
            payloadJson = JsonSerializer.Serialize(normalizedEntry, JsonSerializerOptions);
        }
        catch (Exception exception)
        {
            return SharedMarketDataCacheWriteResult.SerializeFailed(SanitizeMessage(exception.Message));
        }

        var ttlMilliseconds = Math.Max(
            1L,
            (long)Math.Ceiling((normalizedEntry.ExpiresAtUtc - normalizedEntry.CachedAtUtc).TotalMilliseconds));

        try
        {
            var response = await ExecuteAsync(
                details,
                ["SET", key, payloadJson, "PX", ttlMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                cancellationToken);

            return string.Equals(response, "OK", StringComparison.OrdinalIgnoreCase)
                ? SharedMarketDataCacheWriteResult.Written()
                : SharedMarketDataCacheWriteResult.ProviderUnavailable($"Unexpected Redis write response '{SanitizeMessage(response)}'.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Redis shared market-data cache write failed for {Endpoint}.", details.EndpointDisplay);
            return SharedMarketDataCacheWriteResult.ProviderUnavailable(SanitizeMessage(exception.Message));
        }
    }

    public async ValueTask<SharedMarketDataCacheReadResult<TPayload>> ReadAsync<TPayload>(
        SharedMarketDataCacheDataType dataType,
        string symbol,
        string? timeframe,
        CancellationToken cancellationToken = default)
    {
        var connectionString = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString) ||
            !TryParseConnectionDetails(connectionString, out var details))
        {
            return RecordReadAndReturn(
                dataType,
                symbol,
                timeframe,
                SharedMarketDataCacheReadResult<TPayload>.ProviderUnavailable("Redis connection string is missing or invalid."));
        }

        string key;
        try
        {
            key = SharedMarketDataCacheKeyBuilder.Build(dataType, symbol, timeframe);
        }
        catch (ArgumentException exception)
        {
            return RecordReadAndReturn(
                dataType,
                symbol,
                timeframe,
                SharedMarketDataCacheReadResult<TPayload>.InvalidPayload(SanitizeMessage(exception.Message)));
        }

        string? payloadJson;
        try
        {
            payloadJson = await ExecuteAsync(details, ["GET", key], cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Redis shared market-data cache read failed for {Endpoint}.", details.EndpointDisplay);
            return RecordReadAndReturn(
                dataType,
                symbol,
                timeframe,
                SharedMarketDataCacheReadResult<TPayload>.ProviderUnavailable(SanitizeMessage(exception.Message)));
        }

        if (payloadJson is null)
        {
            return RecordReadAndReturn(
                dataType,
                symbol,
                timeframe,
                SharedMarketDataCacheReadResult<TPayload>.Miss($"Cache key {key} was not found."));
        }

        SharedMarketDataCacheEntry<TPayload>? entry;
        try
        {
            entry = JsonSerializer.Deserialize<SharedMarketDataCacheEntry<TPayload>>(payloadJson, JsonSerializerOptions);
        }
        catch (JsonException exception)
        {
            return RecordReadAndReturn(
                dataType,
                symbol,
                timeframe,
                SharedMarketDataCacheReadResult<TPayload>.DeserializeFailed(SanitizeMessage(exception.Message)));
        }

        if (!TryValidateReadEntry(dataType, symbol, timeframe, entry, out var invalidReason))
        {
            return RecordReadAndReturn(
                dataType,
                symbol,
                timeframe,
                SharedMarketDataCacheReadResult<TPayload>.InvalidPayload(invalidReason));
        }

        var utcNow = timeProvider.GetUtcNow().UtcDateTime;

        return RecordReadAndReturn(
            dataType,
            symbol,
            timeframe,
            utcNow <= entry.FreshUntilUtc
            ? SharedMarketDataCacheReadResult<TPayload>.HitFresh(entry)
            : SharedMarketDataCacheReadResult<TPayload>.HitStale(entry));
    }

    private SharedMarketDataCacheReadResult<TPayload> RecordReadAndReturn<TPayload>(
        SharedMarketDataCacheDataType dataType,
        string symbol,
        string? timeframe,
        SharedMarketDataCacheReadResult<TPayload> result)
    {
        cacheObservabilityCollector.RecordRead(dataType, symbol, timeframe, result);
        return result;
    }

    private string? ResolveConnectionString()
    {
        var redisConnectionString = configuration.GetConnectionString("Redis")?.Trim();
        return string.IsNullOrWhiteSpace(redisConnectionString)
            ? null
            : redisConnectionString;
    }

    private static bool TryValidateWriteEntry<TPayload>(
        SharedMarketDataCacheEntry<TPayload> entry,
        out SharedMarketDataCacheEntry<TPayload> normalizedEntry,
        out string? invalidReason)
    {
        normalizedEntry = entry;
        invalidReason = null;

        if (entry.SchemaVersion != 1)
        {
            invalidReason = $"Unsupported schema version {entry.SchemaVersion}.";
            return false;
        }

        if (entry.Payload is null)
        {
            invalidReason = "Payload is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(entry.Source))
        {
            invalidReason = "Source is required.";
            return false;
        }

        try
        {
            normalizedEntry = entry with
            {
                Symbol = MarketDataSymbolNormalizer.Normalize(entry.Symbol),
                Timeframe = SharedMarketDataCacheKeyBuilder.NormalizeTimeframe(entry.DataType, entry.Timeframe),
                UpdatedAtUtc = NormalizeTimestamp(entry.UpdatedAtUtc),
                CachedAtUtc = NormalizeTimestamp(entry.CachedAtUtc),
                FreshUntilUtc = NormalizeTimestamp(entry.FreshUntilUtc),
                ExpiresAtUtc = NormalizeTimestamp(entry.ExpiresAtUtc),
                Source = entry.Source.Trim()
            };
        }
        catch (ArgumentException exception)
        {
            invalidReason = SanitizeMessage(exception.Message);
            return false;
        }

        if (normalizedEntry.FreshUntilUtc < normalizedEntry.UpdatedAtUtc)
        {
            invalidReason = "FreshUntilUtc cannot be earlier than UpdatedAtUtc.";
            return false;
        }

        if (normalizedEntry.ExpiresAtUtc <= normalizedEntry.CachedAtUtc)
        {
            invalidReason = "ExpiresAtUtc must be later than CachedAtUtc.";
            return false;
        }

        return true;
    }

    private static bool TryValidateReadEntry<TPayload>(
        SharedMarketDataCacheDataType expectedType,
        string requestedSymbol,
        string? requestedTimeframe,
        SharedMarketDataCacheEntry<TPayload>? entry,
        out string? invalidReason)
    {
        invalidReason = null;

        if (entry is null)
        {
            invalidReason = "Payload envelope is missing.";
            return false;
        }

        if (entry.DataType != expectedType)
        {
            invalidReason = $"Payload type mismatch. Expected={expectedType}; Actual={entry.DataType}.";
            return false;
        }

        if (!TryValidateWriteEntry(entry, out var normalizedEntry, out invalidReason))
        {
            return false;
        }

        var normalizedRequestedSymbol = MarketDataSymbolNormalizer.Normalize(requestedSymbol);
        var normalizedRequestedTimeframe = SharedMarketDataCacheKeyBuilder.NormalizeTimeframe(expectedType, requestedTimeframe);

        if (!string.Equals(normalizedEntry.Symbol, normalizedRequestedSymbol, StringComparison.Ordinal) ||
            !string.Equals(normalizedEntry.Timeframe, normalizedRequestedTimeframe, StringComparison.Ordinal))
        {
            invalidReason = $"Payload scope mismatch. Expected={normalizedRequestedSymbol}/{normalizedRequestedTimeframe}; Actual={normalizedEntry.Symbol}/{normalizedEntry.Timeframe}.";
            return false;
        }

        return true;
    }

    private static DateTime NormalizeTimestamp(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static string SanitizeMessage(string? value)
    {
        var sanitized = string.IsNullOrWhiteSpace(value)
            ? "Redis shared cache operation failed."
            : value.Trim();

        return sanitized.Length <= 256
            ? sanitized
            : sanitized[..256];
    }

    private static async Task<string?> ExecuteAsync(
        RedisConnectionDetails details,
        string[] parts,
        CancellationToken cancellationToken)
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

            if (!string.Equals(authResponse, "OK", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Redis authentication failed.");
            }
        }

        await WriteCommandAsync(responseStream, timeoutCts.Token, parts);
        return await ReadResponseAsync(responseStream, timeoutCts.Token);
    }

    private static bool TryParseConnectionDetails(string connectionString, out RedisConnectionDetails details)
    {
        if (TryParseUriConnectionDetails(connectionString, out details))
        {
            return true;
        }

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

        var hostToken = tokens[0];
        var hostParts = hostToken.Split(':', 2, StringSplitOptions.TrimEntries);
        if (hostParts.Length == 0 || string.IsNullOrWhiteSpace(hostParts[0]))
        {
            return false;
        }

        var host = hostParts[0];
        var port = 6379;
        var useSsl = false;
        string? username = null;
        string? password = null;
        var timeout = DefaultTimeout;

        if (hostParts.Length == 2 &&
            int.TryParse(hostParts[1], out var parsedPort) &&
            parsedPort > 0)
        {
            port = parsedPort;
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

            if (string.Equals(key, "port", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(value, out parsedPort) &&
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

        details = new RedisConnectionDetails(
            host,
            port,
            useSsl,
            username,
            password,
            timeout,
            useSsl ? $"{host}:{port} (TLS)" : $"{host}:{port}");

        return true;
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

        string? username = null;
        string? password = null;
        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            var parts = Uri.UnescapeDataString(uri.UserInfo).Split(':', 2, StringSplitOptions.TrimEntries);
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

        var useSsl = string.Equals(uri.Scheme, "rediss", StringComparison.OrdinalIgnoreCase);
        var port = uri.Port > 0 ? uri.Port : (useSsl ? 6380 : 6379);

        details = new RedisConnectionDetails(
            uri.Host,
            port,
            useSsl,
            username,
            password,
            DefaultTimeout,
            useSsl ? $"{uri.Host}:{port} (TLS)" : $"{uri.Host}:{port}");

        return true;
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

    private static async Task<string?> ReadResponseAsync(Stream stream, CancellationToken cancellationToken)
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

    private static async Task<string?> ReadBulkStringAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthText = await ReadLineAsync(stream, cancellationToken);
        if (!int.TryParse(lengthText, out var length))
        {
            throw new InvalidOperationException("Redis returned an invalid bulk string length.");
        }

        if (length < 0)
        {
            return null;
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

    private sealed record RedisConnectionDetails(
        string Host,
        int Port,
        bool UseSsl,
        string? Username,
        string? Password,
        TimeSpan Timeout,
        string EndpointDisplay);
}
