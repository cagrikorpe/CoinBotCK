using System.Collections.Generic;

namespace CoinBot.Web.ViewModels.Home;

public sealed record DashboardViewModel(
    IReadOnlyCollection<DashboardMarketTickerViewModel> MarketTickers,
    string MarketDataHubPath,
    string OperationsHubPath,
    string DisplayTimeZoneId,
    string DisplayTimeZoneJavaScriptId,
    string DisplayTimeZoneLabel,
    List<KpiItemViewModel> Kpis,
    OperationsSummaryViewModel OperationsSummary,
    PerformanceViewModel Performance,
    List<AiFeedItemViewModel> AiFeed,
    List<RecentOrderViewModel> RecentOrders,
    List<OpenPositionViewModel> OpenPositions
);

public record KpiItemViewModel(string Label, string Value, string Help, string Tone, string Tag);

public record AiFeedItemViewModel(
    string Time,
    string Symbol,
    string Timeframe,
    string StrategyDirection,
    string StrategyConfidence,
    string Direction,
    string Confidence,
    string Reason,
    string Tone,
    bool IsFallback,
    bool Veto,
    string FinalAction,
    string Agreement,
    string NoSubmitReason,
    string? HypotheticalBlockReason,
    string? FeatureSnapshotReference,
    string? FeatureSummary,
    string? TopSignalHints);

public record OperationsSummaryViewModel(
    int EnabledBotCount,
    int EnabledSymbolCount,
    int ConflictedSymbolCount,
    string LastJobStatus,
    string? LastJobErrorCode,
    string LastExecutionState,
    string? LastExecutionFailureCode,
    string WorkerHealthLabel,
    string WorkerHealthTone,
    string PrivateStreamHealthLabel,
    string PrivateStreamHealthTone,
    string BreakerLabel,
    string BreakerTone,
    int OpenCircuitBreakerCount,
    string DailyLossSummary,
    string PositionLimitSummary,
    string CooldownSummary,
    string DriftSummary,
    string DriftReason,
    string TradeMasterStatus,
    string TradeMasterTone,
    string TradingModeStatus,
    string TradingModeTone,
    string PilotActivationStatus,
    string PilotActivationTone,
    string MarketReadinessStatus,
    string MarketReadinessTone,
    string MarketReadinessSummary,
    string PrivatePlaneStatus,
    string PrivatePlaneTone,
    string PrivatePlaneSummary,
    string LatestNoTradeStatus,
    string LatestNoTradeTone,
    string? LatestNoTradeCode,
    string LatestNoTradeSummary,
    string LatestRejectStatus,
    string LatestRejectTone,
    string? LatestRejectCode,
    string LatestRejectSummary,
    string? LatestRejectReconciliation);

public record PerformanceViewModel(
    string EquityEstimate,
    string DailyPnl,
    string OpenPositionEffect,
    string ClosedTradeEffect,
    string Summary,
    bool HasSufficientData,
    string EmptyStateMessage,
    List<PerformancePointViewModel> Points);

public record PerformancePointViewModel(string Time, string Equity, string Source);

public record RecentOrderViewModel(
    string Time,
    string Symbol,
    string Side,
    string FinalState,
    string FinalStateTone,
    string FillSummary,
    string PnlSummary,
    string Reconciliation,
    string ResultCode,
    string ResultSummary,
    string ReasonSummary);

public record OpenPositionViewModel(
    string Symbol,
    string Direction,
    string DirectionTone,
    string Leverage,
    string Entry,
    string Current,
    string Pnl,
    string PnlTone,
    string Risk,
    string RiskTone,
    string Updated
);
