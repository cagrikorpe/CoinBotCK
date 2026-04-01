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
    IReadOnlyCollection<UserDashboardBalanceSnapshot> Balances,
    IReadOnlyCollection<UserDashboardPositionSnapshot> Positions);

public sealed record UserDashboardBalanceSnapshot(
    string Asset,
    decimal WalletBalance,
    decimal CrossWalletBalance,
    decimal? AvailableBalance,
    decimal? MaxWithdrawAmount,
    DateTime ExchangeUpdatedAtUtc,
    DateTime SyncedAtUtc);

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
    DateTime SyncedAtUtc);
