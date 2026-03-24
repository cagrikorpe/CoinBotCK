using CoinBot.Application.Abstractions.Monitoring;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Monitoring;

public sealed class MonitoringTelemetryCollector(
    TimeProvider timeProvider,
    ILogger<MonitoringTelemetryCollector> logger) : IMonitoringTelemetryCollector
{
    private readonly object syncRoot = new();
    private DateTime? binancePingObservedAtUtc;
    private int? binancePingMs;
    private int? rateLimitUsage;
    private DateTime? webSocketLastMessageAtUtc;
    private int? webSocketLastMessageAgeSeconds;
    private int? webSocketStaleDurationSeconds;
    private int webSocketReconnectCount;
    private int webSocketStreamGapCount;
    private DateTime? databaseLatencyObservedAtUtc;
    private int? databaseLatencyMs;
    private DateTime? redisLatencyObservedAtUtc;
    private int? redisLatencyMs;
    private int signalRActiveConnectionCount;

    public void RecordBinancePing(TimeSpan latency, int? rateLimitUsage = null, DateTime? observedAtUtc = null)
    {
        lock (syncRoot)
        {
            binancePingObservedAtUtc = NormalizeTimestamp(observedAtUtc ?? timeProvider.GetUtcNow().UtcDateTime);
            binancePingMs = ToMilliseconds(latency.TotalMilliseconds);
            this.rateLimitUsage = rateLimitUsage;
        }

        logger.LogDebug(
            "Recorded Binance ping latency {LatencyMs}ms and rate limit usage {RateLimitUsage}.",
            binancePingMs,
            rateLimitUsage?.ToString() ?? "n/a");
    }

    public void RecordWebSocketActivity(
        DateTime lastMessageAtUtc,
        int reconnectCount,
        int streamGapCount,
        int? lastMessageAgeSeconds = null,
        int? staleDurationSeconds = null)
    {
        lock (syncRoot)
        {
            webSocketLastMessageAtUtc = NormalizeTimestamp(lastMessageAtUtc);
            webSocketReconnectCount = Math.Max(0, reconnectCount);
            webSocketStreamGapCount = Math.Max(0, streamGapCount);
            webSocketLastMessageAgeSeconds = lastMessageAgeSeconds;
            webSocketStaleDurationSeconds = staleDurationSeconds;
        }
    }

    public void RecordSignalRConnectionCount(int activeConnectionCount, DateTime? observedAtUtc = null)
    {
        _ = observedAtUtc;

        lock (syncRoot)
        {
            signalRActiveConnectionCount = Math.Max(0, activeConnectionCount);
        }
    }

    public void AdjustSignalRConnectionCount(int delta, DateTime? observedAtUtc = null)
    {
        _ = observedAtUtc;

        lock (syncRoot)
        {
            signalRActiveConnectionCount = Math.Max(0, signalRActiveConnectionCount + delta);
        }
    }

    public void RecordDatabaseLatency(TimeSpan latency, DateTime? observedAtUtc = null)
    {
        lock (syncRoot)
        {
            databaseLatencyObservedAtUtc = NormalizeTimestamp(observedAtUtc ?? timeProvider.GetUtcNow().UtcDateTime);
            databaseLatencyMs = ToMilliseconds(latency.TotalMilliseconds);
        }
    }

    public void RecordRedisLatency(TimeSpan? latency, DateTime? observedAtUtc = null)
    {
        lock (syncRoot)
        {
            redisLatencyObservedAtUtc = NormalizeTimestamp(observedAtUtc ?? timeProvider.GetUtcNow().UtcDateTime);
            redisLatencyMs = latency.HasValue ? ToMilliseconds(latency.Value.TotalMilliseconds) : null;
        }
    }

    public MonitoringTelemetrySnapshot CaptureSnapshot(DateTime? capturedAtUtc = null)
    {
        var capturedAt = NormalizeTimestamp(capturedAtUtc ?? timeProvider.GetUtcNow().UtcDateTime);

        lock (syncRoot)
        {
            var lastMessageAgeSeconds = webSocketLastMessageAtUtc.HasValue
                ? ToSeconds((capturedAt - webSocketLastMessageAtUtc.Value).TotalMilliseconds)
                : webSocketLastMessageAgeSeconds;
            var staleDurationSeconds = webSocketLastMessageAtUtc.HasValue
                ? ToSeconds((capturedAt - webSocketLastMessageAtUtc.Value).TotalMilliseconds)
                : webSocketStaleDurationSeconds;

            return new MonitoringTelemetrySnapshot(
                capturedAt,
                binancePingMs,
                binancePingObservedAtUtc,
                rateLimitUsage,
                webSocketLastMessageAtUtc,
                lastMessageAgeSeconds,
                staleDurationSeconds,
                webSocketReconnectCount,
                webSocketStreamGapCount,
                databaseLatencyMs,
                databaseLatencyObservedAtUtc,
                redisLatencyMs,
                redisLatencyObservedAtUtc,
                signalRActiveConnectionCount);
        }
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

    private static int ToMilliseconds(double value)
    {
        if (value <= 0)
        {
            return 0;
        }

        if (value >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private static int ToSeconds(double value)
    {
        return ToMilliseconds(value) / 1000;
    }
}
