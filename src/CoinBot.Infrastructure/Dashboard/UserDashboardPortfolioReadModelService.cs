using CoinBot.Application.Abstractions.Dashboard;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Dashboard;

public sealed class UserDashboardPortfolioReadModelService(
    ApplicationDbContext dbContext) : IUserDashboardPortfolioReadModelService
{
    public async Task<UserDashboardPortfolioSnapshot> GetSnapshotAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = NormalizeRequired(userId, nameof(userId));
        var activeAccounts = await dbContext.ExchangeAccounts
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == normalizedUserId &&
                !entity.IsDeleted &&
                entity.ExchangeName == "Binance" &&
                entity.CredentialStatus == ExchangeCredentialStatus.Active)
            .Select(entity => entity.Id)
            .ToListAsync(cancellationToken);

        if (activeAccounts.Count == 0)
        {
            return new UserDashboardPortfolioSnapshot(
                ActiveAccountCount: 0,
                SyncStatusLabel: "Aktif Binance hesabi yok",
                SyncStatusTone: "neutral",
                LastSynchronizedAtUtc: null,
                Balances: Array.Empty<UserDashboardBalanceSnapshot>(),
                Positions: Array.Empty<UserDashboardPositionSnapshot>());
        }

        var balances = await dbContext.ExchangeBalances
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == normalizedUserId &&
                activeAccounts.Contains(entity.ExchangeAccountId) &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.SyncedAtUtc)
            .ThenBy(entity => entity.Asset)
            .Select(entity => new UserDashboardBalanceSnapshot(
                entity.Asset,
                entity.WalletBalance,
                entity.CrossWalletBalance,
                entity.AvailableBalance,
                entity.MaxWithdrawAmount,
                entity.ExchangeUpdatedAtUtc,
                entity.SyncedAtUtc))
            .ToListAsync(cancellationToken);

        var positions = await dbContext.ExchangePositions
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == normalizedUserId &&
                activeAccounts.Contains(entity.ExchangeAccountId) &&
                !entity.IsDeleted)
            .OrderByDescending(entity => Math.Abs(entity.UnrealizedProfit))
            .ThenBy(entity => entity.Symbol)
            .Select(entity => new UserDashboardPositionSnapshot(
                entity.Symbol,
                entity.PositionSide,
                entity.Quantity,
                entity.EntryPrice,
                entity.BreakEvenPrice,
                entity.UnrealizedProfit,
                entity.MarginType,
                entity.IsolatedWallet,
                entity.ExchangeUpdatedAtUtc,
                entity.SyncedAtUtc))
            .ToListAsync(cancellationToken);

        var syncStates = await dbContext.ExchangeAccountSyncStates
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == normalizedUserId &&
                activeAccounts.Contains(entity.ExchangeAccountId) &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.LastPrivateStreamEventAtUtc ?? DateTime.MinValue)
            .ThenByDescending(entity => entity.LastPositionSyncedAtUtc ?? DateTime.MinValue)
            .ThenByDescending(entity => entity.LastBalanceSyncedAtUtc ?? DateTime.MinValue)
            .ToListAsync(cancellationToken);

        var latestSyncAtUtc = syncStates
            .SelectMany(entity => new[]
            {
                entity.LastPrivateStreamEventAtUtc,
                entity.LastBalanceSyncedAtUtc,
                entity.LastPositionSyncedAtUtc,
                entity.LastStateReconciledAtUtc
            })
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty()
            .Max();

        var latestState = syncStates.FirstOrDefault();

        return new UserDashboardPortfolioSnapshot(
            activeAccounts.Count,
            BuildSyncStatusLabel(latestState),
            BuildSyncStatusTone(latestState),
            latestSyncAtUtc == DateTime.MinValue ? null : latestSyncAtUtc,
            balances,
            positions);
    }

    private static string BuildSyncStatusLabel(ExchangeAccountSyncState? syncState)
    {
        if (syncState is null)
        {
            return "Henüz senkron yok";
        }

        return syncState.PrivateStreamConnectionState switch
        {
            ExchangePrivateStreamConnectionState.Connected when syncState.DriftStatus == ExchangeStateDriftStatus.InSync => "Canli senkron bagli",
            ExchangePrivateStreamConnectionState.Connected => "Canli senkron bagli, drift izleniyor",
            ExchangePrivateStreamConnectionState.Reconnecting => "Canli senkron yeniden baglaniyor",
            ExchangePrivateStreamConnectionState.Connecting => "Canli senkron baglaniyor",
            ExchangePrivateStreamConnectionState.ListenKeyExpired => "Listen key yenilemesi gerekiyor",
            _ => "Canli senkron bagli degil"
        };
    }

    private static string BuildSyncStatusTone(ExchangeAccountSyncState? syncState)
    {
        if (syncState is null)
        {
            return "neutral";
        }

        return syncState.PrivateStreamConnectionState switch
        {
            ExchangePrivateStreamConnectionState.Connected when syncState.DriftStatus == ExchangeStateDriftStatus.InSync => "positive",
            ExchangePrivateStreamConnectionState.Connected => "warning",
            ExchangePrivateStreamConnectionState.Reconnecting => "warning",
            ExchangePrivateStreamConnectionState.Connecting => "neutral",
            ExchangePrivateStreamConnectionState.ListenKeyExpired => "negative",
            _ => "negative"
        };
    }

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        return normalizedValue;
    }
}
