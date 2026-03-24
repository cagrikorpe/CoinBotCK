namespace CoinBot.Application.Abstractions.Monitoring;

public sealed record MonitoringMetricsSnapshot(
    int? BinancePingMs,
    int? WebSocketStaleDurationSeconds,
    int? LastMessageAgeSeconds,
    int? ReconnectCount,
    int? StreamGapCount,
    int? RateLimitUsage,
    int? DbLatencyMs,
    int? RedisLatencyMs,
    int? SignalRActiveConnectionCount,
    DateTime? WorkerLastHeartbeatAtUtc,
    int? ConsecutiveFailureCount,
    int? SnapshotAgeSeconds);
