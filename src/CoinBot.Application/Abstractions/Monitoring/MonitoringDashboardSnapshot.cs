using CoinBot.Application.Abstractions.Administration;

namespace CoinBot.Application.Abstractions.Monitoring;

public sealed record MonitoringDashboardSnapshot(
    IReadOnlyCollection<HealthSnapshot> HealthSnapshots,
    IReadOnlyCollection<WorkerHeartbeat> WorkerHeartbeats,
    DateTime LastRefreshedAtUtc)
{
    public MarketScannerDashboardSnapshot MarketScanner { get; init; } = MarketScannerDashboardSnapshot.Empty();

    public SharedMarketDataCacheHealthSnapshot MarketDataCache { get; init; } = SharedMarketDataCacheHealthSnapshot.Empty();

    public UltraDebugLogHealthSnapshot UltraDebugLogHealth { get; init; } = UltraDebugLogHealthSnapshot.Empty();

    public OperationalObservabilitySnapshot OperationalObservability { get; init; } = OperationalObservabilitySnapshot.Empty();

    public static MonitoringDashboardSnapshot Empty(DateTime lastRefreshedAtUtc)
    {
        return new MonitoringDashboardSnapshot(Array.Empty<HealthSnapshot>(), Array.Empty<WorkerHeartbeat>(), lastRefreshedAtUtc)
        {
            MarketScanner = MarketScannerDashboardSnapshot.Empty(),
            MarketDataCache = SharedMarketDataCacheHealthSnapshot.Empty(lastRefreshedAtUtc),
            UltraDebugLogHealth = UltraDebugLogHealthSnapshot.Empty(),
            OperationalObservability = OperationalObservabilitySnapshot.Empty()
        };
    }
}

public sealed record OperationalObservabilitySnapshot(
    string OverallState,
    string SystemHealthState,
    string SystemHealthSummary,
    string WorkerHeartbeatSummary,
    DateTime? LastScannerCycleCompletedAtUtc,
    string? LastScannerCycleSummary,
    string? TopCandidateSymbol,
    int EligibleCandidateCount,
    string? LatestHandoffSummary,
    OperationalExecutionReadinessSnapshot ExecutionReadiness,
    int RecentAiShadowDecisionCount,
    int RecentAiShadowDecisionOutcomeCount,
    decimal AiShadowOutcomeCoveragePercent,
    string AiShadowOutcomeCoverageSummary,
    IReadOnlyCollection<OperationalReasonBucketSnapshot> BlockedReasons,
    IReadOnlyCollection<OperationalReasonBucketSnapshot> NoSubmitReasons,
    string LogSystemState,
    string DiskPressureState,
    string JanitorSummary,
    string ExportSummary,
    IReadOnlyCollection<OperationalWarningSnapshot> CriticalWarnings)
{
    public static OperationalObservabilitySnapshot Empty()
    {
        return new OperationalObservabilitySnapshot(
            OverallState: "Unknown",
            SystemHealthState: "Unknown",
            SystemHealthSummary: "No health snapshot yet.",
            WorkerHeartbeatSummary: "No worker heartbeat yet.",
            LastScannerCycleCompletedAtUtc: null,
            LastScannerCycleSummary: "No scanner cycle yet.",
            TopCandidateSymbol: null,
            EligibleCandidateCount: 0,
            LatestHandoffSummary: "No handoff attempt yet.",
            ExecutionReadiness: OperationalExecutionReadinessSnapshot.Empty(),
            RecentAiShadowDecisionCount: 0,
            RecentAiShadowDecisionOutcomeCount: 0,
            AiShadowOutcomeCoveragePercent: 0m,
            AiShadowOutcomeCoverageSummary: "No recent shadow decisions.",
            BlockedReasons: Array.Empty<OperationalReasonBucketSnapshot>(),
            NoSubmitReasons: Array.Empty<OperationalReasonBucketSnapshot>(),
            LogSystemState: "Unknown",
            DiskPressureState: "Unknown",
            JanitorSummary: "No janitor heartbeat yet.",
            ExportSummary: "Export availability unknown.",
            CriticalWarnings: Array.Empty<OperationalWarningSnapshot>());
    }
}

public sealed record OperationalExecutionReadinessSnapshot(
    string State,
    string Summary,
    string? ReasonCode,
    string? TradeMasterState,
    string? DefaultMode,
    string? GlobalSystemState,
    DateTime? LatestPreparedAtUtc,
    DateTime? LatestBlockedAtUtc,
    string? LatestBlockedReasonCode)
{
    public static OperationalExecutionReadinessSnapshot Empty()
    {
        return new OperationalExecutionReadinessSnapshot(
            State: "Unknown",
            Summary: "Execution readiness snapshot unavailable.",
            ReasonCode: null,
            TradeMasterState: null,
            DefaultMode: null,
            GlobalSystemState: null,
            LatestPreparedAtUtc: null,
            LatestBlockedAtUtc: null,
            LatestBlockedReasonCode: null);
    }
}

public sealed record OperationalReasonBucketSnapshot(string ReasonCode, int Count);

public sealed record OperationalWarningSnapshot(
    string Code,
    string Summary,
    string Tone);
