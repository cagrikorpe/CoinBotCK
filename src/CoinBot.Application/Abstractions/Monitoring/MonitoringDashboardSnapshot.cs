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

    public OperationalStrategyProfitQualitySnapshot StrategyProfitQuality { get; init; } = OperationalStrategyProfitQualitySnapshot.Empty();

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
            StrategyProfitQuality = OperationalStrategyProfitQualitySnapshot.Empty(),
            PilotConfigEvidence = OperationalPilotConfigEvidenceSnapshot.Empty(),
            PrivateSyncEvidence = OperationalPrivateSyncEvidenceSnapshot.Empty()
        };
    }
}

public sealed record OperationalStrategyProfitQualitySnapshot(
    int PairedTradeCount,
    int UnpairedEntryCount,
    int UnpairedExitCount,
    int WinCount,
    int LossCount,
    decimal WinRatePercent,
    decimal? AverageProfitQuote,
    decimal? AverageLossQuote,
    decimal? AverageNetPnlQuote,
    decimal? AverageNetPnlPct,
    decimal? MaxFavorableExcursionQuote,
    decimal? MaxAdverseExcursionQuote,
    DateTime? LastClosedTradeAtUtc,
    string Summary,
    IReadOnlyCollection<OperationalStrategyProfitQualityRowSnapshot> StrategySummaries)
{
    public static OperationalStrategyProfitQualitySnapshot Empty()
    {
        return new OperationalStrategyProfitQualitySnapshot(
            PairedTradeCount: 0,
            UnpairedEntryCount: 0,
            UnpairedExitCount: 0,
            WinCount: 0,
            LossCount: 0,
            WinRatePercent: 0m,
            AverageProfitQuote: null,
            AverageLossQuote: null,
            AverageNetPnlQuote: null,
            AverageNetPnlPct: null,
            MaxFavorableExcursionQuote: null,
            MaxAdverseExcursionQuote: null,
            LastClosedTradeAtUtc: null,
            Summary: "No paired strategy profit quality evidence yet.",
            StrategySummaries: Array.Empty<OperationalStrategyProfitQualityRowSnapshot>());
    }
}

public sealed record OperationalStrategyProfitQualityRowSnapshot(
    string StrategyKey,
    string Symbol,
    string EntrySource,
    string ExitSource,
    int PairedTradeCount,
    int WinCount,
    int LossCount,
    decimal WinRatePercent,
    decimal? AverageProfitQuote,
    decimal? AverageLossQuote,
    decimal? AverageNetPnlQuote,
    decimal? AverageNetPnlPct,
    decimal? MaxFavorableExcursionQuote,
    decimal? MaxAdverseExcursionQuote,
    DateTime? LastClosedTradeAtUtc,
    bool UsesEstimatedPricing,
    string Summary);

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
    public int ReverseBlockedUnprofitableCount { get; init; }

    public int TrailingExitCount { get; init; }

    public int ManualCloseCount { get; init; }

    public string LastExitSummary { get; init; } = "No exit evidence in bounded window.";

    public string? LastBlockedExitSummary { get; init; }

    public IReadOnlyCollection<OperationalOrderEvidenceRowSnapshot> RecentEntryOrders { get; init; } = Array.Empty<OperationalOrderEvidenceRowSnapshot>();

    public IReadOnlyCollection<OperationalOrderEvidenceRowSnapshot> RecentExitOrders { get; init; } = Array.Empty<OperationalOrderEvidenceRowSnapshot>();

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
            LastExitReduceOnly: null)
        {
            ReverseBlockedUnprofitableCount = 0,
            TrailingExitCount = 0,
            ManualCloseCount = 0,
            LastExitSummary = "No exit evidence in bounded window.",
            LastBlockedExitSummary = null,
            RecentEntryOrders = Array.Empty<OperationalOrderEvidenceRowSnapshot>(),
            RecentExitOrders = Array.Empty<OperationalOrderEvidenceRowSnapshot>()
        };
    }
}

public sealed record OperationalOrderEvidenceRowSnapshot(
    DateTime ObservedAtUtc,
    string Symbol,
    string SignalType,
    string Side,
    decimal Quantity,
    decimal? Price,
    bool ReduceOnly,
    string State,
    bool SubmittedToBroker,
    string SourceLabel,
    string PolicyDecision,
    string PolicyReason,
    decimal? EstimatedPnlQuote,
    decimal? EstimatedPnlPct,
    string Summary);

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
