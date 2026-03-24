namespace CoinBot.Application.Abstractions.Monitoring;

public sealed record MonitoringTelemetrySnapshot(
    DateTime CapturedAtUtc,
    int? BinancePingMs,
    DateTime? BinancePingObservedAtUtc,
    int? RateLimitUsage,
    DateTime? WebSocketLastMessageAtUtc,
    int? WebSocketLastMessageAgeSeconds,
    int? WebSocketStaleDurationSeconds,
    int WebSocketReconnectCount,
    int WebSocketStreamGapCount,
    int? DatabaseLatencyMs,
    DateTime? DatabaseLatencyObservedAtUtc,
    int? RedisLatencyMs,
    DateTime? RedisLatencyObservedAtUtc,
    int SignalRActiveConnectionCount);
