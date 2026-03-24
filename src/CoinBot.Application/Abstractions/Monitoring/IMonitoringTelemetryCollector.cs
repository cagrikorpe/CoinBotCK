namespace CoinBot.Application.Abstractions.Monitoring;

public interface IMonitoringTelemetryCollector
{
    void RecordBinancePing(TimeSpan latency, int? rateLimitUsage = null, DateTime? observedAtUtc = null);

    void RecordWebSocketActivity(
        DateTime lastMessageAtUtc,
        int reconnectCount,
        int streamGapCount,
        int? lastMessageAgeSeconds = null,
        int? staleDurationSeconds = null);

    void RecordSignalRConnectionCount(int activeConnectionCount, DateTime? observedAtUtc = null);

    void AdjustSignalRConnectionCount(int delta, DateTime? observedAtUtc = null);

    void RecordDatabaseLatency(TimeSpan latency, DateTime? observedAtUtc = null);

    void RecordRedisLatency(TimeSpan? latency, DateTime? observedAtUtc = null);

    MonitoringTelemetrySnapshot CaptureSnapshot(DateTime? capturedAtUtc = null);
}
