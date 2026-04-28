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
    public OperationalExitPnlEvidenceSnapshot ExitPnlEvidence { get; init; } = OperationalExitPnlEvidenceSnapshot.Empty();

    public OperationalPilotConfigEvidenceSnapshot PilotConfigEvidence { get; init; } = OperationalPilotConfigEvidenceSnapshot.Empty();

    public OperationalPrivateSyncEvidenceSnapshot PrivateSyncEvidence { get; init; } = OperationalPrivateSyncEvidenceSnapshot.Empty();

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
            CriticalWarnings: Array.Empty<OperationalWarningSnapshot>())
        {
            ExitPnlEvidence = OperationalExitPnlEvidenceSnapshot.Empty(),
            PilotConfigEvidence = OperationalPilotConfigEvidenceSnapshot.Empty(),
            PrivateSyncEvidence = OperationalPrivateSyncEvidenceSnapshot.Empty()
        };
    }
}

public sealed record OperationalPilotConfigEvidenceSnapshot(
    bool AutoManageAdoptedPositions,
    string ExecutionDispatchMode,
    int AllowedSymbolCount,
    string AllowedSymbolsSummary,
    int AllowedBotIdCount,
    int AllowedUserIdCount,
    int? AllowedExchangeAccountIdCount)
{
    public static OperationalPilotConfigEvidenceSnapshot Empty()
    {
        return new OperationalPilotConfigEvidenceSnapshot(
            AutoManageAdoptedPositions: false,
            ExecutionDispatchMode: "Unknown",
            AllowedSymbolCount: 0,
            AllowedSymbolsSummary: "n/a",
            AllowedBotIdCount: 0,
            AllowedUserIdCount: 0,
            AllowedExchangeAccountIdCount: null);
    }
}

public sealed record OperationalPrivateSyncEvidenceSnapshot(
    string State,
    string Summary,
    string? ExchangeAccountId,
    string Plane,
    string PrivateStreamConnectionState,
    string DriftStatus,
    string DriftSummary,
    int? BalanceMismatches,
    int? PositionMismatches,
    string SnapshotSource,
    DateTime? LastDriftDetectedAtUtc,
    DateTime? LastBalanceSyncedAtUtc,
    DateTime? LastPositionSyncedAtUtc,
    DateTime? LastStateReconciledAtUtc,
    int? SyncAgeSeconds,
    int ConsecutiveStreamFailureCount,
    string? LastErrorCode,
    DateTime? UpdatedDate,
    int InactiveBlockedCredentialAccountCount,
    int PrivatePlaneStaleRejectCount)
{
    public static OperationalPrivateSyncEvidenceSnapshot Empty()
    {
        return new OperationalPrivateSyncEvidenceSnapshot(
            State: "Unknown",
            Summary: "No futures private sync evidence yet.",
            ExchangeAccountId: null,
            Plane: "n/a",
            PrivateStreamConnectionState: "Unknown",
            DriftStatus: "Unknown",
            DriftSummary: "n/a",
            BalanceMismatches: null,
            PositionMismatches: null,
            SnapshotSource: "n/a",
            LastDriftDetectedAtUtc: null,
            LastBalanceSyncedAtUtc: null,
            LastPositionSyncedAtUtc: null,
            LastStateReconciledAtUtc: null,
            SyncAgeSeconds: null,
            ConsecutiveStreamFailureCount: 0,
            LastErrorCode: null,
            UpdatedDate: null,
            InactiveBlockedCredentialAccountCount: 0,
            PrivatePlaneStaleRejectCount: 0);
    }
}

public sealed record OperationalExitPnlEvidenceSnapshot(
    int LastExitCount,
    int ProfitableExitCount,
    int UnprofitableExitBlockedCount,
    int StopLossExitCount,
    int TakeProfitExitCount,
    string? LastExitReason,
    decimal? LastEstimatedPnlQuote,
    decimal? LastEstimatedPnlPct,
    DateTime? LastExitAtUtc,
    string? LastExitSymbol,
    string? LastExitSide,
    bool? LastExitReduceOnly)
{
    public static OperationalExitPnlEvidenceSnapshot Empty()
    {
        return new OperationalExitPnlEvidenceSnapshot(
            LastExitCount: 0,
            ProfitableExitCount: 0,
            UnprofitableExitBlockedCount: 0,
            StopLossExitCount: 0,
            TakeProfitExitCount: 0,
            LastExitReason: null,
            LastEstimatedPnlQuote: null,
            LastEstimatedPnlPct: null,
            LastExitAtUtc: null,
            LastExitSymbol: null,
            LastExitSide: null,
            LastExitReduceOnly: null);
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
