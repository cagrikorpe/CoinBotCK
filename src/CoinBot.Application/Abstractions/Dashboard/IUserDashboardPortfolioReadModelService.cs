using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Dashboard;

public interface IUserDashboardPortfolioReadModelService
{
    Task<UserDashboardPortfolioSnapshot> GetSnapshotAsync(
        string userId,
        CancellationToken cancellationToken = default);
}

public sealed record UserDashboardPortfolioSnapshot(
    int ActiveAccountCount,
    string SyncStatusLabel,
    string SyncStatusTone,
    DateTime? LastSynchronizedAtUtc,
    decimal RealizedPnl,
    decimal UnrealizedPnl,
    decimal TotalPnl,
    string PnlConsistencySummary,
    IReadOnlyCollection<UserDashboardBalanceSnapshot> Balances,
    IReadOnlyCollection<UserDashboardPositionSnapshot> Positions,
    IReadOnlyCollection<UserDashboardTradeHistoryRowSnapshot> TradeHistory,
    IReadOnlyCollection<UserDashboardSpotHoldingSnapshot>? SpotHoldings = null);

public sealed record UserDashboardBalanceSnapshot(
    string Asset,
    decimal WalletBalance,
    decimal CrossWalletBalance,
    decimal? AvailableBalance,
    decimal? MaxWithdrawAmount,
    DateTime ExchangeUpdatedAtUtc,
    DateTime SyncedAtUtc,
    decimal? LockedBalance = null,
    ExchangeDataPlane Plane = ExchangeDataPlane.Futures,
    Guid? ExchangeAccountId = null);

public sealed record UserDashboardPositionSnapshot(
    string Symbol,
    string PositionSide,
    decimal Quantity,
    decimal EntryPrice,
    decimal BreakEvenPrice,
    decimal UnrealizedProfit,
    string MarginType,
    decimal IsolatedWallet,
    DateTime ExchangeUpdatedAtUtc,
    DateTime SyncedAtUtc,
    ExchangeDataPlane Plane = ExchangeDataPlane.Futures,
    decimal? RealizedPnl = null,
    decimal? CostBasis = null,
    decimal? MarkPrice = null,
    decimal? AvailableQuantity = null,
    decimal? LockedQuantity = null);

public sealed record UserDashboardTradeHistoryRowSnapshot(
    Guid OrderId,
    string? ClientOrderId,
    string CorrelationId,
    string Symbol,
    string Timeframe,
    string Side,
    decimal Quantity,
    decimal? AverageFillPrice,
    decimal RealizedPnl,
    decimal? UnrealizedPnlContribution,
    decimal? FeeAmountInQuote,
    decimal? CostImpact,
    DateTime OpenedAtUtc,
    DateTime? ClosedAtUtc,
    DateTime LastUpdatedAtUtc,
    string FinalState,
    string ExecutionResultCategory,
    string ExecutionResultCode,
    string ExecutionResultSummary,
    string RejectionStage,
    bool SubmittedToBroker,
    bool RetryEligible,
    bool CooldownApplied,
    string ReasonChainSummary,
    bool AiScoreAvailable,
    int? AiScoreValue,
    string AiScoreLabel,
    string AiScoreSummary,
    string AiScoreSource,
    DateTime? AiScoreGeneratedAtUtc,
    bool AiScoreIsPlaceholder,
    ExchangeDataPlane Plane = ExchangeDataPlane.Futures,
    decimal? FilledQuantity = null,
    decimal? CumulativeQuoteQuantity = null,
    int FillCount = 0,
    string? TradeIdsSummary = null);

public sealed record UserDashboardSpotHoldingSnapshot(
    Guid ExchangeAccountId,
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    decimal Quantity,
    decimal AvailableQuantity,
    decimal LockedQuantity,
    decimal AverageCost,
    decimal CostBasis,
    decimal RealizedPnl,
    decimal UnrealizedPnl,
    decimal TotalFeesInQuote,
    decimal? MarkPrice,
    DateTime LastTradeAtUtc,
    DateTime? LastMarkPriceAtUtc,
    ExchangeDataPlane Plane = ExchangeDataPlane.Spot);
