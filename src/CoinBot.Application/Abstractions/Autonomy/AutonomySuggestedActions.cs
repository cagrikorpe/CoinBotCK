namespace CoinBot.Application.Abstractions.Autonomy;

public static class AutonomySuggestedActions
{
    public const string WebSocketReconnect = "WEBSOCKET_RECONNECT";
    public const string SignalRReconnect = "SIGNALR_RECONNECT";
    public const string WorkerRetry = "WORKER_RETRY";
    public const string CacheRebuild = "CACHE_REBUILD";
    public const string EmergencyFlatten = "EMERGENCY_FLATTEN";
    public const string FinalBlacklist = "FINAL_BLACKLIST";
    public const string UserOverride = "USER_OVERRIDE";
    public const string PolicyChange = "POLICY_CHANGE";
}
